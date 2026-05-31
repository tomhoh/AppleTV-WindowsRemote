# Port plan

This is the staged roadmap for porting the Apple TV protocol layer
from Python (**pyatv**) to C#/.NET and building a WPF tray-app UI on
top. Each phase is a meaningful checkpoint with a verifiable
deliverable.

Source of truth:
[**postlund/pyatv**](https://github.com/postlund/pyatv) (Python).
Secondary reference:
[**tomhoh/AppleTV-MenubarRemote**](https://github.com/tomhoh/AppleTV-MenubarRemote)
(Swift port we already maintain).

---

## Phase 0 — Scaffolding *(done)*

- `dotnet new sln` with three projects under `src/`.
- README, PLAN, LICENSE, .gitignore.
- Repo initialised; first commit.

## Phase 1 — Primitives *(done)*

Pure-data, no-IO building blocks. Each primitive is unit-tested against
pyatv- or `cryptography`-generated fixtures (byte-exact). Commit/file
mapping:

- **TLV8** — `src/AppleTVProtocol/Tlv8.cs` (22 tests). HomeKit-style
  type-length-value encoding.
- **OPACK** — `src/AppleTVProtocol/Opack.cs` (50 tests). Companion's
  binary serialisation.
- **CryptoUtils** — `src/AppleTVProtocol/Crypto.cs` (15 tests):
  - `Crypto.HkdfSha512` — HKDF-SHA512
  - `ChaCha20Poly1305_8ByteNonce` — HAP's 8-byte-nonce AEAD convention
  - `Srp6aClient` — SRP-6a client (3072-bit prime, generator 5,
    SHA-512, "Pair-Setup" identity). Byte-for-byte matches
    pyatv/srptools.
- **Curves** — `src/AppleTVProtocol/Curves.cs` (13 tests) via
  BouncyCastle (pure-managed — keeps single-file publish + MSIX
  simple). Ed25519 sign/verify, X25519 ECDH. .NET 10's BCL still has
  no managed versions of either curve.
- **BigInteger glue** — used inside `Crypto.cs`. .NET's built-in
  `System.Numerics.BigInteger` is enough.

**Deliverable met:** 100+ primitive-level tests pass on Ubuntu + Windows.

## Phase 2 — Discovery + pairing (in progress)

### 2a. mDNS discovery *(done)*

- `src/AppleTVProtocol/Discovery.cs` (9 tests).
- Browses `_companion-link._tcp.local.` via `Makaretu.Dns`.
- `AppleTVDevice` record surfaces name, IPv4/IPv6 address, port, and
  TXT key/value pairs.
- CLI: `atv discover [--seconds N] [--service ...]`.

### 2b. Credential persistence *(done)*

- `src/AppleTVProtocol/Credentials.cs` (10 tests).
- JSON-on-disk under `%APPDATA%\AppleTV-WindowsRemote\credentials\`
  (or `~/.config/...` on macOS/Linux). One file per paired Apple TV.
- `HapCredentials` matches what pair-setup will produce.

### 2c. HAP pair-setup *(done)*

- `src/AppleTVProtocol/CompanionFrame.cs` — 4-byte-header frame
  encode/decode (8 tests).
- `src/AppleTVProtocol/CompanionCipher.cs` — counter-nonce
  ChaCha20-Poly1305 (8 tests, byte-exact vs pyatv).
- `src/AppleTVProtocol/CompanionConnection.cs` — async TCP + read
  loop + optional encryption (10 tests over TcpListener loopback).
- `src/AppleTVProtocol/PairSetup.cs` — M1/M3/M5 builders, M2/M4/M6
  parser, async driver (9 tests, byte-exact envelopes vs pyatv).
- CLI: `atv pair <host> <port> [--name <n>]`, `atv list`, `atv unpair`.

### 2d. HAP pair-verify *(done)*

- `src/AppleTVProtocol/PairVerify.cs` (4 tests).
- 3-frame X25519 ECDH + Ed25519 cross-signature exchange.
- On success, installs `ClientEncrypt-main` / `ServerEncrypt-main`
  session keys on the connection so subsequent E_OPACK frames are
  encrypted.

**Deliverable met:** `atv pair`, `atv list`, `atv unpair` work; pair-verify
runs and switches the connection into encrypted mode (covered by the
end-to-end test against a fake-AppleTV loopback server).

## Phase 3 — Companion session + HID commands *(done)*

- `src/AppleTVProtocol/CompanionSession.cs` (6 tests).
- XID-tracked request/response + fire-and-forget events over E_OPACK.
- `HidCommand` enum (Up/Down/Left/Right/Select/Menu/Home/PlayPause/
  Volume±/Wake/Sleep/Siri/Screensaver/Channel±/Guide/Page±).
- `SendHidAsync(cmd, down)` + `PressAsync(cmd, hold?)` helpers.
- CLI: `atv key <device-id> <host> <port> <button> [--hold-ms N]`.

**Deliverable met:** `atv key <id> <host> <port> home` etc. should
work against a real Apple TV (haven't tested live yet — author has
no TV on the dev network).

## Phase 3 — Companion session + basic commands (1 week)

- **EncryptedFrameTransport** — the post-pair-verify framed encrypted
  transport. Every Companion frame after pair-verify goes through
  ChaCha20-Poly1305 with the session keys.
- **CompanionSession** — wraps the transport, handles the
  `_systemInfo`/`_touchStart`/`_sessionStart` handshake, hosts a
  callback-style API.
- **HID commands** — `Up/Down/Left/Right/Select/Menu/Home/PlayPause/
  VolumeUp/VolumeDown/Wake/Sleep`. Each is one short OPACK message
  with a HID keycode and a "state" (down/up).

**Deliverable:** `atv key home`, `atv key up`, etc. work — the TV
responds.

## Phase 4 — Touch / swipe *(done)*

Touchpad-style gestures, same encrypted-session machinery as HID:

- `TouchAction` enum (Press/Hold/Release/Click).
- `SendTouchAsync(x, y, action)` — fire-and-forget `_hidT` event with
  timestamp + finger count + coords + phase.
- `SwipeAsync(start → end, duration)` — interpolates ~16 ms apart.
- Long-press is already covered by `PressAsync(cmd, hold:)` from Phase 3.
- CLI: `atv swipe <device-id> <host> <port> <up|down|left|right>`.

Click-and-swipe (HID-select held during a touch swipe) is left for
later — it's just `PressAsync(Select, down:true)` + `SwipeAsync(...)`
+ `PressAsync(Select, down:false)`, easy to layer on once needed.

## Phase 5 — App launcher + text input

### 5a. App launcher *(done)*

- `ListAppsAsync()` — bundle id → display-name map (sends
  `FetchLaunchableApplicationsEvent`, walks the nested response).
- `LaunchAppAsync(bundleId)` — sends `_launchApp` request.
- CLI: `atv apps`, `atv launch <bundle-id>`.

### 5b. Remote Text Input *(next)*

- `sendText(text)` / `sendBackspace()` / `sendClearText()` — RTI
  (Remote Text Input) message family. Requires a one-time RTI pairing
  follow-up the first time a text field opens on the TV.

**Deliverable:** `atv text "hello"` works once a text field is open.

## Phase 6 — Now Playing (3 days)

- AirPlay tunnel subscription that delivers now-playing metadata
  pushes from the TV. (`AirPlayEventChannel` in pyatv.)
- `NowPlayingInfo` model: title, artist, album, app, elapsed time,
  duration, playback rate.
- Live elapsed-time extrapolation between pushes.

**Deliverable:** `atv now` shows what's playing; data is also
observable from the WPF UI.

## Phase 7 — WPF tray app (2 weeks)

- `NotifyIcon` (WPF `Hardcodet.NotifyIcon.Wpf` NuGet — well-maintained
  WPF wrapper for the Win32 shell notify icon).
- Popup window that drops down from the tray icon position. Matches
  the Siri-Remote-style aesthetic of `AppleTV-MenubarRemote`
  (aluminum gradient, rounded clickpad, button rows, light/dark).
- Pairing flow UI (PIN entry on first run).
- Right-click context menu with discovered devices, paired/connected
  markers, click-to-switch.
- Now Playing banner at the top of the popup when something's playing.
- App launcher panel (slide-out from left of popup).
- Standalone text-input window when the TV opens a text field.

**Deliverable:** double-clickable `.exe` that runs as a tray app.

## Phase 8 — Distribution (1 week)

- Self-contained single-file publish via
  `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.
- Code-sign (or instruct users to bypass SmartScreen — same dance as
  Gatekeeper on macOS).
- MSI installer via WiX or just a zip with the `.exe`.
- GitHub Release with attached binary.

---

## Out of scope (initially)

- **Multi-device airplay grouping.** pyatv supports this; not
  needed for a tray-app remote.
- **RAOP** (Remote Audio Output Protocol) audio streaming. Not
  needed for a tray-app remote.
- **DACP** legacy protocol for iTunes-era Apple TVs. Companion + MRP
  cover everything from Apple TV HD onwards.

## Estimated total

~9–12 weeks of focused part-time work to ship Phase 8. The protocol
phases (1–6) are most of the time; the WPF UI in Phase 7 should be
mechanical once the protocol layer is solid.

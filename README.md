# AppleTV-WindowsRemote

A native Windows tray app for controlling an Apple TV — pure C#/.NET,
no Python, no Electron, no Node. A port of the Apple TV Companion
protocol (Apple's reverse-engineered remote-control protocol) to
.NET, fronted by a WPF system-tray UI (and a WinUI 3 variant).

## Why this exists

There is no native Windows Apple TV remote app. The options today are:

- **pyatv** (Python) — works on Windows but requires a Python install.
- **node-appletv** (Node.js) — works on Windows but requires Node + npm.
- **atv-desktop-remote** (Electron) — bundles 100+ MB of Chromium for
  what should be a tray-icon-and-popup.
- **AppleTV-MenubarRemote** (Swift) — companion project, macOS-only;
  Swift/SwiftUI doesn't really exist as a Windows UI story.

A native .NET implementation gives you a small, fast, double-clickable
`.exe` with a real Windows tray icon and a WPF popup that looks like a
Windows app — and it can be code-signed for distribution without
needing a separate runtime.

## Companion project

| Project | UI | OS |
|---|---|---|
| [`AppleTV-MenubarRemote`](https://github.com/tomhoh/AppleTV-MenubarRemote) | SwiftUI menu-bar drop-down | macOS |

## Building

```pwsh
# From a Windows machine with .NET 10 SDK installed:
dotnet build
```

The library and CLI build on any platform with .NET 10. The WPF
project only builds on Windows (Microsoft.WindowsDesktop.App).

## Releases

CI is in [`.github/workflows`](.github/workflows):

| Workflow | Trigger | Output |
|---|---|---|
| `ci.yml`      | every push / PR to `main`                      | builds the library + CLI on Ubuntu **and** Windows, builds the WPF app + the WinUI 3 app on Windows, runs all xUnit tests |
| `release.yml` | push of a tag matching `v*.*.*` (or manual run) | publishes per-tag artifacts: self-contained WPF `.exe` (zip), self-contained WinUI 3 app (zip), self-contained CLI (zip), and **two** MSIX packages (WPF + WinUI, distinct package identities so they install side-by-side) + publisher `.cer` |

To cut a release:

```bash
git tag v0.1.0
git push origin v0.1.0
```

…and the workflow attaches all three assets to a new GitHub Release.

> The MSIX is currently signed with a **dev self-signed cert generated
> in CI**, so end-users must trust the included `.cer` before sideloading.
> Real code-signing will be wired in once there's a cert to bind.

## License

MIT.

## Acknowledgements

- [**pyatv**](https://github.com/postlund/pyatv) by Pierre Ståhl — the
  reference implementation this project ports from. The Companion
  protocol behaviour, OPACK encoder, TLV8 framing, SRP-6a pairing
  flow, and related wire formats were all learned from pyatv's source.
- [**mediaremotetv-protocol**](https://github.com/jeanregisser/mediaremotetv-protocol)
  by Jean Regisser — original public spec of the MRP protocol that
  bootstrapped this whole ecosystem.

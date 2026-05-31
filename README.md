# AppleTV-WindowsRemote

A native Windows tray app for controlling an Apple TV — pure C#/.NET,
no Python, no Electron, no Node. A port of the Apple TV Companion /
MRP protocols (Apple's reverse-engineered remote-control protocols)
to .NET, fronted by a WPF system-tray UI.

> **Status: scaffolding only.** No protocol code is implemented yet.
> This repo currently contains only the .NET solution layout, the
> porting plan ([PLAN.md](PLAN.md)), and notes. See the plan for the
> roadmap and source-of-truth references.

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

## Companion projects

This work has two prior siblings on the author's GitHub, both
controlling the same protocol from a different angle:

| Project | UI | Protocol | OS |
|---|---|---|---|
| [`AppleTV-MenubarRemote`](https://github.com/tomhoh/AppleTV-MenubarRemote) | SwiftUI menu-bar drop-down | Pure Swift (native port of pyatv) | macOS |
| [`MenuBarRemote`](https://github.com/tomhoh/MenuBarRemote) | SwiftUI menu-bar drop-down | Bundled Python `helper.py` + pyatv | macOS |
| **AppleTV-WindowsRemote** *(this repo)* | WPF tray icon + popup | C# port (planned) | Windows |

## Plan in one paragraph

Port the protocol implementation from **pyatv** (~30 KLOC of clean,
readable Python with excellent docs) — *not* from the Swift port —
because pyatv has broader feature coverage, better test cases, and
Python is a friendlier source to read than tight crypto-correct Swift.
Build it as a .NET 8/10 class library (`AppleTVProtocol`). Layer a
WPF tray app on top (`AppleTVRemote.Windows`). Include a small console
CLI (`AppleTVRemote.CLI`) for scripting and testing. See
[PLAN.md](PLAN.md) for the phased breakdown.

## Solution layout

```
AppleTV-WindowsRemote/
├── AppleTV-WindowsRemote.sln
├── README.md
├── PLAN.md
├── LICENSE
└── src/
    ├── AppleTVProtocol/        ← C# port of pyatv internals (the heavy lift)
    ├── AppleTVRemote.Windows/  ← WPF tray icon + popup remote UI
    └── AppleTVRemote.CLI/      ← Console scripting tool (atv-equivalent)
```

All three projects target **.NET 10** (LTS at time of authoring).
WPF target is `net10.0-windows`; the library and CLI are
cross-platform `net10.0`.

## Building

```pwsh
# From a Windows machine with .NET 10 SDK installed:
dotnet build
```

The library and CLI build on any platform with .NET 10. The WPF
project only builds on Windows (Microsoft.WindowsDesktop.App).

## License

[MIT](LICENSE). The protocol implementation will follow pyatv's MIT
license — credit goes upstream.

## Acknowledgements

- [**pyatv**](https://github.com/postlund/pyatv) by Pierre Ståhl — the
  reference implementation this project will port from. The Companion
  + MRP protocol behaviour, OPACK encoder, TLV8 framing, SRP-6a
  pairing flow, and AirPlay tunnelling we'll be reproducing are all
  from pyatv's source.
- [**mediaremotetv-protocol**](https://github.com/jeanregisser/mediaremotetv-protocol)
  by Jean Regisser — original public spec of the MRP protocol that
  bootstrapped this whole ecosystem.

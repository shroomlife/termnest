<div align="center">

# 🪺 TermNest

**A cozy nest for your SSH sessions.**

A modern, native session manager for Windows 11.
Tabs, a tree of saved hosts, a real terminal — no `putty.exe` in sight.

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2011-0078d4)](#install)
[![Built with WinUI 3](https://img.shields.io/badge/built%20with-WinUI%203-005BA1)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Release](https://img.shields.io/github/v/release/shroomlife/termnest?label=release)](https://github.com/shroomlife/termnest/releases)

</div>

---

## Why TermNest?

PuTTY-style session managers on Windows still feel like 2008. TermNest is
the small thing I wanted: a clean side-rail of saved hosts, tabs that
actually look like Windows 11 tabs, and a real terminal — built on
[xterm.js] inside [WebView2], with [SSH.NET] doing the protocol work
under the hood.

No embedded `putty.exe`. No MFC bridges. No MDI tab juggling. Just a
small WinUI 3 desktop app that does one thing well.

[xterm.js]: https://xtermjs.org
[WebView2]: https://learn.microsoft.com/microsoft-edge/webview2/
[SSH.NET]: https://github.com/sshnet/SSH.NET

---

## Highlights

- 🪟 **Native WinUI 3** — Mica, Fluent buttons, system theme, Light by
  default with automatic dark switching.
- 🌳 **Tree of sessions** — group hosts in folders, search, create empty
  folders before you fill them.
- 🪶 **xterm.js terminal** — proper ANSI / 256-color rendering, real
  copy & paste, clean fonts.
- 🔐 **SSH.NET transport** — direct SSH protocol, password auth in 1.0,
  host-key prompt on first connect, hard-refusal on fingerprint mismatch.
- 🖱️ **One-click row actions** — single-click on a session copies the
  host to your clipboard. Hover reveals *connect*, *open in WinSCP*,
  *edit*. The row never opens a dialog by accident.
- ↔️ **Resizable side rail** — grab the divider, drag, drop. Width
  persists immediately, not just on close.
- ⚙️ **Settings in a dialog** — gear button in the footer opens a
  centred Settings dialog (PuTTY path, WinSCP path, terminal font size).
  No noisy inline panels.
- 💾 **Layout memory** — window placement, side-rail width, expanded
  folders, and font size all survive a restart.
- 📥 **Import from SuperPuTTY** — point at an existing
  `sessions.xml` and TermNest absorbs the lot.
- ⏳ **Quiet status footer** — every transient message lands bottom-left
  and auto-clears after 5 s of silence so the UI never gets noisy.

---

## Install

The pre-built MSIX is sideload-signed with a self-issued certificate.
You trust the cert in `LocalMachine\TrustedPeople` once; after that,
upgrades run unattended.

### From a GitHub Release (recommended)

1. Grab the latest `TermNest.App_*.cer` and `TermNest.App_*.msix` from
   [Releases](https://github.com/shroomlife/termnest/releases).
2. **One-time:** trust the signing cert in an **Admin PowerShell**:
   ```powershell
   Import-Certificate -FilePath "$HOME\Downloads\TermNest.App_<version>_x64.cer" `
                      -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"
   ```
3. Double-click the `.msix` to install. Find **TermNest** in the
   Start menu when it's done.

Future updates: just download the new `.msix` and double-click — the
trusted cert stays.

### Uninstall

```powershell
Get-AppxPackage *TermNest* | Remove-AppxPackage
```

---

## Build from source

**Prerequisites**

- Windows 11
- .NET 10 SDK
- Windows App SDK 1.8 (pulled in via NuGet)
- PowerShell 7+

**Local build & install**

```powershell
git clone https://github.com/shroomlife/termnest.git
cd termnest

# Debug build (run from VS / dotnet)
dotnet build -p:Platform=x64

# Release MSIX (signed, dropped in dist/)
scripts\build-msix.ps1
scripts\install-msix.ps1
```

The build script generates a self-signed code-signing certificate the
first time it runs (under `Cert:\CurrentUser\My`, subject
`CN=ShroomlifeDev`) and reuses it for every subsequent build. The
matching public `.cer` lands next to the `.msix` in `dist/`.

---

## Releasing

Releases are cut by pushing a `v*` git tag. The
[`release` workflow](.github/workflows/release.yml) does the rest: it
imports the signing cert from `SIGNING_CERTIFICATE_BASE64` /
`SIGNING_CERTIFICATE_PASSWORD` repo secrets, builds the signed MSIX,
and creates a GitHub release with the `.msix` and `.cer` attached plus
auto-generated release notes.

```powershell
# Bump version in src/TermNest.App/Package.appxmanifest first
# (Identity Version="1.0.0.X" — patch field bumps every release).

git tag v1.0.0.3
git push origin v1.0.0.3
```

The CI run produces the same artefacts as `scripts\build-msix.ps1` does
locally — the workflow is just the same dotnet build with a PFX-based
cert instead of the dev cert in `CurrentUser\My`.

---

## Where TermNest stores your data

Sessions, layout settings and trusted SSH host keys live in the standard
MSIX-isolated `LocalState` folder, no manual maintenance needed:

```
%LocalAppData%\Packages\dev.shroomlife.TermNest_<id>\LocalState\
├── sessions.json           ← saved sessions + empty folders
├── known_hosts.json        ← accepted SSH host-key fingerprints
├── layouts/
│   └── default.json        ← side-rail width, window placement, …
├── active-layout           ← marker for the active layout
└── debug.log               ← crash + diagnostic sink
```

`sessions.json` and `known_hosts.json` are human-readable JSON. Atomic
writes mean crashes never leave a corrupt file behind. Passwords are
**never** persisted — they're runtime-only and supplied via the password
prompt on connect.

---

## Project layout

```
src/
  TermNest.App/      WinUI 3 desktop app (UI, MSIX manifest, WebView2 host)
  TermNest.Core/     Session model, SSH transport, layout & known-hosts persistence
scripts/
  build-msix.ps1            Build + sign the MSIX, drop it under dist/
  install-msix.ps1          Trust the cert + Add-AppxPackage
  generate-app-icons.ps1    Regenerate the MSIX logo set from one source
.github/
  workflows/release.yml     Tag-triggered MSIX build + GitHub release
docs/                       Internal notes (release checklist, etc.)
```

`Core` is a plain class library — no UI dependencies — so the session,
layout and transport code stays testable without standing up a WinUI
host.

---

## Roadmap

The 1.0 cut focuses on the SSH happy path. Tracked for later:

- [ ] Public-key SSH auth (Phase 2)
- [ ] Re-introducing a focused ad-hoc Quick-Connect dialog
- [ ] Detachable docks / multi-window
- [ ] SFTP browser tab
- [ ] Saved scripts (re-run a command on connect)
- [ ] Session export to `sessions.json` / `sessions.xml`
- [ ] ARM64 native build
- [ ] Themed terminal palettes (Solarized, Tokyo Night, …)

Open an issue if you want to argue for a different priority order.

---

## Contributing

PRs welcome. Keep changes tightly scoped: one feature or fix per PR, a
clear *why* in the description, and a screenshot or short clip when the
change is visual.

If you spot a security issue, please email
[robin@shroomlife.de](mailto:robin@shroomlife.de) directly instead of
opening a public issue.

---

## Credits

- [WebView2] — for embedding a real browser without the pain.
- [xterm.js] — for being the terminal everyone secretly uses.
- [SSH.NET] — for a managed SSH stack that just works.
- [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) —
  for letting WinUI ship outside the Store.
- The [Iconify](https://icon-sets.iconify.design/) project — for the
  inline action icons used throughout the UI.

---

## License

[MIT](LICENSE) — do whatever, just keep the copyright notice.

<div align="center">

Made with 🍄 by [shroomlife](https://github.com/shroomlife) in Germany.

</div>

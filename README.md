# TermNest

> A modern, native SSH session manager for Windows 11.

TermNest is a small, opinionated home for your SSH connections. Tabs for
sessions, a tree of saved hosts in the side rail, a real terminal powered
by [xterm.js] inside a [WebView2], and a transport built on [SSH.NET] —
no embedded `putty.exe`, no MFC bridges, no MDI tab juggling.

Built with WinUI 3 and packaged as a sideload-friendly MSIX.

[xterm.js]: https://xtermjs.org
[WebView2]: https://learn.microsoft.com/microsoft-edge/webview2/
[SSH.NET]: https://github.com/sshnet/SSH.NET

---

## Features

- **Native xterm.js terminal** — true terminal rendering, real ANSI / 256-color
  support, copy & paste that behaves the way you expect.
- **SSH.NET transport** — direct SSH protocol, no shelling out to PuTTY.
  Password and key-based auth, host-key prompts, 15-second connection timeout.
- **Tabbed multi-session UI** — open as many sessions as you like, each in its
  own tab; the active tab keeps its own pty size and font.
- **Tree-based session library** — group hosts into folders. Search with the
  inline filter. Create / edit / delete inline. Inline icons for *connect*,
  *open in WinSCP*, and *edit* appear on row hover.
- **One-click IP copy** — single-click on a session copies its host to the
  clipboard. Connecting is an explicit action via the connect icon.
- **Layout persistence** — side-rail width, expanded folders, window
  placement and font size all survive a restart.
- **Resizable side rail** — grab the divider, drag, drop. The new width is
  saved immediately, not just on close.
- **Light by default** — clean, fresh aesthetic that doesn't fight the rest
  of Windows 11. Dark mode follows the system theme.

---

## Install

> Pre-built MSIX is sideload-signed with a self-issued certificate. The
> install script trusts that cert in `LocalMachine\TrustedPeople` once;
> after that, upgrades run unattended.

Grab the latest `.msix` from
[Releases](https://github.com/shroomlife/termnest/releases) and run:

```powershell
# from the repo root, after cloning, or against a downloaded msix
scripts\install-msix.ps1
```

The first run will request UAC elevation to add the signing cert to the
trusted store. Subsequent runs are silent.

Find **TermNest** in the Start menu when it's done.

### Uninstall

```powershell
Get-AppxPackage *TermNest* | Remove-AppxPackage
```

---

## Build from source

Prerequisites:

- Windows 11
- .NET 10 SDK
- Windows App SDK 1.8 (pulled in via NuGet)
- PowerShell 7+

```powershell
# clone
git clone https://github.com/shroomlife/termnest.git
cd termnest

# debug build (run from VS / dotnet)
dotnet build -p:Platform=x64

# release MSIX (signed, dropped in dist/)
scripts\build-msix.ps1
scripts\install-msix.ps1
```

The build script generates a self-signed code-signing certificate the first
time you run it (under `Cert:\CurrentUser\My`, subject `CN=ShroomlifeDev`)
and reuses it for every subsequent build.

---

## Project layout

```
src/
  TermNest.App/      WinUI 3 desktop app (UI, MSIX manifest, WebView2 host)
  TermNest.Core/     Session model, SSH transport, layout persistence
scripts/
  build-msix.ps1     Builds + signs the MSIX, drops it under dist/
  install-msix.ps1   Trusts the cert + Add-AppxPackage
  generate-app-icons.ps1   Regenerates the MSIX logo set from one source
docs/                Internal notes (release checklist, etc.)
```

The `Core` project is a plain class library — no UI dependencies — so the
session, layout and transport code stays testable without standing up a
WinUI host.

---

## Roadmap

The 1.0 cut focuses on the SSH happy path. Tracked for later:

- Detachable docks / multi-window
- Session import/export (JSON, sessions.xml)
- Saved scripts (re-run command on connect)
- SFTP browser tab
- ARM64 native build

---

## Contributing

Issues and PRs welcome. Keep changes tightly scoped: one feature or fix per
PR, a clear description of the *why*, and a screenshot or short clip when
the change is visual.

If you spot a security issue, please email
[robin@shroomlife.de](mailto:robin@shroomlife.de) directly instead of opening
a public issue.

---

## License

[MIT](LICENSE) — do whatever, just keep the copyright notice.

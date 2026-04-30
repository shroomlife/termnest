# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

TermNest — native WinUI 3 SSH session manager for Windows 11. Tabs, a tree of saved hosts, and a real terminal built on **WebView2 + xterm.js** (UI surface) and **SSH.NET** (protocol).

- Stack: .NET 10, C#, WinUI 3 (Windows App SDK 1.8), MSIX-packaged.
- Solution: `TermNest.slnx` references two projects:
  - `src/TermNest.App/` — WinUI 3 desktop app (XAML, MSIX manifest, WebView2 host).
  - `src/TermNest.Core/` — pure class library (`net10.0`, no UI deps). Holds session model, SSH transport, layout/known-hosts persistence. Keep it free of WinUI / WinAppSDK references so it stays testable without standing up a XAML host.

## Build / Run / Install

All commands run from the repo root. **PowerShell 7+** is required for the scripts.

```powershell
# Debug build (typical inner loop)
dotnet build -p:Platform=x64

# Release MSIX build, signed with a self-signed sideload cert.
# First run generates the cert under Cert:\CurrentUser\My (CN=ShroomlifeDev)
# and caches the thumbprint to .cert-thumbprint. Output: dist\*.msix
scripts\build-msix.ps1

# Install / upgrade the latest .msix from dist\.
# First run elevates to add the cert to LocalMachine\TrustedPeople; later runs are silent.
scripts\install-msix.ps1

# Uninstall
Get-AppxPackage *TermNest* | Remove-AppxPackage
```

There is **no test project** in this repo yet — do not invent `dotnet test` commands.

The `Publisher` in `Package.appxmanifest` (`CN=ShroomlifeDev`) MUST stay in sync with the cert subject in `scripts/build-msix.ps1`. MSIX install fails with a publisher-mismatch error if these drift.

### Versioning

**Bump the patch component of `Package.appxmanifest` `<Identity Version="MAJOR.MINOR.BUILD.PATCH">` on every change** that produces a new MSIX (`1.0.0.1` → `1.0.0.2` → ...). MSIX upgrades only kick in when the version is strictly higher; without the bump, `Add-AppxPackage` rejects the install or treats it as an in-place re-deploy that doesn't refresh resources reliably. Reserve `BUILD` (third digit) for visible feature drops and `MINOR`/`MAJOR` for larger milestones — the patch field is the boring per-change counter.

## Architecture (the parts that span files)

### Process model

- **Single-instance app**, enforced in `App.OnLaunched` via `AppInstance.FindOrRegisterForKey("TermNest-4")`. The secondary instance calls `AllowSetForegroundWindow(primaryPid)` (Win32) before redirecting activation, otherwise the primary's later `SetForegroundWindow` is silently blocked by the foreground-lock policy. The activation key is bumped (`-4`) when the redirect contract changes — increment if you change activation behavior in a way the previous build won't understand.
- `App.Window` / `App.WindowHandle` / `App.DispatcherQueue` are the canonical accessors — code reaches these instead of trying to recover them via `Window.Current` (which is UWP-only and always null in WinUI 3).
- Crash logs land in `<LocalState>/debug.log` via `TermNest.Core.Diagnostics.DebugLog`. `App.xaml.cs` wires up `UnhandledException`, `AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` to the same sink.

### UI shell (`TermNest.App/Shell/ShellLayout.xaml`)

A single fixed XAML grid hosts everything:

```
+--- ConnectionBar (top) ----------------------------+
| SessionsPanel | Splitter | SessionTabsControl       |
+----------- BottomStrip status bar ------------------+
```

- The side-rail splitter is hand-rolled (`SplitterHandle` + pointer events in `ShellLayout.xaml.cs`) because CommunityToolkit's `GridSplitter` isn't yet pinned to a WinAppSDK 1.8 / net10 build.
- Width changes persist immediately on pointer release (`LayoutStore.SaveAsync`) — not just on window close — so a crash never loses a resize.
- `MainWindow.AppWindow.Changed` forwards every move/size to `ShellLayout.RefreshEmbeddedHostPositions`. The legacy `EmbeddedPuttyHost` reparented PuTTY HWNDs and needs them to track the shell — the modern WebView2 path doesn't, but the plumbing remains for the embedded fallback.

### Terminal pipeline

`TerminalView` (control) is the sole adapter between two **backend session classes** in `TermNest.Core.Sessions` and the `xterm.js` page in `Assets/Terminal/index.html`:

- `ConsoleTerminalSession` — **the active path for every protocol in 1.x**, including SSH. ConPTY-backed; SSH connects shell out to OpenSSH `ssh.exe` and let it handle authentication (password / key / agent), host-key prompts, and known-hosts persistence (`~/.ssh/known_hosts`). Local cmd / PowerShell sessions also flow through here.
- `SshTerminalSession` — SSH.NET-driven, app-level SSH path. **Currently dormant** — `SessionTabsControl.OpenSessionAsync` routes every SSH protocol to `OpenConsoleTabAsync`, so this class plus `KnownHostsStore` and `HostKeyPromptDelegate` are reserved for a future "TermNest does the SSH" mode where the connect / auth / fingerprint UI lives inside the app instead of inside ssh.exe's terminal output. Pumps `ShellStream` bytes through a UTF-8 decoder onto `DataReceived`. Don't delete it: the wiring (`ShellLayout.PromptForHostKeyAsync`, `SessionTabs.HostKeyStore`/`HostKeyPrompt`) and the on-disk `known_hosts.json` schema are intentionally pre-laid for that switch-over.

The WebView2 page is mapped via `SetVirtualHostNameToFolderMapping("termnest.local", ...)` so it loads from a real `https://termnest.local/index.html` origin (CSP `'self'`, fetch, workers all behave). The page communicates via JSON `postMessage`s (`ready` / `data` / `resize` / `painted` / `title` / `log`); the C# side dispatches via `OnWebMessageReceived` and writes back through `host_write(...)` JS calls. Bundled terminal assets live under `src/TermNest.App/Assets/Terminal/` and are copied via `<Content Include="Assets\Terminal\**\*" />`.

### Host-key verification (security-critical)

**Today (1.x):** Host-key checking is delegated to OpenSSH `ssh.exe` because every SSH connect goes through `ConsoleTerminalSession`. The user sees the standard `ssh-keygen`-style fingerprint prompt printed inside the terminal pane and types `yes` / `no`; the accepted fingerprint persists in `%USERPROFILE%\.ssh\known_hosts` (OpenSSH's file, not ours). MITM protection is therefore as strong as OpenSSH's default — `StrictHostKeyChecking ask` policy.

**Reserved for future:** when the SSH.NET path is re-enabled, the contract is enforced by `SshTerminalSession` + `KnownHostsStore` + `HostKeyPromptDelegate`:

1. First connect to a host → prompt user with SHA-256 fingerprint, persist on accept.
2. Subsequent match → silent accept.
3. **Mismatch → refuse without prompting.** Prompting on mismatch trains users to click through MITM warnings; the only way to re-pin is to remove the entry from `known_hosts.json` and reconnect.

`SshTerminalSession` requires a non-null store + delegate. `TerminalView.StartSshAsync` throws if either is missing. Do not "default" them to permissive behavior — refusing the connect is the safe default.

The prompt delegate runs on a background SSH.NET thread — implementations must marshal to the UI dispatcher themselves (see `ShellLayout.PromptForHostKeyAsync`).

### Persistence

All state lives in the MSIX-isolated `ApplicationData.Current.LocalFolder`. There is an **unpackaged fallback** (`Environment.SpecialFolder.LocalApplicationData\TermNest`) for `dotnet run` outside MSIX — most stores are best-effort under that path.

| File | Owner | Notes |
|---|---|---|
| `sessions.json` | `SessionStore` | Two on-disk shapes accepted: new `{sessions:[], folders:[]}` and legacy plain-array. Atomic write via temp+rename. |
| `layouts/<name>.json` + `active-layout` | `LayoutStore` | Window placement, side-rail width, expanded folders, open-session list. |
| `known_hosts.json` | `KnownHostsStore` | SHA-256 fingerprints by host+port. Atomic write. |
| `debug.log` | `DebugLog` | Crash + diagnostic sink. |

`SessionData.Password` is `[JsonIgnore]` — passwords are runtime-only. Never persist plaintext passwords; that was a v3 regression v4 declines to inherit. A future credential service is the right place (Phase 6+).

### Session tree

`SessionTreeNode.BuildTree` reconstructs the folder hierarchy from `SessionData.SessionId` (slash-delimited paths like `Customer/Subfolder/HostName`). Empty folders only survive a reload because `SessionStoreSnapshot.EmptyFolders` tracks them explicitly — the tree alone can't represent a folder with no sessions.

## Project conventions

- **Nullable + ImplicitUsings on** in both csproj files. Treat nullable warnings as bugs.
- **MVVM via `CommunityToolkit.Mvvm`** (`ObservableObject`, `RelayCommand`). The `ViewModels/` folder is currently sparse — most state lives in code-behind for now, view-models will grow as the shell is decomposed.
- **WinUI 3, not UWP.** Common confusions to avoid: `Microsoft.UI.Xaml.*` (not `Windows.UI.Xaml.*`), `DispatcherQueue.TryEnqueue` (not `CoreDispatcher.RunAsync`), `ContentDialog` with `XamlRoot` set (not `MessageDialog`), `WinRT.Interop.InitializeWithWindow` for any picker/dialog that needs an HWND.
- **Set `XamlRoot` on every `ContentDialog`** before calling `ShowAsync` (see `ShellLayout.PromptForHostKeyAsync`). Forgetting this throws at runtime under WinUI 3.
- **TFM is `net10.0-windows10.0.26100.0`** for the App and bare `net10.0` for Core. The qualified TFM in the App is what unlocks WinRT APIs — don't downgrade it to bare `net10.0`.
- Atomic file writes use the `temp + Move(overwrite: true)` pattern. Match that pattern for any new persistence.
- `Resources/active-layout` and `.cert-thumbprint` are runtime/dev artefacts — `.cert-thumbprint` is `.gitignore`d, do not commit it.

## Things to leave alone unless asked

- `installer/` is gitignored — abandoned WiX experiment. The MSIX path in the csproj replaces it.
- `Assets/Terminal/vendor/` holds bundled xterm.js / fonts. Don't hand-edit; if you need to refresh, do it as a deliberate vendoring task.
- App icons are regenerated by `scripts/generate-app-icons.ps1` from a single source — don't edit individual `Square*Logo*.png` files directly.
- `Package.appxmanifest` `<Identity>` (`Name`, `Publisher`, `Version`) is load-bearing — touching `Publisher` invalidates every existing install's signature trust.

# Changelog

All notable changes to TermNest are documented in this file.

The version format is `MAJOR.MINOR.BUILD.PATCH` to match the MSIX `<Identity Version>` shape. The `PATCH` field is bumped on every change that produces a new MSIX so `Add-AppxPackage` recognises an upgrade.

## [1.0.0.4] - 2026-04-30

### Fixed
- **Edit Session / New Session dialog now centres horizontally.** The dialog was pinning `ContentDialogMinWidth` / `ContentDialogMaxWidth` as scoped resources *and* setting `MinWidth` / `MaxWidth` on itself, which forces the WinUI 3 ContentDialog template layer to size against the popup root and ignore `HorizontalAlignment = Center`. The width is now constrained on the inner StackPanel only, matching the Settings dialog pattern.

## [1.0.0.3] - 2026-04-30

### Fixed
- **Keyboard activation in the session tree** — pressing Enter (or Space) on a focused session now opens it. The `TreeView.ItemInvoked` handler was a no-op since 1.0.0.0; sessions could only be opened by mouse before.
- **Clean shell exit no longer reports as "Connection lost"** — `ConsoleTerminalSession` distinguishes a natural EOF (user typed `exit`, ssh.exe finished) from a real read-pipeline failure. Only the latter renders the disconnect overlay now.
- **`TerminalView` start-state lock-up** — `_startInProgress` is now reset in a `finally` block, so a retry on the same view (post-failure) can never silently no-op.

### Changed
- WebView2 `WebMessageReceived` is now unsubscribed in `TerminalView.CloseAsync` for symmetric lifetime with the `OnLoaded` subscription.
- `ShellLayout` stops the status auto-clear timer on `Unloaded` so a stray late tick can never run on a torn-down dispatcher.
- Documentation alignment: CLAUDE.md and README now spell out that **SSH currently flows through OpenSSH `ssh.exe` in a ConPTY**, with `~/.ssh/known_hosts` carrying the host-key pinning. The `SshTerminalSession` + `KnownHostsStore` path is reserved for the future "app-level SSH" mode and explicitly marked dormant.

### Pipeline
- Removed `dotnet-quality: "preview"` from the release workflow — .NET 10 is GA.
- The signing PFX is removed from the runner's filesystem after build (`if: always()`), narrowing the leak window even on ephemeral runners.

## [1.0.0.2] - 2026-04-30

### Changed
- Removed the `TabStripHeader` "Sessions" label from the tab area — the tab strip now uses the full width.
- Empty-tab hint reworded to "Pick a session from the left rail to get started." now that the Quick Connect bar is gone.

## [1.0.0.1] - 2026-04-30

### Removed
- Top **Quick Connect** bar removed from the shell. Sessions are opened from the left rail; ad-hoc connects come back as a focused dialog in a later release.
- Inline **Settings panel** at the bottom of the side rail removed (PuTTY path, WinSCP path, terminal font size). Same fields are now in a dedicated centred Settings dialog.
- Bottom-strip "Layout: default" label removed — there's only the default layout in 1.x.

### Added
- **Settings dialog** opens from a gear button in the bottom-right of the status footer. Same Edit-Session-style centred dialog (horizontal + vertical centre).
- **Unified status footer** — every transient message (clipboard copy, "Opening …", connect results, errors) now lands in one place at the bottom-left and auto-clears after 5 s of silence.

### Changed
- All `ContentDialog` instances (Edit Session, New folder, Delete, host-key prompt, password prompt, Settings) are explicitly horizontally and vertically centred.

## [1.0.0.0] - 2026-04-30

Initial public release.

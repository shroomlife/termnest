# Changelog

All notable changes to TermNest are documented in this file.

The version format is `MAJOR.MINOR.BUILD.PATCH` to match the MSIX `<Identity Version>` shape. The `PATCH` field is bumped on every change that produces a new MSIX so `Add-AppxPackage` recognises an upgrade.

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

# ADR-014: CommunityToolkit.Mvvm

Status: Accepted baseline; implementation LFI-060.

Use CommunityToolkit.Mvvm for observable state and async commands in the WinUI desktop.
Keep view models dependent on IPC/application-facing interfaces, not SQLite or crawling.
Expose progress, cancellation, service-offline state, errors, and consent explicitly.

Pin the package when UI implementation begins; do not add unused runtime dependencies
in Phase 0. Test view-model behavior separately from UI accessibility/DPI/tray smoke
checks. Code-behind remains presentation-only.

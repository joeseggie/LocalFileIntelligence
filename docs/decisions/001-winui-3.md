# ADR-001: WinUI 3

Status: Accepted baseline; implementation Phase 6.

Use WinUI 3 for the Windows 11 desktop, with native accessibility, DPI, tray integration,
and owner consent workflows. A web UI or cross-platform wrapper is unnecessary for the
Windows-only target. Keep presentation independent of indexing and SQLite.

Pin the Windows App SDK when the UI is introduced and validate deployment, keyboard,
screen-reader, high-contrast, and DPI behavior. Phase 0's Desktop project is a boundary
library, not an implemented WinUI application.

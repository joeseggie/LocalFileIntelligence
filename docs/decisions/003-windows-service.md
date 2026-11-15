# ADR-003: Independent least-privileged Windows Service

Status: Accepted baseline; installation release gate in Phase 3.

Use a Generic Host worker independent of the UI and agent processes. Bind one
installation to an explicitly enrolled Windows owner account; never index as LocalSystem
or use backup privileges to bypass access. Session 0 never interacts directly with UI.

The installer validates service-logon support, data-directory ACLs, and root access.
Use secure OS service enrollment, never passwords in JSON/args/logs. Unsupported
accounts, including configurations without usable service logon, fail explicitly.
Foreground developer execution is allowed, not a hidden elevated fallback.

A UI-only worker cannot satisfy independent indexing; an all-user privileged broker
adds unnecessary impersonation/multi-tenant risk. Account support and install/start/
stop/recovery/uninstall behavior require an isolated Windows VM before release.

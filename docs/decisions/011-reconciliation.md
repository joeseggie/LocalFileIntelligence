# ADR-011: Watchers plus authoritative reconciliation

Status: Accepted baseline; implementation Phase 4.

Treat FileSystemWatcher as lossy hints. Normalize/debounce events into bounded durable
jobs and reconcile at startup, after downtime/overflow/reconnect, and periodically.
Use persisted scan generations and file/policy revisions.

Only successful complete scans tombstone unseen records. Partial/inaccessible/offline
scans do not imply deletion. Retry finitely, recover leases after crashes, and discard
stale work. USN Journal may be a future optimization, not a correctness dependency.

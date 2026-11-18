# Initial schema snapshot

`schema.sql` is the executable initial-release design. Phase 0 tests it against
in-memory SQLite; it does not create a production index or implement migrations.
See [database-design.md](../database-design.md) for the data dictionary, invariants,
transaction rules, and future schema boundaries.

The LFI-015 SQL migration in `src/LocalFileIntelligence.Storage/Migrations` defines
file-content storage, external-content FTS5, and revision/access invalidation triggers.
It assumes the root and file-entry tables already exist; the LFI-011 migration runner
is still required before production databases can apply it.

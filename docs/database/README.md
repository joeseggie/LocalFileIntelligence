# Initial schema snapshot

`schema.sql` is the executable initial-release design. Phase 0 tests it against
in-memory SQLite; it does not create a production index or implement migrations.
See [database-design.md](../database-design.md) for the data dictionary, invariants,
transaction rules, and future schema boundaries.

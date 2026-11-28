# Initial schema snapshot

`schema.sql` is the executable initial-release design. Phase 0 tests it against
in-memory SQLite; it does not create a production index or implement migrations.
See [database-design.md](../database-design.md) for the data dictionary, invariants,
transaction rules, and future schema boundaries.

Storage migration `0001_initial_schema.sql` defines file contents, external-content
FTS5, and the eligibility guards on **both insert and update**. Migration
`0003_offline_content_invalidation.sql` extends root invalidation to offline roots
and purges their existing cached content on upgrade. Neither migration starts indexing.

# ADR-004: SQLite and explicit SQL migrations

Status: Accepted baseline; persistence implementation Phase 1.

Use Microsoft.Data.Sqlite with parameterized SQL, foreign keys on every connection,
WAL, bounded busy timeouts/readers, one writer, and short transactions. Store timestamps
as fixed-format UTC ISO 8601. The service is the only process opening SQLite.

Use ordered immutable checksummed SQL migrations, transactional ledger updates, and
SQLite's backup API. Refuse unsupported newer schemas and corruption; never silently
reset a DB. The Phase 0 SQL snapshot is an executable design, not the migration engine.

Hosted databases violate local deployment; JSON files lack the query/transaction
guarantees. ORM infrastructure is unnecessary initially. SQLite is not encrypted by
default; restrictive NTFS permissions and host disk encryption are separate controls.

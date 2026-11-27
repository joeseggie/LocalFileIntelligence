# Database design

Status: initial-release schema and persistence baseline. `database/schema.sql` is
normative and is checked against the embedded migration. LFI-010 through LFI-013
implement the connection factory, checksummed SQL migration runner, root repository,
and root-scoped file-entry repository. The repositories are not wired into service
startup; file-path validation is lexical and does not authorize or verify filesystem
access.

## Data dictionary

| Table | Primary fields and responsibility |
| --- | --- |
| `index_roots` | Integer ID, canonical absolute `path`, unique normalized `path_key`, display name, enabled/status, created/updated/last-scan times, redacted last error |
| `file_entries` | Integer ID/root FK, relative path/key/name/extension/type, size, filesystem created/modified times, seen/indexed times, SHA-256, extraction/index status, deletion flag/error |
| `file_contents` | File PK/FK and revision, extracted text, extractor/version, extraction time, content length |
| `scan_runs` | Root, generation/type/status, start/completion, discovered/processed/failed counts, last relative path, redacted error |
| `index_jobs` | File/root, type/status/priority/attempts, created/started/completed/next-attempt times, dedupe key, expected revisions, lease, redacted error |
| `excluded_paths` | Per-root normalized relative exclusions and recursive flag |
| `settings` | Local JSON-valued configuration and update time; never plaintext credentials |
| `mcp_clients` | Registered profile, capability hash, enabled state, declared locality and activity times |
| `client_root_grants` | Explicit per-client/root metadata/content authorization |
| `agent_sessions` | Individual UUID session, client, owner identity, start/last-seen/end |
| `audit_events` | Redacted operation/outcome/error/correlation, scoped client/session/root references |
| `pending_operations` | Reserved future request/approval state; no initial executor or mutation API |
| `schema_migrations` | Applied version/name/checksum/time; snapshot inserts no ledger rows |

Additional safety fields beyond the supplied minimum are root identities/policy and
index revisions, file identities/revisions/generations, parent path keys, deletion
times, job leases/deduplication, and explicit client-root grants. They address
concurrency, path safety, pagination, and recovery rather than adding product scope.

**No file_chunks, embeddings, model tables, or vector indexes are in the initial
schema.** Phase 10 introduces them through separate reviewed migrations.

## Representation

Use SQLite STRICT tables. Stable root/file integer IDs use AUTOINCREMENT to avoid
reusing an old client-visible ID after deletion. IDs are references, not capabilities;
every lookup is authorized. Session/profile/correlation IDs are UUIDs validated by the
application.

Times are UTC ISO 8601 text with a uniform millisecond form:
`2026-01-01T00:00:00.000Z`. The application validates real calendar values and converts
to UTC before serialization. SQL checks enforce the fixed shape, not full calendar
validation. Consistent precision allows lexical ordering.

Only index_roots stores an absolute path. file_entries/excluded_paths store
root-relative Windows paths with no traversal. The selected root itself is represented
by index_roots; file_entries contains its descendant files/directories. An empty
parent_path_key means a direct child of the root.

Normalize path keys from physically verified paths under the ordinal case-insensitive
Windows policy; preserve display spelling separately. Reject unsupported case-sensitive
directories. SQLite's ASCII NOCASE is not sufficient for Unicode Windows paths.
Entries are unique by `(root_id, path_key)`; hard links remain distinct path entries.
Directory sizes are zero and SHA-256 is null.

The file-entry repository canonicalizes relative path separators and derives the
case-insensitive key, parent key, name, and extension. It scopes lookups and mutations
by root ID. Upserts preserve IDs for an existing root/path pair; explicit rename
preserves the source ID within one root. Meaningful metadata changes advance the file
and root index revisions, clear cached hashes, reset processing state, and invalidate
revision-bound content transactionally. Repeated unchanged observations update
last-seen metadata and the root index revision without invalidating content. Tombstones
remain addressable by root/path for idempotent resurrection, while directory-child
queries omit them and use bounded keyset paging through an index on
`(root_id, parent_path_key, is_deleted, path_key)`. Populated-directory renames are rejected
until descendant paths can be updated atomically.

content_length counts UTF-16 units, not bytes. SHA-256 is lowercase 64-character hex
and is tied to the verified file revision. Invalidated metadata clears/recomputes its
digest in the repository transaction before duplicate reporting.

## Connections, migrations, and backup

Enable `foreign_keys=ON` and `busy_timeout=5000` on every connection. Set WAL outside
a transaction and verify it. Start with `synchronous=FULL`. Use one serialized writer,
bounded readers, and short transactions; never extract or hash while holding a write
transaction.

Resolve an explicit non-roaming/non-synchronized owner-restricted directory before
writing. DB, WAL, SHM, logs, and backups inherit the same restrictions. Do not use the
working directory or a client-supplied database path.

The LFI-010 factory currently enforces Local Application Data on Windows, checks known
sync-root environment variables and Windows ACL ownership/access, and holds directory
and database handles to prevent replacement while it is in use. It cannot detect
arbitrary unregistered sync software; service enrollment must add a trusted per-owner
location policy before claiming that such locations are excluded.

Migrations are ordered, immutable, and SHA-256 checksummed over their UTF-8 SQL.
`schema_migrations` is owned by the runner. It takes the serialized writer lease, makes
a same-directory SQLite backup before upgrading a database with an applied version, and
applies pending migration SQL and ledger rows in a transaction. Backup failure,
version gaps, checksum mismatch, unknown schema objects, corruption, and unsupported
newer versions fail without resetting the database. Backups are retained; cleanup and
retention policy are not implemented.

The embedded initial migration matches the design snapshot (the runner creates the
ledger table); integration tests compare their schema objects and exercise fresh/repeat/
upgrade/rollback/reopen/concurrent-lock cases on actual temporary files. In-memory
design tests do not prove WAL or production migrations. The runner is not wired into
service startup.

## Transaction invariants

**Scan:** persist a new generation/run, stream idempotent batches, and update progress.
Only complete successful enumeration may mark unseen entries deleted. Access failure,
depth/resource exhaustion, cancellation, and offline roots produce incomplete runs.

**Metadata/hash:** verify actual file identity/size/time and root policy. Increment the
file revision on change, clear stale SHA-256, and invalidate content in the same
transaction. A hash/extraction job rechecks expected revisions before committing.

**Content:** set the current file to indexed/extracted and insert the matching
file_contents revision in one transaction. Replacements update extractor/version,
text, length, and timestamp together. The composite FK rejects mismatched revisions.
Entry invalidation deletes cached content; FTS triggers remove indexed terms.

**Root state:** disabling or losing access hides data immediately, cancels active jobs,
and removes cached content. Pausing stops work without revoking read consent, but live
access checks still apply. Removing a root cascades entries/content/scans/jobs/exclusions/
grants/pending requests. Logical purging is not physical secure erase.

**Jobs:** active work is unique by root/type/dedupe key. File-specific jobs cannot name
a file belonging to another root. Only running jobs hold a lease; claims and lease
recovery are atomic. Retries are bounded and stale/policy-revoked work is discarded.

**Scoped revisions:** increment each affected index_roots.index_revision in the same
transaction as query-visible changes. API indexRevision is the sum for the requested,
authorized root set, bound to its grant fingerprint. Do not expose a global activity
counter for other clients' roots. Offset pagination is best effort unless the client
provides expectedIndexRevision; mismatches require restarting.

## FTS5

file_contents_fts is an external-content FTS5 index over file_contents.extracted_text,
with file_id as rowid and `unicode61 remove_diacritics 2`. Insert/update/delete triggers
keep index and base table synchronized. Tests must query actual MATCH results after
updates, deletion, revision changes, disabled roots, and root purges.

Query through current file/root/grant filters, not a global match followed by UI-only
filtering. Content-based matching/excerpts require content consent. Bind SQL parameters
and escape/parse literal FTS terms; parameters alone do not disable FTS syntax.
Use bounded BM25 ranking, stable ID tie-breaks, plain-text excerpts, and explicit
freshness/partial-result metadata.

## Authorization and retention

Clients start disabled with no root grants; content grants cannot exceed metadata
grants. Locality is client-declared `local`, `cloud`, or `unknown`, not verified proof
of model execution. Capabilities are random secrets stored only as hashes in SQLite
and protected by DPAPI in client-local storage.

Sessions and audit events are scoped and redacted. No file text, queries, secrets, raw
protocol payloads, or absolute paths in audit/error messages. Tombstones last seven
days; audits are capped at 30 days or 100,000 rows. Bounded maintenance verifies
foreign keys/integrity and checkpoints WAL appropriately.

pending_operations is reserved only. Its approval metadata constraint does not
implement secure approval or authorize execution. The initial app ignores pending
requests; a future ADR must define user confirmation, target revalidation, expiry,
idempotency, cancellation, audit, and request -> approval -> execution transitions.

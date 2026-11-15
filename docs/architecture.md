# Architecture

Status: proposed implementation baseline supplied by the owner. Phase 0 establishes
the structure and executable design contracts; later phases implement runtime behavior.

## Components and project dependencies

| Project | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `Core` | Domain models, value objects, business rules | BCL only |
| `Application` | Use cases, commands, queries, interfaces | Core |
| `Contracts` | Versioned IPC DTOs, envelopes, stable error codes | BCL only |
| `Storage` | SQLite connections, SQL migrations, repositories, FTS5 | Application, Core |
| `Infrastructure` | Filesystem, hashing, extraction, and Named Pipe implementations | Application, Core, Contracts |
| `Indexer` | Crawling orchestration, changes, reconciliation, scheduling | Application, Core |
| `WindowsService` | Generic Host, composition root, pipe server, lifecycle | Application, Contracts, Storage, Infrastructure, Indexer |
| `Desktop` | WinUI 3, CommunityToolkit.Mvvm, tray, owner workflows | Contracts; Infrastructure's IPC client when implemented |
| `McpServer` | Official-SDK standalone stdio adapter | Contracts; Infrastructure's IPC client when implemented |

All project names carry the `LocalFileIntelligence.` prefix. Current project references
are deliberately narrower until an implementation needs an adapter.
**Desktop and McpServer must never reference Storage, including transitively, or open
SQLite directly.** Keep IPC clients separate from filesystem implementations by
namespace/interface; presentation code must not use the filesystem adapter.

Core currently owns the immutable configuration model and lexical selection policy.
The other libraries are boundary scaffolds, not placeholder implementations returning
success. WindowsService runs only `--help`/`--validate-config`; the real worker is
Phase 3. Desktop becomes a WinUI executable in Phase 6. McpServer exits with a clear
stderr error until Phase 8 rather than pretending to serve MCP.

## Process model

Each agent launches its own `McpServer` stdio process. The desktop and all MCP
processes use the same versioned IPC contract to reach one WindowsService instance.
The service alone opens SQLite and performs indexing. Indexing continues with no UI
or MCP processes running.

The service is bound to one explicitly enrolled Windows owner account. It does not run
as LocalSystem or interact with the desktop from Session 0. Installation validates
service-logon support, the configured data directory, and access to selected roots.
Unsupported accounts fail installation clearly; foreground developer execution is
allowed, but privilege escalation is never a fallback.

The security boundary is the Windows owner, not a claim to isolate malicious code
already running as that owner. See `security.md` and ADR-003.

## Pipeline and consistency

1. An owner action selects a root and exclusions. The service validates the opened
   directory's canonical identity, local volume, access, and policy, then persists it.
2. The crawler streams directories/files into a bounded queue. Metadata and scan
   progress are upserted in short transactions, using normalized root-relative keys.
3. SHA-256 is calculated over authorized file bytes with cancellation. A before/after
   identity/size/time check and file/policy revision guard reject changed files.
4. Supported content is extracted locally, in an isolated bounded process, then
   committed to `file_contents` only if the revision and consent are still current.
5. FTS5 insert/update/delete triggers maintain the external-content index.
6. FileSystemWatcher events are normalized, debounced, and persisted as jobs.
   Startup, periodic, and overflow-triggered reconciliation repair missed events.

Watchers are hints, never the source of truth. Only a complete successful scan may
tombstone unseen entries. Inaccessible subtrees, offline roots, cancellation, and
partial enumeration must not become false mass deletion. Directory metadata is indexed
as well as files, enabling `list_directory` and bounded trees.

## Identity, consent, and query boundary

Selected roots do not automatically grant an agent access. Registered clients and
sessions carry per-root metadata/content grants. All authorization is enforced in the
service, before querying and again before releasing a result.

Content search, excerpts, raw supported-text reads, hashes, and duplicate groups require
content consent because even matching without an excerpt can disclose source content.
File reads use indexed IDs; directory tools accept only validated paths relative to an
approved root. No MCP call accepts an arbitrary absolute path or changes scope.

Disabled/revoked/inaccessible roots and files are gated immediately, including during
in-flight work. Root removal cancels work and cascades through content and FTS.
Metadata, counts, resource events, and status are scoped to the caller.

## Named Pipe protocol

Named Pipes is internal IPC, not a custom MCP transport. Separate query and control
endpoints; a query client cannot enroll roots, grant consent, pause, purge, or reindex.

- Owner/SYSTEM-only ACLs, remote-client rejection, Windows peer-token checks, and
  authenticated server identity. A SID in a predictable pipe name is not authentication.
- Owner-approved, cryptographically random client capabilities supplement SID checks.
  Store hashes in SQLite and protect local credentials with DPAPI; no secrets in args.
- Frame: 4-byte little-endian length followed by UTF-8 JSON. Check length before
  allocation; maximum 1 MiB/frame and 64 KiB/request payload.
- Request envelope: `protocolVersion`, `requestId` (UUID), `operation`, `payload`.
  Response: matching ID, typed result or stable error. Reject major-version mismatch.
- Initially one in-flight request per connection with a bounded pool; support
  cancellation, broken pipes, 10-second query deadlines, and bounded concurrency.
- Control commands originate in owner-approved desktop workflows, never in model tools.

Phase 5 must test cross-user/remote/spoofed-server rejection with isolated Windows
identities. A build or schema test cannot prove those properties.

## Storage and extraction

The service owns a non-roaming, non-synchronized, owner-restricted data directory.
Resolve it explicitly at enrollment; never rely on Session 0 environment defaults,
the current directory, or a path in a client request.

Use SQLite WAL, foreign keys on every connection, bounded busy timeouts/readers, one
serialized writer, checksummed transactional SQL migrations, and SQLite backup API.
Times are fixed-format UTC ISO 8601. See `database-design.md`.

Text/source, PDF, and Office extraction are Phase 7 issues. Pin reviewed parser versions;
never use Office automation, macros, network links, or cloud converters. Run parsers in
a restricted child with explicit read-only handles, no network, cancellation, and Job
Object time/memory limits. A Job Object alone is not a filesystem/network sandbox.

## Agent integration and future work

Phase 8 implements nine read-only MCP tools, three resources, and three prompt
templates using standard stdio. Clients share the service's index, not separate scans.
Report the client's declared locality visibly; do not present it as verified inference
locality. Strict end-to-end local AI requires a local agent and model.

Phase 10 adds chunks, local embeddings/vector indexing, semantic tools, summaries, and
related-file discovery. None is part of the initial DB schema. Future mutation requires
request -> pending approval -> explicit owner approval -> execution, with a new ADR;
the reserved `pending_operations` table has no initial executor.

## References

- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [.NET Windows Service hosting](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
- [MCP stdio](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
- [SQLite WAL](https://sqlite.org/wal.html)

# Local File Intelligence - technical specification

**Status:** Proposed architecture baseline supplied by the owner.
**Platform:** Windows 11. **Runtime:** .NET 10 / C#.
**Desktop:** WinUI 3 + CommunityToolkit.Mvvm.
**Database:** SQLite + FTS5. **Agents:** Multiple MCP-compatible clients.
**Processing:** Local only. **Scope:** Explicitly selected folders.

## Product objective

Index selected folders, collect file/directory metadata, calculate SHA-256, extract
supported content locally, provide full-text search, and expose controlled intelligence
to multiple agents. The indexing service must operate independently of the desktop.

Phase 0 is the engineering foundation, not the first working-index product milestone.
Avoid generating the entire application in one pass. Implement and validate one bounded
vertical slice at a time.

## Architecture and contracts

The nine components are Core, Application, Contracts, Storage, Infrastructure, Indexer,
WindowsService, Desktop, and McpServer. Their responsibilities/dependencies and the
pipeline are defined in `architecture.md`. Desktop/McpServer use authenticated Named
Pipes, never SQLite directly. Each agent launches a separate stdio process.

The 15 architecture decisions are indexed in `decisions/README.md`. They cover native
UI, runtime, service identity, SQLite/FTS5, IPC, MCP stdio/process isolation, selected
roots, local processing, reconciliation, SHA-256, read-only tools, MVVM, and deferred
semantics.

## Initial persistence

The normative initial design is `database/schema.sql`, described in
`database-design.md`. It contains `index_roots`, `file_entries`, `file_contents`,
`scan_runs`, `index_jobs`, `excluded_paths`, `settings`, `mcp_clients`, `agent_sessions`,
`audit_events`, reserved `pending_operations`, and `schema_migrations`.
`client_root_grants` is an explicit authorization addition. Version/generation/lease
columns make interrupted work and concurrent clients safe.

`file_entries` are unique by `(root_id, path_key)`, include both directories and files,
and store paths relative to their root. Use UTC ISO 8601 timestamps. External-content
FTS5 indexes `file_contents`, with insert/update/delete triggers and `unicode61`
diacritic removal. Use WAL, foreign keys, busy timeouts, and short write transactions.
`file_chunks`, embeddings, and vector indexes are **not** in the initial schema.

## MCP surface

Initial tools: `get_index_status`, `list_index_roots`, `search_files`, `search_content`,
`list_directory`, `get_file_metadata`, `read_file`, `get_directory_tree`, and
`find_duplicates`. All are read-only, schema-validated, bounded, and authorized.

Resources: `localfile://index/status`, `localfile://index/roots`, and
`localfile://index/recent-activity`. Prompts: `search_my_files`, `investigate_topic`,
and `compare_documents`. Exact contracts, examples, error codes, privacy semantics,
and default limits are in `mcp-contracts.md` and `contracts/mcp/v1`.

Future writes require a request, pending approval, explicit owner approval, and
execution workflow. No initial tool, prompt, resource, or background job executes
reserved pending operations.

## Safety and indexing rules

- Start with zero roots and zero client grants. Validate service-account access at
  installation and root enrollment; never run indexing as LocalSystem.
- Canonicalize every root/relative path and verify the actual opened object. Reject
  traversal, alternate streams, device/network paths, and unsupported volume semantics.
  Lexical checks alone are not authorization.
- Do not follow symlinks/junctions by default; initial support rejects reparse points,
  mount escapes, unsupported case-sensitive directories, and cloud placeholders.
  Do not hydrate offline files or recurse into archives.
- Initially support selected folders on local fixed NTFS volumes. Additional filesystems
  need explicit validation; no implicit home-directory or whole-drive discovery.
- Stream enumeration, respect exclusions, persist scan progress, and bound memory,
  depth, time, queues, retries, and response sizes.
- Hash authorized bytes with SHA-256. Guard work with file/root policy revisions and
  before/after identity/size/time observations; stale work cannot overwrite a new file.
- Watchers normalize/debounce hints; reconciliation is authoritative after startup,
  downtime, overflow, and periodically. Never tombstone on an incomplete scan.
- Recheck current access and grants before returning cached metadata/content. Disabling
  or removing a root gates access immediately; removing it purges dependent content/FTS.
- Keep contents, raw queries, credentials, and personal paths out of logs/CI/prompts.
  Protect DB/WAL/SHM/backups with restrictive NTFS permissions. SQLite is not encrypted
  by default; full-disk encryption is recommended.
- Make locality visible, but label it as client-declared/unknown, not proven.
  Local indexing does not prevent a cloud-backed agent from exporting approved results.

## Configuration-only behavior implemented in Phase 0

```json
{
  "schemaVersion": 1,
  "selectedFolders": []
}
```

Both properties are required and case-sensitive; unknown/duplicate properties are
rejected. Version must be integer `1`; at most 32 paths are accepted.

Paths must be absolute Windows drive-letter directories, not drive roots. The parser
rejects relative/device/UNC paths, forward slashes, dot segments, repeated separators,
alternate streams, invalid characters, reserved device names, trailing dots/spaces,
components over 255 characters, and paths over 32,767 characters. It normalizes one
trailing separator and drive-letter case; duplicate/overlapping selections are rejected
using ordinal case-insensitive component boundaries. Sibling prefixes remain distinct.

`WindowsService --validate-config <file>` reads at most 64 KiB of UTF-8 JSON with an
optional UTF-8 BOM. It does not open or authorize selected folders, create an index,
install a service, or start a watcher. Exit `0`: valid/help; `2`: invalid arguments or
configuration; `3`: configuration I/O/access failure. Errors are explicit and redacted.
Malformed UTF-8/Unicode is rejected rather than replaced silently.

## Initial engineering limits

These are implementation defaults/targets to validate in the owning issues, not
measured product guarantees from Phase 0.

| Area | Initial default or upper bound |
| --- | --- |
| Roots / crawl depth | 32 selected roots; depth 128, with visible incomplete-scan status at the limit |
| Work ingestion | Queue 1,024; one extraction worker; 500 ms event debounce |
| Reconciliation | Startup, overflow, reconnect, and every 30 minutes |
| Retries | 1, 5, 30 seconds, then explicit failure; no hot loop for permanent denial |
| Extraction | 16 MiB source; 2 Mi UTF-16 text units; 10 seconds; 256 MiB worker budget |
| Search | Query <=1,024 characters; limit 1-100; offset 0-10,000 |
| Directory tree | Depth 1-8; 1-1,000 returned entries; explicit truncation |
| Raw text read | `maxBytes` 1-65,536; reject oversized files, invalid encoding, and unsupported types |
| IPC/MCP | IPC frame <=1 MiB; request <=64 KiB; encoded MCP response <=256 KiB; 10-second deadline |
| Retention | Tombstones 7 days; redacted audit 30 days/100,000 rows, whichever limit is reached first |
| Performance target | On documented hardware and 100,000 synthetic files: warm metadata p95 <200 ms, FTS p95 <500 ms |
| Idle service target | CPU <1%, working set <250 MiB excluding parser/model workers |

Enforce encoded byte limits after JSON escaping, not from character count alone.
Resource ceilings that interrupt a scan must surface an incomplete state, never a
success-shaped partial scan or false deletion.

## First product milestone: working local index

The first product milestone is not an AI demo. These criteria are **not completed
by Phase 0** and must be exercised in an isolated Windows environment:

1. Install the least-privileged Windows Service.
2. Start the WinUI application.
3. Explicitly select a synthetic folder.
4. Recursively index its files and directories.
5. Persist metadata in SQLite.
6. Show counts and state in the dashboard.
7. Close the desktop.
8. Modify files and verify incremental changes with the UI closed.
9. Restart the service and verify recovery without false deletion.
10. Confirm no cloud service is required.

The milestone draws on Phases 1-6. Phase 7 adds text/source/XML/CSV/PDF/Office extraction
and FTS search; Phase 8 adds MCP; Phase 9 hardens release/install/backup/security; Phase 10
adds optional local semantics. Exact LFI issues and dependencies are in `backlog.md`.

## Copilot working agreement

For each issue: read AGENTS/architecture, inspect code, propose a small plan, change
only the issue scope, add tests, build/test, review the diff for security and architecture,
then report changed files, validation results, and unresolved risks. Never test by
crawling personal folders or silently installing a service.

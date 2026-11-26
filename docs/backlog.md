# Incremental implementation backlog

The authoritative issue definitions are in `backlog.json`: stable LFI IDs, phase,
scope, dependencies, and issue-specific acceptance criteria. GitHub issues use those
IDs in their titles. Numbering is not a dependency; follow each `dependsOn` list.

**Phase 0 is complete; LFI-010 (SQLite connection factory), LFI-011 (migration
infrastructure), and LFI-012 (root persistence) are implemented.** The rest of Phase 1
and all later phases remain open. Root persistence is not wired into service
enrollment; it does not verify selected folders or start indexing. The first product
milestone is a working local index through Phases 1-6; it is not the engineering
scaffold and is not an AI demo.

| Phase | Deliverable | LFI issues | Done when |
| --- | --- | --- | --- |
| 0 | Engineering foundation | 001-006 | Clean Windows build, tests, boundaries/ADRs, Copilot instructions, working CI |
| 1 | Persistence | 010-015 | Migrations, constraints, repositories, and FTS5 integration tests |
| 2 | Crawler | 020-025 | Recursive file/directory metadata, SHA-256, exclusions, safe errors/recovery |
| 3 | Windows Service | 030-034 | Least-privileged install/start/recovery and independent indexing |
| 4 | Incremental indexing | 040-045 | Create/modify/rename/delete converge, including after downtime |
| 5 | IPC | 050-054 | Concurrent authorized desktop/MCP clients; no direct DB access |
| 6 | Desktop | 060-066 | WinUI root management, dashboard/explorer/activity/tray/settings |
| 7 | Extraction and search | 070-076 | Local text/source/XML/CSV/PDF/Office extraction and useful FTS excerpts |
| 8 | MCP | 080-086 | Nine tools, resources/prompts, sessions, grants/audit, concurrent clients with UI closed |
| 9 | Release hardening | 090-094 | Clean install/upgrade/recovery/backup and security acceptance |
| 10 | Advanced intelligence | 100-105 | Local chunking/models/vector search, semantic tools, provenance and local agents |

## Working-index acceptance gate

Track this across dependent issues; do not close it just because Phase 0 or LFI-010
passes:

- Install the service under a supported least-privileged account in an isolated VM.
- Launch WinUI, select a synthetic root, and index its files and directories into SQLite.
- Show accurate dashboard counts/status; close the UI and keep indexing.
- Detect changes with the UI closed, restart the service, and recover correctly.
- Verify no cloud service or elevated indexing account is required.

Phase 9 adds clean-machine distribution/upgrade and final release assurance beyond
this first working-index milestone.

## Issue workflow

Read AGENTS and the relevant architecture/ADR/schema; inspect existing code; propose
a small plan. Implement one issue or explicitly approved vertical slice on a feature
branch. Add focused failure/cancellation/privacy tests, build/test/format, review the
diff, and report files changed, validation, and unresolved risks before opening a PR.

Do not install the service, change ACLs, or index personal folders during ordinary
development. Privileged checks use a disposable Windows VM and synthetic data.
Do not ship a phase based solely on schema/unit tests when its gate requires real
process, identity, filesystem, UI, or interoperability evidence.

## Scope controls

Initial MCP is read-only. Chunks/embeddings/vector tables and local semantic features
belong only to Phase 10. Mutating operations require a future approval-workflow ADR;
the reserved table is not an executor. Locality declarations are visible but not proof
that an external agent/model stays local.

The next implementation starts with **LFI-013**. Persistence and recovery precede
crawling; real authorization precedes exposing agent tools.

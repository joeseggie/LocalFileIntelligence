# Architecture Decision Records

The owner's proposed baseline is captured as ADR-001 through ADR-015. These decisions
guide incremental implementation; accepting a design does not mean its runtime
requirements have been implemented or verified.

| ID | Decision |
| --- | --- |
| [ADR-001](001-winui-3.md) | WinUI 3 for native Windows UI |
| [ADR-002](002-dotnet-10.md) | Stable .NET 10 and C# |
| [ADR-003](003-windows-service.md) | Least-privileged independent Windows Service |
| [ADR-004](004-sqlite.md) | SQLite persistence and SQL migrations |
| [ADR-005](005-fts5.md) | External-content FTS5 for initial full-text search |
| [ADR-006](006-named-pipes.md) | Authenticated local Named Pipes |
| [ADR-007](007-mcp-stdio.md) | Standard MCP stdio transport |
| [ADR-008](008-per-agent-process.md) | Independent MCP process per agent |
| [ADR-009](009-selected-folders.md) | Explicit selected-folder indexing |
| [ADR-010](010-local-processing.md) | Local-only indexing and extraction |
| [ADR-011](011-reconciliation.md) | Watcher hints plus reconciliation |
| [ADR-012](012-sha256.md) | SHA-256 content hashing |
| [ADR-013](013-read-only-mcp.md) | Read-only MCP first release |
| [ADR-014](014-communitytoolkit-mvvm.md) | CommunityToolkit.Mvvm |
| [ADR-015](015-defer-semantics.md) | Defer chunks, embeddings, and vector search |

New decisions should describe context, alternatives, consequences, compatibility,
and required validation. Never overwrite a shipped migration or silently widen a
security boundary under an existing ADR.

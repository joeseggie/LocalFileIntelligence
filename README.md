# Local File Intelligence

Local-first Windows file indexing and AI-agent intelligence, targeting **Windows 11,
.NET 10, WinUI 3, SQLite + FTS5, a Windows Service, and multiple MCP-compatible agents**.
Only explicitly selected folders are indexed; application processing stays local.

## Current state: Phase 0 complete; LFI-010 and LFI-011 implemented

The engineering foundation contains the nine source projects, three test projects,
central package management, CI, agent instructions, 15 ADRs, an executable initial
database design, MCP contracts, and the LFI implementation backlog. LFI-010 adds a
Storage-owned SQLite connection factory with one serialized writer, bounded read-only
readers, cancellable leases, and verified connection settings. On Windows, it validates
the data location and owner-restricted access, and pins storage identities while the
factory is alive.

**The working local index is not implemented yet.** The connection factory is not wired
into `WindowsService`; the opt-in migration runner is not invoked at service startup.
Repositories and indexing are not implemented. No service is installed, no personal
folders are scanned, and no desktop UI or MCP endpoint is active. Most projects
establish boundaries only. `WindowsService` currently offers a safe, configuration-only
command; `McpServer` fails explicitly until Phase 8 is implemented. The Desktop project
is a boundary-only Windows library; WinUI/MVVM/tray work is Phase 6.

## Build and run the safe starter

Use Windows 11 and a stable .NET 10 SDK. `global.json` prevents an installed .NET 11
preview from changing the build. No admin rights or WinUI workload is needed for Phase 0.

```powershell
git clone https://github.com/joeseggie/LocalFileIntelligence.git
Set-Location LocalFileIntelligence
dotnet restore LocalFileIntelligence.sln --locked-mode
dotnet build LocalFileIntelligence.sln --configuration Release --no-restore
dotnet test LocalFileIntelligence.sln --configuration Release --no-build
dotnet format LocalFileIntelligence.sln --verify-no-changes --no-restore
dotnet run --project src\LocalFileIntelligence.WindowsService -- --validate-config config\indexing.example.json
```

The example selects **zero folders**. The validator reads only the configuration
file named on the command line, not the folders it contains. Path checks are lexical,
not proof of filesystem access or authorization. Private experiments belong in
`config\indexing.local.json`, which is ignored by Git.

## Design and implementation

| Document | Purpose |
| --- | --- |
| [Technical specification](docs/technical-specification.md) | User-supplied baseline, scope, limits, and working-index acceptance criteria |
| [Architecture](docs/architecture.md) | Components, project references, identity, IPC, and pipeline |
| [Architecture decisions](docs/decisions/README.md) | ADR-001 through ADR-015 |
| [Database design](docs/database-design.md) | Initial tables, ISO 8601 timestamps, FTS5, and migration rules |
| [MCP contracts](docs/mcp-contracts.md) | Nine read-only tools, three resources, three prompts, and privacy boundaries |
| [Security](docs/security.md) | Least privilege, path/ACL checks, consent, storage, and agent locality |
| [Backlog](docs/backlog.md) | Phases 0-10 and LFI-001 through LFI-105 |
| [Development guide](docs/development-guide.md) | Build, tests, Copilot CLI, and issue-to-PR workflow |

The initial bootstrap establishes `main`. Subsequent work follows **issue -> small
plan -> feature branch -> implementation -> tests -> review -> PR -> merge**.
Do not generate the entire application in one pass.

The next vertical slice is **LFI-011: migration infrastructure**. The first product
milestone is a working local index through the service and desktop, not an AI demo; its
criteria remain open.

```powershell
gh issue list --repo joeseggie/LocalFileIntelligence --state open
copilot
```

Suggested prompt:

> Implement LFI-011 only. Read AGENTS.md, the issue, docs/database-design.md, and
> ADR-004. Inspect the existing projects, propose a small plan, and work on a feature
> branch. Add focused tests and report changed files, validation, and unresolved risks.
> Do not install the service, index my folders, or implement later issues.

## Privacy

Local indexing is not the same as local model inference. An MCP client can forward
approved results to a cloud model. **End-to-end local AI requires local agent runtimes
and models**; client locality will be visible but a client's declaration is not proof.
No cloud extraction/indexing/embedding service is part of the application.

Development restores and Copilot CLI can use the network. Use only source code and
synthetic fixtures in development prompts and CI, never personal documents or indexes.
The repository is private. No software license has been selected.

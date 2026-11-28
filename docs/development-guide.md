# Development guide

## Toolchain and Phase 0

Use Windows 11 and a stable .NET 10 SDK. global.json permits .NET 10 stable feature/
patch versions, not previews or .NET 11. Phase 0 requires no admin rights, service
installation, WinUI workload, model, database server, or MCP client.

```powershell
dotnet restore LocalFileIntelligence.sln --locked-mode
dotnet build LocalFileIntelligence.sln --configuration Release --no-restore
dotnet test LocalFileIntelligence.sln --configuration Release --no-build
dotnet format LocalFileIntelligence.sln --verify-no-changes --no-restore
dotnet run --project src\LocalFileIntelligence.WindowsService -- --help
```

WindowsService is currently a configuration-only CLI, not a worker installed in SCM.
Desktop is a boundary-only library until LFI-060; McpServer exits with a clear error
until Phase 8. The other projects establish the supplied architecture without fake
successful implementations. Follow `docs/backlog.json`, not generated placeholders.

## Dependencies and network

All package versions live in Directory.Packages.props. Commit per-project lockfiles
and use locked restore in CI. After an intentional manifest/version change, run normal
restore once, inspect the lock diff, and rerun relevant checks.

NuGet.org is the default public source. If this environment cannot establish TLS to
it, the official Microsoft public mirror is an explicit restore alternative:

```powershell
dotnet restore LocalFileIntelligence.sln --locked-mode --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json
```

The bootstrap used that mirror locally; CI uses the default NuGet.org source. Do not
disable TLS checks, auditing, or machine-wide source policy. Repository NuGet.config
is scoped to this clone. Dependencies/development tools using the network are distinct
from runtime processing, which must stay local.

Pin Windows App SDK/CommunityToolkit.Mvvm in LFI-060 and the official MCP SDK in
LFI-080. Do not add unused runtime dependencies in advance. No machine-wide tool or
workload installation is a side effect of the build.

## Copilot CLI workflow

Start `copilot` inside the clone. Root AGENTS.md supplies repository instructions.
`copilot instruction list` shows discovered sources in CLI builds supporting that
command. Work from a specific LFI issue and read its dependencies and acceptance.

1. Read AGENTS, architecture, relevant ADR/schema, and the issue; inspect existing code.
2. Propose a small implementation plan and create a feature branch.
3. Change only that issue's scope, with focused tests and synthetic fixtures.
4. Build, test, format, and run the issue's real integration/manual checks.
5. Review the diff for architecture/security violations and private data.
6. Report changed files, validation results, and unresolved risks; open a linked PR.
7. Review/merge and close the issue only when acceptance is met.

The bootstrap establishes main directly because the repository was empty. All
subsequent implementation uses PRs. Do not merge, install a service, change ACLs, or
index real folders without explicit authorization.

The .NET CLI is sufficient. Optional template-specific Copilot assistance can be
installed separately; the template specialist was not required for this scaffold:

```text
/plugin marketplace add dotnet/skills
/plugin install dotnet-template-engine@dotnet-agent-skills
```

Restart the CLI after installation and check `/skills`. Plugins are developer aids,
not application/CI dependencies.

## Validation layers

| Suite | Current coverage | Does not prove |
| --- | --- | --- |
| UnitTests | Configuration policy, project boundaries, actual MCP JSON Schemas/examples | Live path containment, MCP transport or agent locality |
| IntegrationTests | In-memory design-snapshot schema/constraints/FTS mutation/purge; on-disk temporary SQLite connection settings, WAL, leases, migrations (fresh/repeat/upgrade/rollback/process-kill reopen/overlap/backup) and repositories | Installed-service storage, cross-user ACL enforcement, power-loss durability or a live index |
| AcceptanceTests | Safe command arguments, config encoding/size, exit codes/redaction | Installed Windows Service or an interactive WinUI product |

Run a focused suite/filter while developing, then the required checks before a PR:

```powershell
dotnet test tests\LocalFileIntelligence.UnitTests --filter FullyQualifiedName~Configuration --configuration Release
```

Filesystem tests create uniquely named disposable directories and synthetic data.
Do not use home/Downloads/repository parents or actual selected folders. Privileged
identity/service/ACL and clean-install tests are opt-in in disposable Windows VMs.

The working-index milestone needs actual service install, WinUI, folder selection,
SQLite counts, UI-closed change detection, restart recovery, and no-cloud evidence.
Phase 0 passing does not complete that milestone.

## Conventions

Use nullable C#, file-scoped namespaces, .editorconfig and dotnet format. Warnings are
errors. Keep Core/Contracts free of adapters; Desktop/McpServer never reference Storage,
even transitively. Contracts contain DTOs, not business logic or private DB models.

Use parameterized SQL, UTC ISO 8601 timestamps, bounded transactions/queues, cancellation,
and explicit errors. No broad success-shaped fallbacks. Keep contents/queries/paths/
credentials out of logs, prompts, issues and CI artifacts.

Copy the empty example to `config/indexing.local.json` for private config experiments;
only lexical validation is implemented. Never commit the local file, DBs, logs, dumps,
weights or personal documents.

Use a new ADR for changes to trust boundaries, service identity, schema strategy,
transport, public contracts, filesystem support, extraction isolation or inference.
Immutable shipped migrations are never edited. Keep docs, schemas, tests and backlog
consistent, and mark planned behavior honestly.

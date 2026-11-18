# Security and privacy model

## Assets and boundary

Protect root selections, paths, filenames, metadata, extracted text, vectors, client
grants, and audit data. An embedding is sensitive derived content, not anonymization.

The primary boundary is the Windows owner account. Other unprivileged users and
unauthorized local/network clients must be denied. Administrators, SYSTEM, and
malware already executing as the owner can compromise owner data and are not
isolated by Named Pipes or DPAPI. Do not claim otherwise.

Phase 0 performs configuration-only validation. None of the future file-access, IPC,
service-account, or extraction protections is implemented by a lexical path check.

## Threats and required mitigations

| Threat | Required mitigation and release evidence |
| --- | --- |
| Accidental broad indexing | No default roots; no drive roots, ancestors, implicit home expansion, or network mounts; explicit UI consent |
| Traversal or junction escape | Opened-handle canonical/volume checks, reject reparse points and unsupported case-sensitive directories, revalidate on reads; adversarial synthetic tests |
| TOCTOU path/ACL change | Check the actual opened object and current grants/access; bind data to file/policy revisions; discard stale results |
| Cross-user access or spoofed pipe | Owner/SYSTEM-only ACL, remote-client rejection, token-based peer checks, verified server identity, separate query/control capability tests |
| Content disclosure through search | Require content grants for FTS/semantic matching and snippets, not just for explicit text reads |
| Malicious files | No execution/macros/links; isolated extractor, restricted token/handles, network denial, bounded time/memory/text size |
| Prompt injection in a document | Return text as explicitly untrusted data with provenance; never interpret it as control instructions or let it grant permissions |
| Resource exhaustion | Bounded enumeration, persisted queues, cancellation, input/schema limits, page/response limits, extraction budgets |
| Stale or revoked content | Immediate query gating, transactionally delete derived data, recheck consent before returning/committing, reconcile on restart |
| Database/model theft | Owner-restricted non-synchronized data directory, explicit at-rest limitations, verified local model artifacts |
| Logging/CI leakage | Synthetic test data only; redact personal paths, raw queries, contents, credentials, and protocol payloads |

## Identity and service installation

Run a single-owner service under the explicitly enrolled owner account with the
minimum Windows service-logon rights. Never index as LocalSystem or add backup
privileges to bypass file access. The installer requires elevation only for SCM
configuration and data-directory ACL provisioning.

Credential handling must use the operating system's secure service-enrollment path;
do not store passwords in source, JSON, process arguments, logs, or application DB.
Windows Hello-only and other accounts without supported service logon must fail
installation clearly. Keep foreground execution under the owner as a developer mode,
not a hidden privilege-elevation fallback.

This model must be verified on the supported account types before Phase 3 is release-ready.
Changing to a broker/impersonation design requires a new ADR and security review.

## Root consent and revocation

Consent identifies an exact root, its canonical identity, and the current policy.
Selecting a root does not grant an MCP client access to it. Every client/root pair
gets a separate grant, metadata first; extracted content is an additional opt-in.

Reject unsupported/offline/reparse paths rather than hydrating or following them.
Revalidate live access at query time; never expose cached content simply because
indexing used to be allowed. Root removal gates queries first, then cancels work and
purges dependent records. Revocation is not delayed until the next scan.

Logical deletion does not guarantee physical erasure from SQLite free pages, WAL,
backups, storage snapshots, or an SSD. Provide an explicit purge/checkpoint procedure,
document backup retention, and recommend full-disk encryption. Do not promise secure
erase. A copied index needs the same protection as the original documents.

## Data at rest

SQLite is **not encrypted by default**. V1 relies on Windows ACLs and recommends
BitLocker/device encryption for the host. App-level encryption is not claimed.
DPAPI protects local profile capabilities only, not SQLite text or embeddings.
Full-database encryption requires a reviewed provider, key-recovery policy, and ADR.

Resolve a per-owner, non-roaming data directory, reject synchronized/reparse storage,
and restrict the directory plus DB/WAL/SHM/log/backup files before writing. Fail closed
if safe ACLs cannot be established. Never rely on `.gitignore` as a security control.

The LFI-010 connection factory currently restricts Windows storage to the current
owner's Local Application Data directory, rejects known sync roots exposed through
standard environment variables, verifies the owner/DACL on the data directory and
existing database sidecars, and pins directory/database identities while the factory
is alive. This does not identify arbitrary or unregistered sync software. A future
owner-enrollment location policy must establish that property before the service relies
on this factory for a production index; a local path alone is not proof of non-sync.

Use cryptographically random client capabilities, store only their hashes in SQLite,
and protect the per-client local credential with owner-scoped DPAPI. Do not put the
secret in an MCP command line or check it into a client example. Owner-level malware
can still read/use those capabilities; client registration is least privilege for
cooperating clients, not hostile same-account sandboxing.

## Local-only operation and agents

The application has no runtime HTTP client, telemetry exporter, automatic updater,
cloud inference, or automatic model download. Installation/dependency restore is a
separate, intentional developer/administrator operation.

MCP sends information to the requesting process. Once a permitted client receives
data, this application cannot control its subsequent use. For strict local-only use,
run both the agent and its model locally and verify their network configuration.
A client-supplied claim that it is local is not enforceable proof. Connecting a
cloud-backed agent changes the end-to-end privacy boundary and is outside strict
local-only mode, even though this application itself makes no network requests.

Copilot CLI is a **development assistant**, not a privileged indexing component.
Do not ask it to inspect personal test folders, production indexes, or real document
contents. Only source code, design documents, and synthetic fixtures belong in
development prompts and CI.

## Reporting

Do not include real paths, credentials, index databases, or document contents in issues.
Use a minimal synthetic reproduction. Keep sensitive reports in an owner-approved
private channel; this private repository is not a guarantee that every collaborator
is authorized to see personal files.

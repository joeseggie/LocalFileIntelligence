# MCP v1 contracts

Status: proposed Phase 8 contract; Phase 0 validates schemas/examples but does not
serve MCP. `contracts/mcp/v1/tools.json` is the normative nine-tool inventory. Its
input/output schemas are self-contained JSON Schema 2020-12, with no remote references.
`examples.json` contains synthetic data, not actual indexed files or calculated digests.

## Lifecycle and transport

Use the official MCP C# SDK and standard UTF-8 newline-delimited stdio, one process per
agent. The design baseline is MCP `2025-11-25`; pin a stable SDK and test supported
version negotiation in LFI-080/081. Reject unsupported protocol versions explicitly.

Advertise tools, resources, and prompts, each with `listChanged=false`; resources
have `subscribe=false` initially. Do not advertise sampling, task augmentation, HTTP,
or arbitrary filesystem resources. Reserve stdout for MCP; redacted logs use stderr.
EOF cancels work and exits, and protocol cancellation propagates through IPC.

Future, non-runnable client configuration:

```json
{
  "mcpServers": {
    "local-file-intelligence": {
      "command": "C:\\Program Files\\LocalFileIntelligence\\LocalFileIntelligence.McpServer.exe",
      "args": ["--profile", "<owner-approved-profile-id>"]
    }
  }
}
```

The profile ID alone grants nothing. Load a locally DPAPI-protected random capability,
authenticate the Windows identity and pipe server, and register an individual session.
Never put a secret in args or committed examples. Unknown clients start with no grants.

## Tools and limits

| Tool | Input / defaults | Result and permission |
| --- | --- | --- |
| `get_index_status` | `{}` | Grant-scoped health/counts and reported locality; no global activity disclosure |
| `list_index_roots` | `includeDisabled=false` | Approved root labels/IDs/state/content permission; no absolute paths |
| `search_files` | `query`, optional root IDs/extensions; `limit=25`, `offset=0` | Filename/relative-path metadata search; metadata grant |
| `search_content` | `query`, optional root IDs; `limit=25`, `offset=0` | FTS5 matches and plain-text excerpts; content grant |
| `list_directory` | root ID; relative path `""`; entry-type/extensions; `limit=25`, `offset=0` | Indexed direct children, not arbitrary live enumeration |
| `get_file_metadata` | file ID | Metadata, revision, extraction state; hash null without content consent |
| `read_file` | file ID; `maxBytes=65536`; optional expected revision | Complete supported UTF-8/UTF-16 text within cap; content grant |
| `get_directory_tree` | root ID, relative path `""`; `depth=3`, `entryLimit=200` | Flat descendant tree with relative paths/depth and explicit truncation |
| `find_duplicates` | optional root IDs; `minimumSize=1`, `limit=20` | Size/SHA-256 groups from currently accessible files; content grant |

Root/file IDs are positive stable integers, not capabilities. Omitted root ID arrays
mean currently granted roots; an explicit empty array is invalid. Unknown or
unauthorized roots/files are indistinguishable. Explicitly requesting any unauthorized
root fails rather than silently broadening or partially honoring a cross-scope request.

All inputs reject unknown properties and out-of-range values. Query text is 1-1,024
characters, not whitespace-only; root/extension arrays have at most 32 entries.
Extensions are dotted literal suffixes, not globs. Implement defaults explicitly;
JSON Schema `default` does not populate values.

Search/list limits are 1-100 and offsets 0-10,000. Sort deterministically (metadata by
normalized path/ID; FTS by BM25/ID). `nextOffset` is null only when exhausted; if the
offset ceiling prevents continuation, return `SIZE_LIMIT_EXCEEDED` with guidance to
narrow the query rather than pretending the result set is complete.

Offset paging without an expected revision is best effort: edits can reorder entries.
Clients needing a consistent traversal resend the prior `indexRevision` as
`expectedIndexRevision` with unchanged query, filters, and roots. Compare under the
same short read snapshot; mismatch returns `INDEX_BUSY` with restart guidance.
The revision is the sum for the requested authorized roots, not a global counter.
Revocation is always enforced even if it invalidates the page.

Content matching itself requires content consent, even without excerpts. Hash and
duplicate-group access require the same consent. Query filters precede candidate
disclosure, and live root/file access and current grants are checked before release.
Metadata-only clients cannot infer content through a match or digest.

Directory paths are root-relative Windows strings: empty means the selected root.
Reject absolute paths, dot/traversal segments, alternate streams, device names, and
unsupported syntax; schema checks are only an initial filter. Service-side
canonical/handle-based checks remain authoritative. Tree depth is 1-8, entry limit
1-1,000; depths are relative to the requested base and root itself is not a returned
file_entries row. `truncated=true` means a depth/count/byte cap omitted descendants.

`read_file` resolves an indexed ID and safely opens the current underlying file;
it is not an alias for cached PDF/Office extraction. Only supported text/source
UTF-8/UTF-16 files are returned, with strict decoding and no rendering/execution.
Reject files over maxBytes (1-65,536) rather than silently truncating. Check identity,
size/time, revision, grants, and access before and after reading. An expected-revision
mismatch or concurrent change returns `CONTENT_NOT_AVAILABLE` and requires refresh.
PDF/Office content is accessed through authorized search excerpts, not binary reads.

Duplicate groups have at least two authorized, current members, at most 100 returned
members/group and 100 groups. `totalFiles` counts only authorized members; truncation
flags are explicit. `sameFileIdentity` compares each member with the group's first
member (`null` when unknown); hard links are not evidence of reclaimable duplicate
disk space. No delete suggestion is executed.

All requests have a 64 KiB payload budget and 10-second query deadline. The entire MCP
response, including duplicated text/structured JSON, is capped at 256 KiB after encoding.
Reduce pages with honest continuation/truncation flags; if a legal atomic result cannot
fit, return `SIZE_LIMIT_EXCEEDED`. Never truncate JSON or split Unicode surrogates.
Partial availability is explicit; inaccessible individual files are omitted without
revealing why a hidden file matched.

## Success and errors

Tool success: `structuredContent = {"ok":true,"data":...}`, `isError=false`.
Execution failure: `structuredContent = {"ok":false,"error":...}`, `isError=true`.
The `content` array also contains a text block serializing the exact same object,
for clients without structured-result support. Both variants conform to outputSchema.

Example execution failure:

```json
{
  "ok": false,
  "error": {
    "code": "FILE_NOT_FOUND",
    "message": "The requested file is unavailable.",
    "retryable": false,
    "correlationId": "33333333-3333-4333-8333-333333333333"
  }
}
```

| Code | Meaning |
| --- | --- |
| `ROOT_NOT_FOUND` | Unknown or unauthorized root; no existence disclosure |
| `ROOT_DISABLED` | Already authorized root is disabled |
| `FILE_NOT_FOUND` | Unknown, deleted, inaccessible, or unauthorized file; indistinguishable |
| `PATH_OUTSIDE_ROOT` | Invalid/escaping relative directory request |
| `CONTENT_NOT_AVAILABLE` | Missing/stale extraction or changed/unavailable current text; refresh metadata |
| `UNSUPPORTED_FILE_TYPE` | Read/extraction type or encoding is unsupported |
| `SIZE_LIMIT_EXCEEDED` | File/text/request/result/depth/pagination budget cannot satisfy the request |
| `INDEX_BUSY` | Bounded capacity/deadline or expected snapshot mismatch; finite retry/restart |
| `SERVICE_UNAVAILABLE` | Service stopped/upgrading/not ready or authorized root unavailable |
| `ACCESS_DENIED` | Disabled client or known approved scope lacks content permission |

Malformed JSON-RPC, unknown tools, and schema-invalid arguments use standard SDK
protocol errors (for example `-32602`), not success-shaped tool results. Operational
exceptions map to redacted stable errors; logs retain correlation/error codes, not
paths, queries, text, credentials, or protocol payloads. Do not use error detail to
confirm that an unauthorized object exists.

## Resources

`resources.json` declares the three exact resource URIs. `resources/read` returns a
text resource with `mimeType=application/json`; the text is serialized JSON.

| URI | JSON payload |
| --- | --- |
| `localfile://index/status` | Same structured envelope as get_index_status |
| `localfile://index/roots` | Same envelope as list_index_roots with includeDisabled=false |
| `localfile://index/recent-activity` | `recent-activity.schema.json`: at most 100 current-client redacted events, newest first |

Unknown URIs, extra URI parameters, and unauthorized resources fail explicitly. Do not
add file:// resources, arbitrary URI templates, or cross-client/global event streams.
Recent activity exposes only whitelisted operation/outcome/error/correlation data, never
paths/query text or contents. Resource protocol failures carry stable error codes in
SDK error data; they are not tool CallToolResult messages.

## Prompts

`prompts.json` declares prompt names/arguments. `prompt-templates.json` supplies bounded
static guidance for `search_my_files`, `investigate_topic`, and `compare_documents`.
Prompt arguments are strings as required by MCP: query/topic 1-1,024 non-whitespace
characters; file IDs are positive decimal integer strings. Reject unknown arguments.

On prompts/get, return the static template as one user-role text message and a second
user-role text message containing JSON-serialized argument data, explicitly labeled
untrusted user input. Never interpolate raw argument text into system/developer
instructions. No documents are read by prompt expansion, and no prompt grants scope,
executes tools, requests sampling, or silently sends content to a model.

## Security and interoperability gates

Filenames, labels, excerpts, and document text are untrusted data, not instructions.
File-bearing tool results have `untrusted=true`; annotations/templates do not replace
service authorization or reliably defeat prompt injection in an external model.

The adapter must show registered locality as `local`, `cloud`, or `unknown`, explicitly
client-declared. A local index cannot stop a permitted client forwarding content to
cloud inference. Strict local AI requires local runtimes/models and independently
verified network configuration.

LFI-086 must exercise initialize/version negotiation, all tools/resources/prompts,
EOF/shutdown/cancellation, stderr/stdout discipline, grants/revocation, malformed/large
input, and concurrent independent clients with the UI closed. Record at least two
client implementations and versions. Schema tests do not prove a running server,
transport compatibility, filesystem authorization, or model locality.

## References

- [MCP tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
- [MCP resources](https://modelcontextprotocol.io/specification/2025-11-25/server/resources)
- [MCP prompts](https://modelcontextprotocol.io/specification/2025-11-25/server/prompts)
- [MCP transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
- [Official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)

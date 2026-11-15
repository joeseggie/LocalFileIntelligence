# ADR-008: One MCP process per agent

Status: Accepted baseline; implementation Phase 8.

Each agent launches an independent McpServer process and registered session. All
processes query the same service-owned index. Scope grants, cancellation, audit, and
locality display to each client/session.

A shared unauthenticated adapter would conflate permissions/lifetimes. One small
process per agent improves compatibility and session isolation but does not sandbox
malware already running as the owner. Test at least two independent MCP client
implementations concurrently, including with the desktop closed.

# ADR-006: Authenticated Named Pipes

Status: Accepted baseline; implementation Phase 5.

Use bounded, versioned Named Pipes for desktop/MCP-to-service communication. Enforce
owner/SYSTEM ACLs, reject remote peers, validate Windows identity and server ownership,
and separate control/query capabilities. The shared Contracts project contains DTOs,
not database access.

Loopback HTTP would add listener/origin/DNS-rebinding concerns. A predictable pipe
name is not authentication. Test spoofed server, cross-user, revoked client, malformed/
oversized frame, cancellation, timeout, and concurrent-client cases.

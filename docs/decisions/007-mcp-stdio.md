# ADR-007: Standard MCP stdio

Status: Accepted baseline; implementation Phase 8.

Use the official C# MCP SDK with standard UTF-8 newline-delimited stdio. Negotiate
protocol versions explicitly. Reserve stdout for MCP, stderr for redacted diagnostics.
Expose the specified tools, resources, and prompts without an HTTP listener or sampling.

Custom Named Pipe MCP transport would reduce interoperability; Named Pipes is only the
internal adapter-to-service transport. Pin and test the SDK's supported protocol
versions and real stdio behavior, not just the JSON Schemas.

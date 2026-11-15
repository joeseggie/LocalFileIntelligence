# ADR-013: Read-only MCP initially

Status: Accepted baseline; implementation Phase 8.

The first release has the nine specified read-only tools, three resources, and three
prompt templates. Enforce authorization and bounded results in the service. Reads use
indexed IDs or validated paths relative to approved roots, never arbitrary paths.

No file mutation, shell execution, root enrollment, or scope-changing tool. Prompt
templates do not bypass grants or automatically disclose content. The reserved
pending_operations table has no initial executor. Future mutations require a new ADR
and request -> pending approval -> explicit owner approval -> execution workflow.

# ADR-015: Defer embeddings and vectors

Status: Accepted baseline; advanced intelligence Phase 10.

The initial schema and release use metadata, SHA-256, and FTS5. Do not add file_chunks,
embeddings, vector indexes, model downloads, or an AI demo to the foundation.

Phase 10 may add explicitly imported local models/tokenizers, version/checksum/license
tracking, chunks, bounded local vector search, provenance, summaries, and semantic MCP
tools. Choose the model and vector implementation with measured synthetic workloads
and an ADR; never mix incompatible model dimensions or silently call a hosted model.

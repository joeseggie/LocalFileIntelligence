# ADR-010: Local-only processing

Status: Accepted baseline.

Index, hash, extract, and eventually embed locally. No cloud converters, hosted
indexes/models, telemetry export, or automatic model download. Use bounded isolated
parsers for supported text/source/PDF/Office files; never execute macros or active links.

Development dependency restore is separate from application processing. Use synthetic
fixtures in CI/Copilot prompts. A permitted agent can forward returned content to a
cloud model: display declared/unknown locality, but never claim it is verified.
Strict end-to-end locality requires local agent runtimes and models.

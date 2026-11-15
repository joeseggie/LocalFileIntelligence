# ADR-009: Explicit selected-folder indexing

Status: Accepted baseline; enrollment/crawler Phase 2.

Start with no roots. Only an explicit owner action enrolls a root; agents cannot add
one. Persist exclusions and normalized root-relative paths. Validate actual opened
objects, component containment, current permissions, volume identity, and policy.

Reject traversal/device/network paths and unsupported reparse/case-sensitive semantics;
do not follow links/junctions by default or hydrate cloud placeholders. Configuration
syntax is not authorization. Automatic home/whole-drive discovery is rejected because
it expands scope without consent. Root disable/removal gates access immediately.

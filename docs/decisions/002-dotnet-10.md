# ADR-002: .NET 10 and C#

Status: Accepted baseline.

Use stable .NET 10 and C# for the service, contracts, adapters, and tests. Centralize
package versions in Directory.Packages.props; use nullable types, deterministic builds,
warnings as errors, and committed lockfiles. global.json permits .NET 10 stable
feature/patch updates but not previews or a different major.

A .NET 11 preview is not required just because it is installed. Core and Contracts
remain free of platform/transport/storage dependencies. Platform-specific code is
isolated and introduced by its owning issue.

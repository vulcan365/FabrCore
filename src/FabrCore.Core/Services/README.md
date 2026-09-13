# FabrCore.Services.Contracts

Transport-neutral contracts shared by FabrCore service administration clients and service hosts.
The package is organized by service and retains each service's existing public namespaces, so
adding a contract here does not couple GraphRAG, Memory, or future services to one another.

The package includes GraphRAG and Memory administration clients, DTOs, capability states, request
models, and typed errors. Future open service contracts should be added under their own service
area in this same package rather than creating another contracts NuGet.

Forge administration contracts are commercial and live in `FabrCore.Forge.Contracts`; this
package intentionally contains no Forge types.

This package contains no SQL client, Dapper dependency, migrations, schema initialization,
ingestion implementation, service-host implementation, or database credentials. UI applications
normally consume it from the public package feed.

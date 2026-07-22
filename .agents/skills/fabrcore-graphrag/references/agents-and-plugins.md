# Agents And Plugins Reference

## Table Of Contents

- Available adapters
- Required DI setup
- Configuration keys
- Scope handling
- Search plugin
- Ingest plugin
- Search agent
- Best practices

## Available Adapters

The service package includes adapters in the `FabrCore.Services.GraphRag`
namespace:

- `GraphRagSearchPlugin`
- `GraphRagIngestPlugin`
- `GraphRagQueryPlugin`
- `GraphRagDomainPlugin`
- `GraphRagScopePlugin`
- `GraphRagSearchAgent`

These adapters are useful for agent and tool-call scenarios. They are not UI.

## Required DI Setup

Register GraphRAG services before initializing plugins or agents:

```csharp
builder.Services.AddGraphRagServices(
    connectionStringName: "GraphRagDb",
    extractionModelName: "graph-extraction");
```

Plugins resolve service interfaces from DI, especially:

- `IKnowledgeSearchService`
- `IKnowledgeIngestionService`
- `IKnowledgeScopeService`

## Configuration Keys

Common plugin/agent args:

```json
{
  "ConnectionStringName": "GraphRagDb",
  "AllowedScopes": "customer-a,customer-b"
}
```

Plugin-specific setting style is also supported:

```json
{
  "graph-rag-search:ConnectionStringName": "GraphRagDb",
  "graph-rag-search:AllowedScopes": "customer-a,customer-b"
}
```

For ingestion:

```json
{
  "graph-rag-ingest:ConnectionStringName": "GraphRagDb"
}
```

## Scope Handling

For search-capable tools, `AllowedScopes` is required before tool calls can
search. Scopes must come from trusted configuration or authenticated context.

Never let the LLM provide authoritative scopes. The LLM may ask about topics,
domains, categories, or entity types, but the application decides the scope list.

## Search Plugin

`GraphRagSearchPlugin` exposes tool-call methods over `IKnowledgeSearchService`.

Typical capabilities:

- Search entities.
- Search chunks.
- Search relationships.
- Hybrid search.
- Deep search.

Use for agent tools where a prompt should retrieve scoped knowledge.

## Ingest Plugin

`GraphRagIngestPlugin` extends search plugin behavior and adds ingestion-related
tooling. It resolves `IKnowledgeIngestionService` from DI.

Use for controlled administrative or automation agents that are allowed to ingest
documents. Avoid exposing ingestion tools to general chat agents unless the
workflow has explicit authorization and review.

## Search Agent

`GraphRagSearchAgent` is an agent wrapper for scoped knowledge access. Configure
allowed scopes in the agent configuration.

Use when a dedicated agent should answer questions from a bounded knowledge graph
scope.

## Best Practices

- Register `AddGraphRagServices` once in the host.
- Configure `AllowedScopes` per agent/plugin instance.
- Keep ingestion tools separate from general query tools when possible.
- Add audit context where app workflows know actor/user IDs.
- Use domains/categories to improve relevance and provenance, not authorization.
- Prefer `IKnowledgeSearchService` directly for application APIs that do not need
  an LLM tool wrapper.


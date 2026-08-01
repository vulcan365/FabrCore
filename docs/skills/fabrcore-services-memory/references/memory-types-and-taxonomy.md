# Memory Types and Taxonomy

## Philosophy

Memory types classify what a memory IS, not where it came from. This matters for graph structure (nodes and edges), retrieval filtering, and compaction decisions.

The four types are designed for general-purpose agents across any domain. They reflect durability and function:

- **Facts** are stable anchors — other memories link to them
- **Rules** define relationships and constraints between facts
- **Instructions** are authoritative directives that persist until revoked
- **Observations** are candidates — they may prove durable or get pruned

## Memory Types

### Fact

Verified truths, domain knowledge, system behaviors, established states.

Facts are stable. They rarely change. They serve as graph nodes that other memories (Rules, Instructions, Observations) link to.

**Examples across domains:**
- Customer support: "Customer account 4521 has been on the Enterprise plan since January 2024"
- Manufacturing: "Chiller X-100 requires a 30-minute warmup before calibration readings are reliable"
- Healthcare: "Lab results from the external provider take 48-72 hours to appear in the portal"
- Operations: "The staging environment shares a database with QA"

### Rule

Business rules, constraints, policies, conventions, conditions that govern decisions.

Rules define relationships between facts. In the graph, they often form edges: a Rule `APPLIES_TO` or `CONSTRAINS` one or more Facts.

**Examples across domains:**
- Customer support: "Refunds over $500 require manager approval before processing"
- Manufacturing: "Safety shutdown must trigger if temperature exceeds 180F for more than 60 seconds"
- Legal: "Client communications about pending litigation must be reviewed by counsel before sending"
- Operations: "All API responses must use camelCase; the database uses snake_case"

### Instruction

User directives, preferences, standing orders, explicit guidance.

Instructions persist until explicitly revoked or superseded. They are authoritative — during compaction, Instructions are protected from staleness pruning because they represent deliberate user intent, not time-bound observations.

**Examples across domains:**
- Customer support: "Always check order status before offering a refund"
- Manufacturing: "Include safety warnings when discussing chemical processes"
- General: "Prefer concise answers over detailed explanations unless asked for depth"
- General: "Ask before taking any irreversible action"

### Observation

Patterns noticed, inferences, situational context, unverified assessments.

Observations are the least durable type. They are candidates — they may be promoted to Facts if verified, used to inform Rules, or pruned during compaction if they become stale.

**Examples across domains:**
- Customer support: "Customer volume appears to increase significantly on Mondays"
- Manufacturing: "The current production run seems to have a higher-than-usual defect rate"
- Operations: "The third-party API has been responding slower than usual this week"
- General: "The user seems to prefer working through problems step by step"

## Type Enforcement

`MemoryTaxonomyRules.Validate()` checks that the memory type is in `AgentMemoryOptions.AllowedMemoryTypes`. Default allows all four.

```csharp
// Restrict to only Facts and Rules for a read-only knowledge agent
options.AllowedMemoryTypes = new() { MemoryType.Fact, MemoryType.Rule };
```

Content validation is not performed by the library. What counts as a "good memory" depends on the agent's domain and the extraction prompt.

## How Types Affect Compaction

During consolidation, types influence decisions:

| Type | Staleness pruning | Contradiction resolution |
|------|-------------------|--------------------------|
| **Fact** | Pruned only if clearly outdated | Newer facts supersede older ones |
| **Rule** | Pruned only if conditions are no longer active | Specific rules supersede general ones |
| **Instruction** | **Never pruned** — persists until revoked | More recent instructions supersede earlier ones |
| **Observation** | Most aggressively pruned when old | Easily superseded by facts or rules |

## Graph Relationships

Types map naturally to knowledge graph patterns:

```
Instruction ──CONSTRAINS──▶ Fact
Rule ──APPLIES_TO──▶ Fact
Observation ──SUPPORTS──▶ Fact
Observation ──SUGGESTS──▶ Rule
Fact ──RELATES_TO──▶ Fact
```

This structure means vector search finds relevant Facts, then graph traversal discovers the Rules and Instructions that apply to them.

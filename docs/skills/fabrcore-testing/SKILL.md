---
name: fabrcore-testing
description: >
  Test and evaluate FabrCore agents — in-memory test host, mock and live LLM modes,
  FabrCoreTestHarness, FakeChatClient, TestFabrCoreAgentHost, MSTest patterns,
  and LLM evaluation with Microsoft.Extensions.AI.Evaluation quality/safety/NLP metrics.
  Triggers on: "test agent", "test FabrCore", "FabrCoreTestHarness", "TestFabrCoreAgentHost",
  "FakeChatClient", "TestChatClientService", "mock LLM", "agent test", "unit test agent",
  "integration test agent", "test harness", "mock chat client", "deterministic response",
  "WithSequentialResponses", "WithTextResponse", "CreateMockAgent", "CreateLiveAgent",
  "test plugin", "MSTest FabrCore", "LLM eval", "evaluation", "evaluator", "eval agent",
  "RelevanceEvaluator", "CoherenceEvaluator", "FluencyEvaluator", "GroundednessEvaluator",
  "CompositeEvaluator", "EvaluationResult", "ScenarioRun", "ReportingConfiguration",
  "quality eval", "safety eval", "BLEU", "agent evaluation", "eval metrics".
  Do NOT use for: agent development — use fabrcore-agent.
  Do NOT use for: server/client setup — use fabrcore-server or fabrcore-client.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Testing Skill

Test FabrCore agents and libraries using MSTest with a lightweight in-memory test host — no Orleans silo required.

## Quick Reference

| Component | Purpose |
|-----------|---------|
| `TestFabrCoreAgentHost` | In-memory `IFabrCoreAgentHost` — replaces Orleans grain for testing |
| `FakeChatClient` | Deterministic `IChatClient` with sequential response support |
| `TestChatClientService` | Dual-mode `IFabrCoreChatClientService` (mock or live LLM) |
| `FabrCoreTestHarness` | Wires DI, creates agents, provides `InitializeAgent`/`SendMessage` helpers |

## Architecture

```
┌─────────────────────────────────┐
│  Test (MSTest)                  │
│  FabrCoreTestHarness            │
├─────────────────────────────────┤
│  Your Agent (FabrCoreAgentProxy)│  ← Same production code
├─────────────────────────────────┤
│  TestFabrCoreAgentHost          │  ← Replaces AgentGrain/Orleans
│  TestChatClientService          │  ← Mock or live IChatClient
│  FakeChatClient                 │  ← Deterministic responses
└─────────────────────────────────┘
```

## Two Testing Modes

### Mock Mode (Unit/Deterministic)
- Uses `FakeChatClient` — no LLM calls, fast, offline
- Tests routing logic, JSON parsing, error handling
- Run with: `dotnet test`

### Live Mode (Integration/Eval)
- Uses real LLM via `fabrcore.json` configuration
- **No FabrCore Host API required** — reads model configs and API keys directly from fabrcore.json
- Creates chat clients locally (Azure, OpenAI, Grok, Gemini, OpenRouter)
- Tests actual agent behavior with real AI responses
- Tagged with `[TestCategory("Integration")]`
- Run with: `dotnet test --filter TestCategory=Integration`
- Skips gracefully if no API key: `Assert.Inconclusive()`

## Setting Up a Test Project

### 1. Create Project

```xml
<!-- FabrCore.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MSTest" Version="3.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\YourAgentProject\YourAgentProject.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="fabrcore.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

### 2. Copy Infrastructure Files

Copy these from the `assets/` directory into your test project's `Infrastructure/` folder:
- `TestFabrCoreAgentHost.cs` — In-memory `IFabrCoreAgentHost`
- `FakeChatClient.cs` — Deterministic `IChatClient` for mock tests
- `TestChatClientService.cs` — Dual-mode `IFabrCoreChatClientService` (mock or live)
- `FabrCoreTestHarness.cs` — DI wiring and agent creation helpers
- `CustomEvaluator.cs` (optional) — Example custom `IEvaluator` implementation for LLM evals

### 3. Add InternalsVisibleTo (Optional)

To test internal methods, add to the agent project's `.csproj`:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="YourTestProject" />
</ItemGroup>
```

## Writing Agent Tests

### Mock Mode Test

```csharp
[TestClass]
public class MyAgentTests
{
    [TestMethod]
    public async Task OnMessage_ReturnsExpectedResponse()
    {
        using var harness = new FabrCoreTestHarness();

        // Configure sequential LLM responses
        var chatClient = FakeChatClient.WithSequentialResponses(
            """{"effort": "small", "reasoning": "Simple question"}""",
            "The answer is 42."
        );

        var agent = harness.CreateMockAgent<MyAgent>(chatClient);
        await harness.InitializeAgent(agent);

        var response = await harness.SendMessage(agent, "What is the answer?");

        Assert.IsNotNull(response.Message);
        Assert.IsTrue(response.Message.Contains("42"));
    }
}
```

### Live Integration Test

```csharp
[TestClass]
[TestCategory("Integration")]
public class MyAgentIntegrationTests
{
    [TestMethod]
    public async Task OnMessage_ProducesCoherentResponse()
    {
        using var harness = new FabrCoreTestHarness();
        var agent = harness.CreateLiveAgent<MyAgent>();

        if (agent is null)
        {
            Assert.Inconclusive("Requires fabrcore.json with valid API keys.");
            return;
        }

        await harness.InitializeAgent(agent);
        var response = await harness.SendMessage(agent, "What is the capital of France?");

        Assert.IsNotNull(response.Message);
        Assert.IsTrue(response.Message.Contains("Paris", StringComparison.OrdinalIgnoreCase));
    }
}
```

## FakeChatClient Factory Methods

| Method | Use Case |
|--------|----------|
| `WithTextResponse(text)` | Always returns the same text |
| `WithJsonResponse(json)` | Always returns JSON (alias for WithTextResponse) |
| `WithSequentialResponses(r1, r2, ...)` | Returns r1 on first call, r2 on second, etc. |

## FabrCoreTestHarness API

| Method | Description |
|--------|-------------|
| `CreateMockAgent<T>(chatClient, config?)` | Creates agent with mock LLM |
| `CreateLiveAgent<T>(config?, jsonPath?)` | Creates agent with real LLM (returns null if unavailable) |
| `InitializeAgent(agent)` | Calls `OnInitialize()` |
| `SendMessage(agent, text, fromHandle?)` | Calls `OnMessage()` with a properly formed `AgentMessage` |
| `InitializeAndMessage(agent, text)` | Convenience: init + send in one call |
| `AgentHost` | Access the `TestFabrCoreAgentHost` for assertions |

## TestFabrCoreAgentHost Handle Methods

`TestFabrCoreAgentHost` implements all handle methods from `IFabrCoreAgentHost`:

```csharp
// Default handle is "test-agent" (no owner)
var harness = new FabrCoreTestHarness();
var host = harness.AgentHost;
host.GetHandle();        // "test-agent"
host.GetOwnerHandle();   // ""
host.GetAgentHandle();   // "test-agent"
host.HasOwner();         // false

// With owner-scoped handle
var harness2 = new FabrCoreTestHarness(new() { Handle = "user1:my-agent" });
var host2 = harness2.AgentHost;
host2.GetOwnerHandle();  // "user1"
host2.GetAgentHandle();  // "my-agent"
host2.HasOwner();        // true
```

## TestFabrCoreAgentHost Assertions

```csharp
// Check messages sent by the agent to other agents
Assert.AreEqual(1, harness.AgentHost.SentMessages.Count);

// Check events sent
Assert.AreEqual(0, harness.AgentHost.SentEvents.Count);

// Check timers/reminders registered
CollectionAssert.Contains(harness.AgentHost.RegisteredTimers, "my-timer");
```

## Running Tests

```bash
# All mock tests (fast, no API key needed)
dotnet test --filter "TestCategory!=Integration"

# Integration tests only (requires fabrcore.json with API key)
dotnet test --filter "TestCategory=Integration"

# All tests
dotnet test
```

## fabrcore.json for Live Tests

Place in test project root (copied to output via `<CopyToOutputDirectory>`).
The test harness reads this file directly — **no FabrCore Host API needs to be running**.

Supported providers: `Azure`, `OpenAI`, `OpenRouter`, `Grok`, `Gemini`.

```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "Azure",
      "Uri": "https://your-resource.cognitiveservices.azure.com/",
      "Model": "gpt-4o",
      "ApiKeyAlias": "default-key",
      "TimeoutSeconds": 180,
      "MaxOutputTokens": null,
      "ContextWindowTokens": 128000
    }
  ],
  "ApiKeys": [
    { "Alias": "default-key", "Value": "your-api-key-here" }
  ]
}
```

**Important:** If the file contains `REPLACE_WITH` or `YOUR_API_KEY`, live tests skip with `Assert.Inconclusive()`.

## Testing Data.Intelligence Specifications

For projects using FabrCore.Data.Intelligence, create test entities and specifications:

```csharp
// Test entity
public class Product { public int Id { get; set; } public string Name { get; set; } = ""; }

// Test specification
[SpecificationFor<Product>]
public class ProductByNameSpec : ByStringSpecification<Product>
{
    public ProductByNameSpec(string name) : base(name, p => p.Name) { }
}

// Test
[TestMethod]
public void Spec_FiltersByName()
{
    var spec = new ProductByNameSpec("Widget");
    var product = new Product { Name = "Widget" };
    Assert.IsTrue(spec.Criteria.Compile()(product));
}
```

Use `Microsoft.EntityFrameworkCore.InMemory` for `SpecificationEvaluator` tests:
```csharp
var options = new DbContextOptionsBuilder<TestDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString())
    .Options;
var db = new TestDbContext(options);
// Seed and query with SpecificationEvaluator<T>.GetQuery()
```

---

## LLM Evaluation with Microsoft.Extensions.AI.Evaluation

Use the `Microsoft.Extensions.AI.Evaluation.*` libraries to evaluate the quality, safety, and accuracy of your agent's LLM responses. These evaluators score real LLM output using AI-judged or algorithmic metrics — no manual review needed.

### NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.AI.Evaluation` | Core abstractions (`IEvaluator`, `EvaluationResult`, `EvaluationMetric`) |
| `Microsoft.Extensions.AI.Evaluation.Quality` | Quality evaluators (Relevance, Coherence, Fluency, Groundedness, etc.) |
| `Microsoft.Extensions.AI.Evaluation.Safety` | Content safety evaluators (Hate, Violence, Self-Harm, Sexual) |
| `Microsoft.Extensions.AI.Evaluation.NLP` | Algorithmic NLP metrics (BLEU, GLEU, F1) |
| `Microsoft.Extensions.AI.Evaluation.Reporting` | Result storage, response caching, HTML/JSON report generation |

Add to your test project:
```xml
<PackageReference Include="Microsoft.Extensions.AI.Evaluation" Version="*" />
<PackageReference Include="Microsoft.Extensions.AI.Evaluation.Quality" Version="*" />
<PackageReference Include="Microsoft.Extensions.AI.Evaluation.Reporting" Version="*" />
<!-- Optional: -->
<PackageReference Include="Microsoft.Extensions.AI.Evaluation.Safety" Version="*" />
<PackageReference Include="Microsoft.Extensions.AI.Evaluation.NLP" Version="*" />
```

### Core Concepts

| Type | Purpose |
|------|---------|
| `IEvaluator` | Interface for all evaluators — takes messages + response, returns `EvaluationResult` |
| `EvaluationResult` | Dictionary of named `EvaluationMetric` values |
| `NumericMetric` | Score (e.g., 1-5 for quality, 0-1 for NLP) |
| `BooleanMetric` | Pass/fail result |
| `StringMetric` | Free-text result |
| `ChatConfiguration` | Wraps an `IChatClient` used by LLM-based evaluators to judge responses |
| `EvaluationContext` | Additional context passed to evaluators (grounding text, retrieved chunks, ground truth) |
| `CompositeEvaluator` | Runs multiple evaluators concurrently in a single call |

### Available Quality Evaluators

All quality evaluators are LLM-based (require a `ChatConfiguration` with a chat client, optimized for GPT-4o) and return `NumericMetric` scores on a 1-5 scale:

| Evaluator | Metric Name | What It Measures | Required Context |
|-----------|-------------|------------------|------------------|
| `RelevanceEvaluator` | `"Relevance"` | How well the response addresses the question | None |
| `FluencyEvaluator` | `"Fluency"` | Grammar, vocabulary, sentence structure | None |
| `CoherenceEvaluator` | `"Coherence"` | Logical flow, readability, idea organization | None |
| `GroundednessEvaluator` | `"Groundedness"` | Whether response is grounded in provided context | `GroundednessEvaluatorContext` |
| `RetrievalEvaluator` | `"Retrieval"` | Relevance and ranking of retrieved chunks (RAG) | `RetrievalEvaluatorContext` |
| `EquivalenceEvaluator` | `"Equivalence"` | Similarity to a ground truth reference answer | `EquivalenceEvaluatorContext` |
| `CompletenessEvaluator` | `"Completeness"` | Whether all aspects of the question are addressed | None |
| `TaskAdherenceEvaluator` | `"Task Adherence"` | Whether the response follows task instructions | None |
| `ToolCallAccuracyEvaluator` | `"Tool Call Accuracy"` | Whether the correct tools were called | None |
| `IntentResolutionEvaluator` | `"Intent Resolution"` | AI system's effectiveness at identifying user intent (agent-focused) | None |
| `RelevanceTruthAndCompletenessEvaluator` | `"Relevance (RTC)"`, `"Truth (RTC)"`, `"Completeness (RTC)"` | Combined multi-metric evaluator (experimental) | None |

### Available NLP Evaluators

Algorithmic (no LLM needed), return `NumericMetric` scores 0.0-1.0:

| Evaluator | Metric Name | What It Measures | Required Context |
|-----------|-------------|------------------|------------------|
| `BLEUEvaluator` | `"BLEU"` | N-gram overlap with reference texts | `BLEUEvaluatorContext` |
| `GLEUEvaluator` | `"GLEU"` | Google BLEU variant | Similar to BLEU |
| `F1Evaluator` | `"F1"` | Token-level F1 score vs reference | Similar to BLEU |

### Available Safety Evaluators

Require Azure AI Foundry, return `NumericMetric` severity scores (0-7, lower is safer):

| Evaluator | Metric Name |
|-----------|-------------|
| `HateAndUnfairnessEvaluator` | `"Hate and Unfairness"` |
| `ViolenceEvaluator` | `"Violence"` |
| `SelfHarmEvaluator` | `"Self Harm"` |
| `SexualEvaluator` | `"Sexual"` |
| `GroundednessProEvaluator` | `"Groundedness Pro"` — fine-tuned model for grounding checks |
| `ProtectedMaterialEvaluator` | `"Protected Material"` — copyrighted/licensed content detection |
| `UngroundedAttributesEvaluator` | `"Ungrounded Attributes"` — detects hallucinated human attributes |
| `CodeVulnerabilityEvaluator` | `"Code Vulnerability"` — code security issues |
| `IndirectAttackEvaluator` | `"Indirect Attack"` — indirect prompt injection detection |
| `ContentHarmEvaluator` | Single-shot evaluation for all four harm metrics above |

### Basic Evaluation (No Reporting)

The simplest way to evaluate a single response — use evaluators directly:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

[TestClass]
[TestCategory("Evaluation")]
public class MyAgentEvalTests
{
    [TestMethod]
    public async Task Agent_Response_MeetsQualityThresholds()
    {
        // 1. Set up a chat client for the evaluator to use as the AI judge
        //    (this is the evaluator's LLM, not your agent's LLM)
        using var harness = new FabrCoreTestHarness();
        var agent = harness.CreateLiveAgent<MyAgent>();
        if (agent is null)
        {
            Assert.Inconclusive("Requires fabrcore.json with valid API keys.");
            return;
        }

        // 2. Get a real response from the agent
        await harness.InitializeAgent(agent);
        var response = await harness.SendMessage(agent, "Explain how photosynthesis works.");

        // 3. Create evaluators
        var evaluatorChatClient = await harness.GetChatClient("default");
        var chatConfig = new ChatConfiguration(evaluatorChatClient);

        var evaluator = new CompositeEvaluator(
            new RelevanceEvaluator(),
            new FluencyEvaluator(),
            new CoherenceEvaluator());

        // 4. Run evaluation
        var result = await evaluator.EvaluateAsync(
            "Explain how photosynthesis works.",  // user request
            response.Message!,                     // model response
            chatConfig);

        // 5. Assert quality thresholds
        Assert.IsTrue(result.Get<NumericMetric>("Relevance").Value >= 3.0,
            $"Relevance: {result.Get<NumericMetric>("Relevance").Value} (reason: {result.Get<NumericMetric>("Relevance").Reason})");
        Assert.IsTrue(result.Get<NumericMetric>("Fluency").Value >= 3.0,
            $"Fluency: {result.Get<NumericMetric>("Fluency").Value}");
        Assert.IsTrue(result.Get<NumericMetric>("Coherence").Value >= 3.0,
            $"Coherence: {result.Get<NumericMetric>("Coherence").Value}");
    }
}
```

### Evaluation with Grounding Context (RAG Scenarios)

When your agent uses retrieval-augmented generation, evaluate whether the response is grounded in the retrieved context:

```csharp
[TestMethod]
public async Task Agent_Response_IsGroundedInContext()
{
    // ... set up agent and get response ...

    var groundingContext = """
        Photosynthesis is a process used by plants to convert light energy
        into chemical energy. It occurs primarily in chloroplasts using
        chlorophyll. The process converts CO2 and water into glucose and oxygen.
        """;

    var evaluator = new GroundednessEvaluator();
    var chatConfig = new ChatConfiguration(evaluatorChatClient);

    var result = await evaluator.EvaluateAsync(
        "Explain how photosynthesis works.",
        response.Message!,
        chatConfig,
        additionalContext: [new GroundednessEvaluatorContext(groundingContext)]);

    var groundedness = result.Get<NumericMetric>("Groundedness");
    Assert.IsTrue(groundedness.Value >= 3.0,
        $"Groundedness: {groundedness.Value}/5 — {groundedness.Reason}");
}
```

### Evaluation with Ground Truth (Equivalence)

Compare the agent's response against a known-good reference answer:

```csharp
[TestMethod]
public async Task Agent_Response_MatchesExpectedAnswer()
{
    // ... set up agent and get response ...

    var evaluator = new EquivalenceEvaluator();
    var chatConfig = new ChatConfiguration(evaluatorChatClient);

    var groundTruth = "Paris is the capital of France.";

    var result = await evaluator.EvaluateAsync(
        "What is the capital of France?",
        response.Message!,
        chatConfig,
        additionalContext: [new EquivalenceEvaluatorContext(groundTruth)]);

    Assert.IsTrue(result.Get<NumericMetric>("Equivalence").Value >= 4.0);
}
```

### Evaluation with Reporting Pipeline

For systematic evals across multiple scenarios with result storage, response caching, and HTML report generation. Use `DiskBasedReportingConfiguration.Create()` for the simplest setup:

```csharp
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

[TestClass]
[TestCategory("Evaluation")]
public class AgentEvalSuite
{
    public TestContext? TestContext { get; set; }

    private string ScenarioName =>
        $"{TestContext!.FullyQualifiedTestClassName}.{TestContext.TestName}";

    private static readonly string s_executionName =
        $"{DateTime.Now:yyyyMMddTHHmmss}";

    private static readonly ReportingConfiguration s_reportingConfig =
        DiskBasedReportingConfiguration.Create(
            storageRootPath: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FabrCoreEvals"),
            evaluators: [
                new RelevanceEvaluator(),
                new FluencyEvaluator(),
                new CoherenceEvaluator(),
                new GroundednessEvaluator()
            ],
            chatConfiguration: GetEvalChatConfiguration(),
            enableResponseCaching: true,
            executionName: s_executionName,
            tags: ["nightly", "quality"]);

    private static ChatConfiguration GetEvalChatConfiguration()
    {
        // Use the same fabrcore.json approach to create a chat client for the AI judge
        // Or create directly:
        // var client = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(key))
        //     .GetChatClient("gpt-4o").AsIChatClient();
        // return new ChatConfiguration(client);

        // Example using FabrCore's TestChatClientService:
        var jsonPath = "fabrcore.json";
        var json = File.ReadAllText(jsonPath);
        var config = JsonSerializer.Deserialize<FabrCoreConfiguration>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var service = new TestChatClientService(config,
            LoggerFactory.Create(b => b.AddConsole()));
        var client = service.GetChatClient("default").Result;
        return new ChatConfiguration(client);
    }

    [TestMethod]
    public async Task Scenario_PhotosynthesisQuestion()
    {
        // 1. Create a scenario run (use await using to auto-persist results on dispose)
        await using var scenarioRun = await s_reportingConfig.CreateScenarioRunAsync(
            ScenarioName);

        // 2. Get response from your FabrCore agent
        using var harness = new FabrCoreTestHarness();
        var agent = harness.CreateLiveAgent<MyAgent>();
        if (agent is null) { Assert.Inconclusive("No API key"); return; }

        await harness.InitializeAgent(agent);
        var agentResponse = await harness.SendMessage(agent,
            "Explain how photosynthesis works.");

        // 3. Build chat messages and model response for the evaluator
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "Explain how photosynthesis works.")
        };
        var modelResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, agentResponse.Message));

        // 4. Evaluate with optional grounding context
        var result = await scenarioRun.EvaluateAsync(
            messages, modelResponse,
            additionalContext: [
                new GroundednessEvaluatorContext(
                    "Photosynthesis converts CO2 and water into glucose and oxygen using sunlight.")
            ]);

        // 5. Assert using interpretation
        var relevance = result.Get<NumericMetric>("Relevance");
        Assert.IsFalse(relevance.Interpretation!.Failed, relevance.Reason);
        Assert.IsTrue(relevance.Interpretation.Rating
            is EvaluationRating.Good or EvaluationRating.Exceptional);

        // ScenarioRun.DisposeAsync() automatically persists results to disk
    }
}
```

### Generating HTML Reports

After running evaluation tests, generate a report using the `dotnet aieval` CLI tool:

```bash
# Install the tool (once)
dotnet tool install --local Microsoft.Extensions.AI.Evaluation.Console

# Generate HTML report from stored results
dotnet tool run aieval report --path <path/to/your/storage> --output report.html

# Open the report
start report.html
```

The report shows all scenarios, metrics, scores, and trends across executions.

### Evaluation with NLP Metrics (No LLM Required)

Use algorithmic evaluators for fast, deterministic scoring against reference texts:

```csharp
using Microsoft.Extensions.AI.Evaluation.NLP;

[TestMethod]
public async Task Agent_Response_HasHighBLEUScore()
{
    // ... get agent response ...

    var evaluator = new BLEUEvaluator();

    var references = new BLEUEvaluatorContext(
    [
        "Paris is the capital of France.",
        "The capital city of France is Paris."
    ]);

    var result = await evaluator.EvaluateAsync(
        "What is the capital of France?",
        response.Message!,
        additionalContext: [references]);

    var bleu = result.Get<NumericMetric>("BLEU");
    Assert.IsTrue(bleu.Value >= 0.5, $"BLEU: {bleu.Value}");
}
```

### Accessing Evaluation Results

```csharp
var result = await evaluator.EvaluateAsync(...);

// Get a specific metric (throws if not found)
var relevance = result.Get<NumericMetric>("Relevance");
Console.WriteLine($"Score: {relevance.Value}/5");
Console.WriteLine($"Reason: {relevance.Reason}");
Console.WriteLine($"Rating: {relevance.Interpretation?.Rating}"); // EvaluationRating enum
Console.WriteLine($"Failed: {relevance.Interpretation?.Failed}");

// Try-get pattern (safe)
if (result.TryGet<NumericMetric>("Fluency", out var fluency))
{
    Console.WriteLine($"Fluency: {fluency.Value}");
}

// Iterate all metrics
foreach (var (name, metric) in result.Metrics)
{
    Console.WriteLine($"{name}: {metric}");
}

// Check diagnostics for errors
if (metric.Diagnostics?.Any() == true)
{
    foreach (var diag in metric.Diagnostics)
        Console.WriteLine($"Diagnostic: {diag}");
}
```

### Metric Interpretation

Metrics carry an `EvaluationMetricInterpretation` with a `Rating`, `Failed` flag, and `Reason`:

```csharp
public enum EvaluationRating
{
    Unknown,        // Value is unknown
    Inconclusive,   // Cannot interpret conclusively
    Unacceptable,   // Unacceptable result
    Poor,           // Below expectations
    Average,        // Meets minimum bar
    Good,           // Meets expectations
    Exceptional     // Exceeds expectations
}
```

```csharp
// Check interpretation
var relevance = result.Get<NumericMetric>("Relevance");
Assert.IsFalse(relevance.Interpretation!.Failed, relevance.Reason);
Assert.IsTrue(relevance.Interpretation.Rating is EvaluationRating.Good or EvaluationRating.Exceptional);
```

Built-in default thresholds:

| Evaluator Type | Good/Exceptional | Unacceptable/Poor |
|---------------|------------------|-------------------|
| Quality (1-5) | >= 3.0 | < 3.0 |
| NLP (0-1) | >= 0.5 | < 0.5 |
| Safety (0-7) | 0-2 (safe) | 3+ (unsafe) |

### Running Evaluation Tests

```bash
# All eval tests
dotnet test --filter "TestCategory=Evaluation"

# Specific scenario
dotnet test --filter "FullyQualifiedName~Scenario_PhotosynthesisQuestion"
```

### Writing a Custom Evaluator

Implement `IEvaluator` for domain-specific metrics. See `assets/custom-evaluator.cs` for a complete example:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

public class MyCustomEvaluator : IEvaluator
{
    public const string MetricName = "MyMetric";
    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        // Your evaluation logic here
        double score = /* compute score */;

        var metric = new NumericMetric(MetricName, value: score,
            reason: "Explanation of the score");

        // Attach interpretation
        metric.Interpretation = new EvaluationMetricInterpretation(
            score >= 0.8 ? EvaluationRating.Good : EvaluationRating.Poor,
            failed: score < 0.5,
            reason: $"Score was {score:F2}");

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
```

Use custom evaluators alongside built-in ones via `CompositeEvaluator` or in `ReportingConfiguration`.

### Tips for FabrCore Agent Evals

1. **Evaluator LLM vs Agent LLM** — The `ChatConfiguration` for evaluators is the *judge* LLM (typically GPT-4o). This is separate from your agent's LLM configured via `fabrcore.json`. You can use the same model or a different one.

2. **Use `CompositeEvaluator`** — Run multiple evaluators in a single call for efficiency. They execute concurrently.

3. **Tag with `[TestCategory("Evaluation")]`** — Keep evals separate from unit tests since they require real LLM calls and are slower.

4. **Response caching** — Use `DiskBasedResponseCacheProvider` in the reporting pipeline to avoid re-calling the LLM judge on repeated test runs with the same inputs.

5. **Use the test harness** — `FabrCoreTestHarness.CreateLiveAgent<T>()` handles all the wiring. Get the response via `SendMessage()`, then pass it to the evaluator pipeline.

6. **Grounding context for RAG agents** — If your agent retrieves context before answering, pass that context to `GroundednessEvaluator` via `GroundednessEvaluatorContext` to evaluate whether the response stays grounded in the retrieved data.

### References

- [Microsoft.Extensions.AI.Evaluation docs](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries)
- [Evaluate with reporting tutorial](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/evaluate-with-reporting)
- [API usage examples (dotnet/ai-samples)](https://github.com/dotnet/ai-samples/blob/main/src/microsoft-extensions-ai-evaluation/api/)
- NuGet: [Microsoft.Extensions.AI.Evaluation](https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation)
- NuGet: [Microsoft.Extensions.AI.Evaluation.Quality](https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation.Quality)
- NuGet: [Microsoft.Extensions.AI.Evaluation.Reporting](https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation.Reporting)

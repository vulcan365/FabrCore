using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk;
using FabrCore.Sdk.VerifiableExecution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.SampleApp.Surface;

[PluginAlias(Alias)]
[Description("In-memory fake domain data plugin for SurfaceApp demo leaf agents.")]
[FabrCoreCapabilities("Returns and mutates seeded fake domain records, handoff guidance, and decision rules while recording demo DB effects for verifiable execution.")]
[FabrCoreNote("Demo-only plugin. It does not call a real API or database, but records in-memory fake operations as external effects when verifiable execution is enabled.")]
public sealed class SurfaceDemoDomainPlugin : IFabrCorePlugin
{
    public const string Alias = "surface-demo-domain-data";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private InMemorySurfaceDemoDomainStore store = default!;
    private IVerifiableExecutionContext? evidence;
    private ILogger<SurfaceDemoDomainPlugin> logger = default!;
    private string agentHandle = string.Empty;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        store = serviceProvider.GetRequiredService<InMemorySurfaceDemoDomainStore>();
        evidence = serviceProvider.GetService<IVerifiableExecutionContext>();
        logger = serviceProvider.GetRequiredService<ILogger<SurfaceDemoDomainPlugin>>();

        var agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        agentHandle = agentHost.GetHandle();
        if (string.IsNullOrWhiteSpace(agentHandle))
        {
            agentHandle = config.Handle ?? "surface-demo-domain-agent";
        }

        store.Seed(agentHandle, BuildSeed(config));
        return Task.CompletedTask;
    }

    [Description("Get this specialist's seeded fake domain profile, responsibilities, records, decision rules, and handoff guidance. Call this before answering broad domain questions.")]
    public async Task<string> GetDomainBrief()
    {
        var dataset = await RecordDbEffect(
            operation: "ReadDomainBrief",
            subject: agentHandle,
            effect: () => Task.FromResult(store.GetDataset(agentHandle)),
            metadata: Metadata("read", agentHandle));

        return ToJson(new
        {
            dataset.Domain,
            dataset.Profile,
            dataset.Responsibilities,
            Records = dataset.Records,
            dataset.Decisions,
            dataset.Handoffs,
            dataset.UpdatedUtc
        });
    }

    [Description("List fake domain records for this specialist. Use when the user asks what records exist or asks for recent demo facts.")]
    public async Task<string> ListDomainRecords(
        [Description("Maximum number of fake records to return, from 1 to 25.")] int limit = 10)
    {
        var records = await RecordDbEffect(
            operation: "ListDomainRecords",
            subject: agentHandle,
            effect: () => Task.FromResult(store.SearchRecords(agentHandle, search: null, limit)),
            metadata: Metadata("read", $"limit:{limit}"));

        return ToJson(new { Records = records });
    }

    [Description("Search fake domain records by ID, summary, or status. Use this before answering record-specific questions.")]
    public async Task<string> SearchDomainRecords(
        [Description("Search text such as a fake record ID, customer name, topic, or status.")] string search,
        [Description("Maximum number of fake records to return, from 1 to 25.")] int limit = 10)
    {
        var records = await RecordDbEffect(
            operation: "SearchDomainRecords",
            subject: search,
            effect: () => Task.FromResult(store.SearchRecords(agentHandle, search, limit)),
            metadata: Metadata("read", search));

        return ToJson(new { Search = search, Records = records });
    }

    [Description("Add a new fake in-memory domain record. Use only when the user asks to create a demo note, task, risk, or fake record.")]
    public async Task<string> AddDomainRecord(
        [Description("Short summary for the fake record.")] string summary,
        [Description("Optional fake status such as Open, Pending Review, At Risk, Blocked, or Resolved.")] string? status = null)
    {
        var record = await RecordDbEffect(
            operation: "AddDomainRecord",
            subject: VerifiableExecutionHash.HashText(summary),
            effect: () => Task.FromResult(store.AddRecord(agentHandle, summary, status)),
            metadata: Metadata("insert", summary, status));

        return ToJson(new { Message = "Demo record added.", Record = record });
    }

    [Description("Update an existing fake in-memory domain record's summary or status. Use only when the user asks to change demo state.")]
    public async Task<string> UpdateDomainRecord(
        [Description("Fake record ID such as LEAD-2048, OPP-7781, SKU-AX12, or INV-6201.")] string recordId,
        [Description("Optional replacement summary. Leave blank to keep the existing summary.")] string? summary = null,
        [Description("Optional replacement status such as Open, Pending Review, At Risk, Blocked, or Resolved.")] string? status = null)
    {
        var record = await RecordDbEffect(
            operation: "UpdateDomainRecord",
            subject: recordId,
            effect: () => Task.FromResult(store.UpdateRecord(agentHandle, recordId, summary, status)),
            metadata: Metadata("update", recordId, summary, status));

        return ToJson(new { Message = "Demo record updated.", Record = record });
    }

    [Description("Get seeded fake decision rules and suggested handoffs for this specialist. Use when the answer needs a next branch or escalation recommendation.")]
    public async Task<string> GetHandoffGuidance(
        [Description("Optional topic from the user request; used only for evidence metadata and response context.")] string? topic = null)
    {
        var dataset = await RecordDbEffect(
            operation: "ReadHandoffGuidance",
            subject: topic ?? agentHandle,
            effect: () => Task.FromResult(store.GetDataset(agentHandle)),
            metadata: Metadata("read", topic ?? agentHandle));

        return ToJson(new
        {
            Topic = topic,
            dataset.Decisions,
            dataset.Handoffs
        });
    }

    private async Task<T> RecordDbEffect<T>(
        string operation,
        string subject,
        Func<Task<T>> effect,
        IReadOnlyDictionary<string, string?> metadata)
    {
        if (evidence is null)
        {
            return await effect();
        }

        var result = await evidence.RecordDbEffectAsync(
            operation: operation,
            target: "SurfaceDemoDomainStore",
            subject: subject,
            effect: effect,
            metadata: metadata,
            logger: logger,
            cancellationToken: CancellationToken.None);

        return result.Value!;
    }

    private IReadOnlyDictionary<string, string?> Metadata(string operation, params string?[] values)
    {
        var parameterText = string.Join("|", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new Dictionary<string, string?>
        {
            ["db.system"] = "in-memory-demo",
            ["db.name"] = "SurfaceAppDemo",
            ["db.table"] = "DomainRecords",
            ["agent_handle_hash"] = VerifiableExecutionHash.HashText(agentHandle),
            ["operation"] = operation,
            ["parameter_hash"] = VerifiableExecutionHash.HashText(parameterText)
        };
    }

    private static SurfaceDemoDomainSeed BuildSeed(AgentConfiguration config)
    {
        var args = config.Args ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new SurfaceDemoDomainSeed
        {
            Domain = Read(args, SurfaceDemoDomainAgent.DomainArg, "Demo Operations"),
            Profile = Read(args, SurfaceDemoDomainAgent.ProfileArg, config.Description ?? config.Handle ?? "Domain Specialist"),
            Responsibilities = Split(Read(args, SurfaceDemoDomainAgent.ResponsibilitiesArg, "Triage the request; summarize fake operational facts; recommend the next squad handoff")),
            Records = Split(Read(args, SurfaceDemoDomainAgent.RecordsArg, "DEMO-001: Sample record awaiting review; DEMO-002: Sample record in progress")),
            Decisions = Split(Read(args, SurfaceDemoDomainAgent.DecisionsArg, "Use the most specific branch specialist; flag assumptions before handoff")),
            Handoffs = Split(Read(args, SurfaceDemoDomainAgent.HandoffsArg, "Escalate cross-domain questions to Assistant"))
        };
    }

    private static string Read(IReadOnlyDictionary<string, string> args, string key, string fallback)
        => args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static IReadOnlyList<string> Split(string value)
        => value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

    private static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);
}

// FabrCore server Program.cs — worker service registration example.

using FabrCore.Experimental.Worker.Configuration;

var builder = WebApplication.CreateBuilder(args);

// 1. Register FabrCore server (provides IFabrCoreChatClientService, IFabrCoreRegistry, etc.)
builder.Services.AddFabrCoreServer(options =>
{
    // ... your FabrCore server configuration
});

// 2. Register the worker addon service.
//    Options here apply across every WorkerService instance the provider hands out.
builder.Services.AddWorkerServices(options =>
{
    // Path to fabrcore-worker.json. Resolved relative to the process working directory.
    // Missing file → empty definition set (logged, not an error).
    options.DefinitionFilePath = "fabrcore-worker.json";

    // Hard cap on the extracted task list length. Tasks beyond the cap are dropped.
    options.MaxExtractedTasks = 12;

    // Per-SME consultation timeout (parallel fan-out). SMEs that exceed this
    // are logged and dropped — the pipeline does not wait for them.
    options.SmeConsultationTimeout = TimeSpan.FromSeconds(30);

    // Ceiling on WorkerDefinition.MaxRetries. ProcessAsync uses
    // min(definition.MaxRetries, AbsoluteMaxRetries). Worker is intentionally
    // not a replan loop — raise this only if you really need more retries.
    options.AbsoluteMaxRetries = 2;

    // Fallback model names per stage. Best practice is to set these so the
    // host's WorkerAgentFactory creates clean, tool-less analysis agents.
    // If you only have one model entry, use "default" for all four.
    options.DefaultExtractionModelName = "fast";       // or "default"
    options.DefaultValidationModelName = "planner";    // or "default"
    options.DefaultSmeRouterModelName = "fast";        // or "default"
    options.DefaultInternalAdvisorModelName = "planner"; // or "default"

    // When true (default), missing SmeReference.Description / GoodFor are filled
    // from IFabrCoreRegistry — class-level [Description] and [FabrCoreCapabilities]
    // attributes on the SME's agent type. Set to false to only use what
    // fabrcore-worker.json provides.
    options.EnrichSmeMetadataFromRegistry = true;
});

var app = builder.Build();
app.Run();

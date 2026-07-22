// TaskAgent registration template for Program.cs / Startup.
//
// Five things to wire:
//   1. Register TaskAgent assembly + your client agent assemblies with FabrCore
//   2. Call AddTaskAgentServices(...) to configure options
//   3. Call AddAgentMemoryServices(...) so Teaching/learning persist
//   4. After app.Build(), bring each client agent + SME online via
//      IFabrCoreAgentService.ConfigureAgentAsync
//   5. Configure one TaskAgent grain per themed scope, passing the definition
//      name in Args["TaskAgentDefinition"]
//
// The TaskAgent owns no domain tools — every task is delegated to a client
// agent listed in the definition's clientAgentHandles. Configure those agents
// with the plugins/tools they need to do real work.

using FabrCore.Agents.TaskAgent;
using FabrCore.Agents.TaskAgent.Configuration;
using FabrCore.Core;
using FabrCore.Experimental.Memory.Configuration;
using FabrCore.Host.Services;
using FabrCore.Sdk;

// =============================================================================
// 1. Register assemblies (TaskAgent + your client agent assemblies)
// =============================================================================

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies =
    [
        typeof(TaskAgent).Assembly,           // the TaskAgent itself
        typeof(MyClientAgent).Assembly,       // each client agent assembly
        typeof(MySmeAgent).Assembly,          // each SME agent assembly
    ]
});

// =============================================================================
// 2. Configure TaskAgent services
// =============================================================================

builder.Services.AddTaskAgentServices(opt =>
{
    // Path to the JSON file that lists themed agent definitions. Default is
    // "fabrcore-taskagent.json" in the working directory.
    opt.DefinitionFilePath = "fabrcore-taskagent.json";

    // Default model config names for each tier — used when a definition leaves
    // a tier unset. Each name must exist in fabrcore.json ModelConfigurations.
    opt.DefaultFastModelName = "default";       // intent triage / classification
    opt.DefaultWorkerModelName = "default";     // conversational answers / synthesis
    opt.DefaultPlannerModelName = "default";    // initial planning + replanning

    // Replan-loop safety detector. If MaxRecentReplans within
    // ReplanLoopWindowSeconds are all user-message-driven OR all
    // needs_adjustment, the agent flips IsStuck=true and asks the user
    // to clarify. The replan-loop threshold defaults to 3 unless overridden
    // per definition (replanLoopThreshold).
    opt.MaxRecentReplans = 5;
    opt.ReplanLoopWindowSeconds = 60;

    // Per-task delegation timeout. Picked under FabrCore's 5-minute
    // stale-message threshold so a hung delegation doesn't trigger
    // stale-message rerouting.
    opt.DefaultDelegationTimeout = TimeSpan.FromMinutes(4);

    // Triage-confidence floor. Below this, the agent asks the user to
    // clarify rather than guess (only when no plan is currently running).
    opt.ClarifyConfidenceThreshold = 0.55;
});

// =============================================================================
// 3. Configure memory services
// =============================================================================
// Required for Teaching messages to persist into long-term memory and for the
// replanner to recall active rules. Without this, the TaskAgent still runs but
// teaching messages get a "I noted it but it won't survive past this session"
// fallback ack.

builder.Services.AddAgentMemoryServices(memOpt =>
{
    memOpt.ConnectionStringName = "expmem";   // SQL Server 2025 / Azure SQL with VECTOR support

    // Optional model overrides per memory operation. Defaults are fine for
    // most cases; tune if you want consolidation/imagining on a different model.
    // memOpt.RelevanceModelName = "gpt-4o-mini";
    // memOpt.CompactionModelName = "gpt-4o";
    // memOpt.ImaginingModelName = "gpt-4o";

    // Whitelist memory categories the TaskAgent can write. Defaults to all five.
    // memOpt.AllowedMemoryTypes = new() { MemoryType.Rule, MemoryType.Instruction, MemoryType.Observation };
});

// =============================================================================
// 4. Build the app and bring client + SME agents online
// =============================================================================

var app = builder.Build();
app.UseFabrCoreServer();

var agentService = app.Services.GetRequiredService<IFabrCoreAgentService>();

// One ConfigureAgentAsync per alias listed in clientAgentHandles + subjectMatterExperts
// of every definition the TaskAgent grains will load.

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "job-execution-agent",
    AgentType = "job-execution-agent",
    Models = "default",
    SystemPrompt = "You execute manufacturing job operations.",
    Plugins = ["job", "scheduling"],
    Tools = ["status-update"]
});

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "data-fetcher",
    AgentType = "data-fetcher",
    Models = "default",
    SystemPrompt = "You fetch and aggregate data from internal systems.",
    Plugins = ["erp-lookup", "warehouse-query"]
});

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "job-policy-sme",
    AgentType = "job-policy-sme",
    Models = "default",
    SystemPrompt =
        "You are a subject-matter expert on job-shop manufacturing policy. " +
        "Answer consultation questions tersely and decisively.",
    Plugins = ["policy-knowledge-base"]
});

// =============================================================================
// 5. Configure TaskAgent grains — one per themed scope
// =============================================================================
// Each grain points at a definition from fabrcore-taskagent.json via
// Args["TaskAgentDefinition"]. You can run as many TaskAgents as you want,
// each scoped to different client agents and SMEs.

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "ops-supervisor",
    AgentType = "task-agent",                    // matches [AgentAlias("task-agent")]
    Models = "default",                          // unused — the three tier names override
    SystemPrompt = "Operations supervisor for job ops.",
    Args = new()
    {
        ["TaskAgentDefinition"] = "ops-agent"    // pick a definition by name
    }
});

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "research-supervisor",
    AgentType = "task-agent",
    Models = "default",
    SystemPrompt = "Research lead.",
    Args = new()
    {
        ["TaskAgentDefinition"] = "research-agent"
    }
});

// =============================================================================
// 6. ForceReconfigure — use after deploying new code
// =============================================================================
// FabrCore agents are permanent grains. After deploying changes to TaskAgent,
// client agents, or definitions, pass forceReconfigure: true on the next
// ConfigureAgentAsync to re-run OnInitialize on each grain. Without this,
// existing grains keep their old initialization until the silo restarts.
//
// Example (in your deployment hook):
//
//     await agentService.ConfigureAgentAsync("system",
//         new AgentConfiguration { Handle = "ops-supervisor", AgentType = "task-agent",
//             Args = new() { ["TaskAgentDefinition"] = "ops-agent" } },
//         forceReconfigure: true);

app.Run();

// Swarm registration template for Program.cs / Startup
//
// Three things to wire:
//   1. Register swarm + your client agent assemblies with FabrCore
//   2. Call AddSwarmOrchestration(...) to configure options
//   3. After the app is built, bring each client agent online via
//      IFabrCoreAgentService.ConfigureAgentAsync
//
// The swarm no longer maintains its own agent registry — client agents are
// listed by bare alias in fabrcore-swarm.json under SwarmDefinition.AgentHandles,
// and the swarm discovers their capabilities at runtime from IFabrCoreRegistry
// (class-level attributes on the agent class) + IFabrCoreAgentHost.GetAgentHealth
// (runtime configured plugins, tools, model, system prompt).

using FabrCore.Core;
using FabrCore.Experimental.Swarm.Agents;
using FabrCore.Experimental.Swarm.Configuration;
using FabrCore.Host.Services;
using FabrCore.Sdk;

// =============================================================================
// 1. Register assemblies (swarm library + your client agent assembly)
// =============================================================================

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies =
    [
        typeof(SwarmOrchestratorAgent).Assembly,   // swarm orchestrator, planner,
                                                    // supervisor, adapter worker,
                                                    // blackboard, factory
        typeof(MyDomainAgent).Assembly             // your client agent(s)
    ]
});

// =============================================================================
// 2. Configure swarm orchestration
// =============================================================================

builder.Services.AddSwarmOrchestration(options =>
{
    options.PlannerModelName = "default";
    options.MaxTasksPerSupervisor = 10;
    options.SwarmDefinitionFilePath = "fabrcore-swarm.json";

    // Drive loop tick interval (Orleans timer that enforces termination
    // policy and recovers stuck waves). Default is 5 seconds.
    options.DriveLoopInterval = TimeSpan.FromSeconds(5);

    // Default termination policy. Per-task limits are enforced by the drive loop.
    // Override per-swarm in fabrcore-swarm.json if needed.
    options.DefaultTermination.MaxTotalIterations = 100;
    options.DefaultTermination.MaxTaskRetries = 3;
    options.DefaultTermination.MaxWallClockTime = TimeSpan.FromHours(4);
    options.DefaultTermination.MaxTaskTime = TimeSpan.FromMinutes(30);
    options.DefaultTermination.MaxRoadblocks = 10;
    options.DefaultTermination.MaxReplanAttempts = 5;
});

// NOTE: There is no AddSwarmWorker() call. The old C# agent-registration API
// is gone — list your agent aliases in fabrcore-swarm.json under
// SwarmDefinition.AgentHandles instead.

// =============================================================================
// 3. Build the app
// =============================================================================

var app = builder.Build();

// =============================================================================
// 4. Bring your client agents online so the swarm can dispatch tasks to them
// =============================================================================
// The swarm discovers each listed agent's capabilities at runtime by calling
// IFabrCoreAgentHost.GetAgentHealth("{owner}:{alias}", Detailed) and combining
// it with IFabrCoreRegistry.GetAgentTypes() (class attributes). Those handles
// must be reachable when the adapter worker forwards the first task, so bring
// agents online here before app.Run().

var agentService = app.Services.GetRequiredService<IFabrCoreAgentService>();

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "my-domain-agent",
    AgentType = "my-domain-agent",
    Models = "default",
    Plugins = ["my-plugin"],
    Tools = ["my-tool"],
    SystemPrompt = "You are a domain expert that handles X, Y, and Z workflows."
});

// Repeat for each client agent listed in fabrcore-swarm.json AgentHandles...

// =============================================================================
// 5. (Optional) Pre-create scoped orchestrators using definitions from
//    fabrcore-swarm.json. Orchestrators are also created on demand via
//    SwarmClientExtensions.CreateSwarmAsync from a Blazor page.
//    The orchestrator provisions one long-lived worker per AgentHandle
//    in the definition during OnInitialize.
// =============================================================================

// await agentService.ConfigureSystemAgentAsync(new AgentConfiguration
// {
//     Handle = "pipe-swarm",
//     AgentType = "swarm-orchestrator",
//     Models = "default",
//     SystemPrompt = "You orchestrate pipe manufacturing jobs.",
//     Description = "Pipe manufacturing swarm",
//     Args = new() { ["SwarmDefinition"] = "pipe-manufacturing" }
// });

// =============================================================================
// 6. ForceReconfigure — use after deploying new code to force all swarm agents
//    (orchestrator, planner, supervisor, workers) to re-initialize. Without this,
//    existing grains keep their old OnInitialize state.
// =============================================================================

// From client-side Blazor (normal startup):
//   var handle = await context.CreateSwarmAsync("pipe-manufacturing");
//
// After deployment (force re-init):
//   var handle = await context.CreateSwarmAsync("pipe-manufacturing", forceReconfigure: true);

app.UseFabrCoreServer();
app.Run();

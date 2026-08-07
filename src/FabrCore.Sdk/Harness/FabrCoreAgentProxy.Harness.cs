#pragma warning disable MAAI001 // Harness providers (LoopAgent, BackgroundAgentsProvider, loop evaluators) are for evaluation purposes only and may change.
using System.Text.Json;
using FabrCore.Core.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

public abstract partial class FabrCoreAgentProxy
{
    /// <summary>Default loop iteration cap when <see cref="HarnessArgs.LoopMaxIterations"/> is not set.</summary>
    private const int DefaultHarnessLoopMaxIterations = 10;

    /// <summary>Default function-invocation cap when <see cref="HarnessArgs.MaxIterationsPerRequest"/> is not set.</summary>
    private const int DefaultHarnessMaxIterationsPerRequest = 40;

    /// <summary>
    /// Creates a harness agent — todo tracking, plan/execute modes, an iteration loop, and delegation to other FabrCore agents —
    /// wired to this agent's chat client, Orleans-backed chat history, and durable custom state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drop-in sibling of <c>CreateChatClientAgent</c>. Settings come from the agent's <c>_Harness*</c>
    /// args first, then from <paramref name="configure"/>, so a blueprint alone can produce a fully wired
    /// harness agent and code can still override anything.
    /// </para>
    /// <para>
    /// Unlike <c>CreateChatClientAgent</c>, the returned session is restored from durable state when one was
    /// persisted, so todos and delegation records survive grain deactivation. Run through
    /// <see cref="FabrCoreHarnessResult.RunAsync(string, AgentRunOptions?, CancellationToken)"/> so each turn
    /// snapshots the session on the way out.
    /// </para>
    /// </remarks>
    /// <param name="chatClientConfigName">Name of the chat client configuration (e.g. "default").</param>
    /// <param name="threadId">Conversation thread id. Also scopes the persisted harness session.</param>
    /// <param name="tools">Tools to make available, typically from <c>ResolveConfiguredToolsAsync</c>.</param>
    /// <param name="configure">Optional hook applied after args are read and before the agent is assembled.</param>
    protected async Task<FabrCoreHarnessResult> CreateFabrCoreHarnessAgent(
        string chatClientConfigName,
        string threadId,
        IList<AITool>? tools = null,
        Action<FabrCoreHarnessOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatClientConfigName);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var args = config.Args ?? new Dictionary<string, string>();
        var handle = fabrcoreAgentHost.GetHandle();

        var chatClient = await GetChatClient(chatClientConfigName);
        var historyProvider = FabrCoreChatHistoryProvider.Create(fabrcoreAgentHost, threadId, logger);

        // Layer 1 of the ladder. A harness agent runs long tool loops, so bounding every call in the loop
        // matters more here than anywhere else — this is the rung that keeps a 40-iteration run inside the
        // window without a single LLM call or any change to persisted history.
        var contextCompactionProvider = await TryCreateContextCompactionProviderAsync(chatClientConfigName);

        var options = new FabrCoreHarnessOptions
        {
            Name = handle,
            Description = config.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = config.SystemPrompt,
                Tools = tools
            },
            ChatHistoryProvider = historyProvider,
            AIContextProviders = contextCompactionProvider is not null ? [contextCompactionProvider] : null,
            LoopMaxIterations = DefaultHarnessLoopMaxIterations,
            MaximumIterationsPerRequest = DefaultHarnessMaxIterationsPerRequest
        };

        // Layer: agent args override defaults (prefixed with _).
        if (args.TryGetValue(HarnessArgs.Todo, out var todoStr) && bool.TryParse(todoStr, out var todoEnabled))
        {
            options.DisableTodoProvider = !todoEnabled;
        }

        if (args.TryGetValue(HarnessArgs.Mode, out var modeStr) && bool.TryParse(modeStr, out var modeEnabled))
        {
            options.DisableAgentModeProvider = !modeEnabled;
        }

        if (args.TryGetValue(HarnessArgs.DefaultMode, out var defaultModeStr) && !string.IsNullOrWhiteSpace(defaultModeStr))
        {
            options.AgentModeProviderOptions = new AgentModeProviderOptions
            {
                DefaultMode = defaultModeStr.Trim()
            };
        }

        if (args.TryGetValue(HarnessArgs.LoopMaxIterations, out var loopMaxStr)
            && int.TryParse(loopMaxStr, out var loopMax))
        {
            options.LoopMaxIterations = Math.Max(1, loopMax);
        }

        if (args.TryGetValue(HarnessArgs.MaxIterationsPerRequest, out var maxPerRequestStr)
            && int.TryParse(maxPerRequestStr, out var maxPerRequest))
        {
            options.MaximumIterationsPerRequest = Math.Max(1, maxPerRequest);
        }

        if (args.TryGetValue(HarnessArgs.LoopMarker, out var markerStr) && !string.IsNullOrWhiteSpace(markerStr))
        {
            options.LoopCompletionMarker = markerStr.Trim();
        }

        if (args.TryGetValue(HarnessArgs.LoopJudgePrompt, out var judgePrompt) && !string.IsNullOrWhiteSpace(judgePrompt))
        {
            options.LoopJudgeOptions = new AIJudgeLoopEvaluatorOptions { Instructions = judgePrompt };
        }

        // An empty value is meaningful here: it drops the harness preamble. Do not blank-guard it.
        if (args.TryGetValue(HarnessArgs.Instructions, out var harnessInstructions))
        {
            options.HarnessInstructions = harnessInstructions;
        }

        var persistSession = true;
        if (args.TryGetValue(HarnessArgs.SessionPersistence, out var persistStr) && bool.TryParse(persistStr, out var persistVal))
        {
            persistSession = persistVal;
        }

        var delegationTimeout = FabrCoreBackgroundAgent.DefaultTimeout;
        if (args.TryGetValue(HarnessArgs.BackgroundTimeoutSeconds, out var timeoutStr)
            && int.TryParse(timeoutStr, out var timeoutSeconds)
            && timeoutSeconds > 0)
        {
            delegationTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        AgentRoster? roster = null;
        if (args.TryGetValue(HarnessArgs.BackgroundAgents, out var backgroundSpec) && !string.IsNullOrWhiteSpace(backgroundSpec))
        {
            roster = await AgentRosterBuilder.BuildAsync(
                backgroundSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                fabrcoreAgentHost,
                serviceProvider.GetService<IFabrCoreRegistry>(),
                logger);

            if (roster.Unavailable.Count > 0)
            {
                logger.LogWarning(
                    "Harness excluded unavailable background agents - Handle: {Handle}, Excluded: {Excluded}",
                    handle,
                    roster.DescribeUnavailable());
            }

            options.BackgroundAgents = FabrCoreBackgroundAgent.FromRoster(roster, fabrcoreAgentHost, delegationTimeout);
        }

        FabrCoreStoredAgentSkillsSource? storedSkillsSource = null;
        if (args.TryGetValue(HarnessArgs.Skills, out var skillSpec) && !string.IsNullOrWhiteSpace(skillSpec))
        {
            var references = new List<FabrCoreSkillReference>();
            var errors = new List<string>();
            foreach (var token in skillSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (FabrCoreSkillReference.TryParse(token, out var reference, out var reason))
                {
                    references.Add(reference!);
                }
                else
                {
                    errors.Add(reason ?? $"Invalid skill reference '{token}'.");
                }
            }

            if (errors.Count > 0 || references.Count == 0)
            {
                throw new ArgumentException(
                    $"Invalid {HarnessArgs.Skills} configuration: {string.Join("; ", errors)}",
                    nameof(config));
            }

            var storage = serviceProvider.GetService<IPrincipalScopedFabrCoreStorageProvider>()
                ?? throw new InvalidOperationException(
                    $"{HarnessArgs.Skills} requires {nameof(IPrincipalScopedFabrCoreStorageProvider)} to be registered.");

            storedSkillsSource = new FabrCoreStoredAgentSkillsSource(
                storage,
                fabrcoreAgentHost.GetUserHandle(),
                references);
            options.AgentSkillsSource = storedSkillsSource;
        }

        // Null means the loop was never configured, which is different from being switched off explicitly.
        HarnessLoopMode? loopModeFromArgs = args.TryGetValue(HarnessArgs.Loop, out var loopSpec)
            ? ParseHarnessLoopMode(loopSpec, handle, logger)
            : null;

        options.LoopMode = loopModeFromArgs ?? HarnessLoopMode.None;

        configure?.Invoke(options);

        // The args-derived source is immutable and storage-backed. Preflight every manifest after the
        // callback so setting AgentSkillsSource to null or replacing it remains a true last-writer-wins override.
        if (storedSkillsSource is not null
            && ReferenceEquals(options.AgentSkillsSource, storedSkillsSource))
        {
            await storedSkillsSource.InitializeAsync();
        }

        // Materialize once so the count checks below and the provider see the same set.
        var delegates = options.BackgroundAgents?.ToList();
        options.BackgroundAgents = delegates;

        if (loopModeFromArgs is null && options.LoopMode == HarnessLoopMode.None)
        {
            // Unset: loop on todos, and on delegations too when there is anything to delegate to.
            var defaultMode = options.DisableTodoProvider ? HarnessLoopMode.None : HarnessLoopMode.Todo;
            if (delegates is { Count: > 0 })
            {
                defaultMode |= HarnessLoopMode.Background;
            }

            options.LoopMode = defaultMode;
        }

        if (options.LoopMode.HasFlag(HarnessLoopMode.Judge) && options.LoopJudgeChatClient is null)
        {
            var judgeModel = args.TryGetValue(HarnessArgs.LoopJudgeModel, out var judgeModelStr) && !string.IsNullOrWhiteSpace(judgeModelStr)
                ? judgeModelStr.Trim()
                : chatClientConfigName;

            options.LoopJudgeChatClient = await GetChatClient(judgeModel);
        }

        var agent = chatClient.AsFabrCoreHarnessAgent(options, loggerFactory, serviceProvider);

        var (session, restored, delegationsLost) = await RestoreOrCreateHarnessSessionAsync(agent, threadId, persistSession);

        // Register for history compaction and the projection fuse exactly as CreateChatClientAgent does,
        // so harness agents inherit the same storage hygiene as every other agent. Layer 1 was composed
        // above; this registers rungs 3–5.
        _chatHistoryProvider = historyProvider;
        _chatClientConfigName = chatClientConfigName;
        var compactionRegistration = new ChatHistoryCompactionRegistration
        {
            Provider = historyProvider,
            ChatClientConfigName = chatClientConfigName
        };
        _chatHistoryCompactionRegistrations.Add(compactionRegistration);
        await EnsureCompactionInitializedAsync(compactionRegistration);

        logger.LogInformation(
            "Created FabrCore harness agent - Handle: {Handle}, Config: {Config}, ThreadId: {ThreadId}, Todos: {Todos}, Modes: {Modes}, Skills: {Skills}, Loop: {Loop}, MaxIterations: {MaxIterations}, Delegates: {DelegateCount}, SessionRestored: {SessionRestored}, DelegationsLost: {DelegationsLost}",
            handle,
            chatClientConfigName,
            threadId,
            !options.DisableTodoProvider,
            !options.DisableAgentModeProvider,
            options.AgentSkillsSource is not null,
            options.LoopMode,
            options.LoopMaxIterations,
            delegates?.Count ?? 0,
            restored,
            delegationsLost);

        return new FabrCoreHarnessResult(
            agent,
            session,
            threadId,
            handle,
            historyProvider,
            persistSession ? new ProxyHarnessSessionStore(this) : null,
            restored,
            delegationsLost,
            logger);
    }

    private async Task<(AgentSession Session, bool Restored, int DelegationsLost)> RestoreOrCreateHarnessSessionAsync(
        FabrCoreHarnessAgent agent,
        string threadId,
        bool persistSession)
    {
        if (!persistSession)
        {
            return (await agent.CreateSessionAsync(), false, 0);
        }

        var stateKey = HarnessSessionSnapshot.KeyFor(threadId);
        var handle = fabrcoreAgentHost.GetHandle();

        // Read the raw element rather than the typed envelope so an unreadable payload can be archived
        // intact instead of being lost to the deserialization failure that revealed it.
        var read = await TryGetStateAsync<JsonElement>(stateKey);

        if (!read.Succeeded)
        {
            logger.LogError(
                read.Error,
                "Harness session state is unreadable and was discarded - Handle: {Handle}, ThreadId: {ThreadId}",
                handle, threadId);

            RemoveState(stateKey);
            await FlushStateAsync();
            return (await agent.CreateSessionAsync(), false, 0);
        }

        var raw = read.Value;
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return (await agent.CreateSessionAsync(), false, 0);
        }

        try
        {
            var snapshot = raw.Deserialize<HarnessSessionSnapshot>()
                ?? throw new JsonException("Harness session snapshot deserialized to null.");

            if (snapshot.Version != HarnessSessionSnapshot.CurrentVersion)
            {
                logger.LogWarning(
                    "Harness session snapshot version {Found} is not {Expected}; starting fresh - Handle: {Handle}, ThreadId: {ThreadId}",
                    snapshot.Version, HarnessSessionSnapshot.CurrentVersion, handle, threadId);

                RemoveState(stateKey);
                await FlushStateAsync();
                return (await agent.CreateSessionAsync(), false, 0);
            }

            var session = await agent.DeserializeSessionAsync(snapshot.Payload);
            var delegationsLost = HarnessSessionSnapshot.CountRunningDelegations(snapshot.Payload);

            if (delegationsLost > 0)
            {
                logger.LogWarning(
                    "Harness session restored with {Count} delegation(s) that were in flight and cannot be recovered - Handle: {Handle}, ThreadId: {ThreadId}",
                    delegationsLost, handle, threadId);
            }

            logger.LogInformation(
                "Harness session restored - Handle: {Handle}, ThreadId: {ThreadId}, SavedUtc: {SavedUtc}",
                handle, threadId, snapshot.SavedUtc);

            return (session, true, delegationsLost);
        }
        catch (Exception ex)
        {
            // Archive rather than delete: harness state resetting is survivable, but silently destroying the
            // evidence of why makes the next occurrence undiagnosable. Conversation history is unaffected —
            // it lives in MessageThreads, not in this snapshot.
            logger.LogError(
                ex,
                "Harness session snapshot could not be restored; archiving it and starting fresh - Handle: {Handle}, ThreadId: {ThreadId}",
                handle, threadId);

            SetState(HarnessSessionSnapshot.CorruptKeyFor(threadId), raw);
            RemoveState(stateKey);
            await FlushStateAsync();

            return (await agent.CreateSessionAsync(), false, 0);
        }
    }

    private static HarnessLoopMode ParseHarnessLoopMode(string? spec, string handle, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return HarnessLoopMode.None;
        }

        var mode = HarnessLoopMode.None;

        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "todo":
                case "todos":
                    mode |= HarnessLoopMode.Todo;
                    break;
                case "background":
                case "delegation":
                    mode |= HarnessLoopMode.Background;
                    break;
                case "marker":
                case "completion":
                    mode |= HarnessLoopMode.Marker;
                    break;
                case "judge":
                    mode |= HarnessLoopMode.Judge;
                    break;
                case "none":
                case "off":
                    break;
                default:
                    logger.LogWarning(
                        "Ignoring unrecognized {Arg} value '{Token}' - Handle: {Handle}. Valid values: todo, background, marker, judge, none.",
                        HarnessArgs.Loop, token, handle);
                    break;
            }
        }

        return mode;
    }

    /// <summary>
    /// Bridges <see cref="FabrCoreHarnessResult"/> to the proxy's protected custom-state API.
    /// </summary>
    /// <remarks>
    /// Each write flushes immediately. That costs a whole-blob grain write per turn, which is the price of
    /// harness state surviving a silo kill rather than only a graceful deactivation.
    /// </remarks>
    private sealed class ProxyHarnessSessionStore(FabrCoreAgentProxy proxy) : IHarnessSessionStore
    {
        public async Task WriteAsync(string key, HarnessSessionSnapshot snapshot)
        {
            proxy.SetState(key, snapshot);
            await proxy.FlushStateAsync();
        }

        public async Task DeleteAsync(string key)
        {
            proxy.RemoveState(key);
            await proxy.FlushStateAsync();
        }
    }
}

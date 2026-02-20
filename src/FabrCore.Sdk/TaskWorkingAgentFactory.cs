using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// Factory for creating TaskWorkingAgent instances.
/// Register as singleton in DI: services.AddSingleton&lt;ITaskWorkingAgentFactory, TaskWorkingAgentFactory&gt;();
/// </summary>
public class TaskWorkingAgentFactory : ITaskWorkingAgentFactory
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<TaskWorkingAgent>? _logger;
    private readonly Func<string, string, Task>? _onProgress;
    private readonly ExecutionOptions? _executionOptions;

    /// <summary>
    /// Creates a new TaskWorkingAgentFactory.
    /// </summary>
    /// <param name="chatClient">The chat client to use for task tracking extraction.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="onProgress">Optional async callback for progress reporting: (phase, message) => Task.</param>
    /// <param name="executionOptions">Optional execution options for running the execution loop.</param>
    public TaskWorkingAgentFactory(
        IChatClient chatClient,
        ILogger<TaskWorkingAgent>? logger = null,
        Func<string, string, Task>? onProgress = null,
        ExecutionOptions? executionOptions = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger;
        _onProgress = onProgress;
        _executionOptions = executionOptions;
    }

    /// <inheritdoc />
    public ITaskWorkingAgent Create(AgentSession session)
    {
        return new TaskWorkingAgent(_chatClient, session, _logger, _onProgress, _executionOptions);
    }
}

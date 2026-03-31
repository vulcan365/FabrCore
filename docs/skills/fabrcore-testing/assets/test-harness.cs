using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FabrCore.Tests.Infrastructure;

/// <summary>
/// Orchestrates FabrCoreAgentProxy creation for testing.
/// Builds the IServiceProvider with all required dependencies and provides
/// factory methods for both mock and live agent creation.
/// </summary>
public class FabrCoreTestHarness : IDisposable
{
    private ServiceProvider? _serviceProvider;

    public TestFabrCoreAgentHost AgentHost { get; private set; } = default!;

    /// <summary>
    /// Creates an agent in mock mode with a FakeChatClient for deterministic testing.
    /// </summary>
    public TAgent CreateMockAgent<TAgent>(
        IChatClient chatClient,
        AgentConfiguration? config = null) where TAgent : FabrCoreAgentProxy
    {
        config ??= CreateDefaultConfig<TAgent>();
        AgentHost = new TestFabrCoreAgentHost(config.Handle ?? "test-agent");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(chatClient));
        services.AddSingleton(new FabrCoreToolRegistry(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<FabrCoreToolRegistry>()));

        _serviceProvider = services.BuildServiceProvider();
        return CreateAgentInstance<TAgent>(config);
    }

    /// <summary>
    /// Creates an agent in live mode reading model configs and API keys directly from fabrcore.json.
    /// No FabrCore Host API required — chat clients are created locally.
    /// Returns null if fabrcore.json is not found or has placeholder API keys.
    /// </summary>
    public TAgent? CreateLiveAgent<TAgent>(
        AgentConfiguration? config = null,
        string? fabrcoreJsonPath = null) where TAgent : FabrCoreAgentProxy
    {
        fabrcoreJsonPath ??= FindFabrcoreJson();
        if (fabrcoreJsonPath is null)
            return null;

        // Check if API key is a placeholder
        var jsonContent = File.ReadAllText(fabrcoreJsonPath);
        if (jsonContent.Contains("REPLACE_WITH") || jsonContent.Contains("YOUR_API_KEY"))
            return null;

        // Parse config directly — no Host API dependency
        var fabrcoreConfig = JsonSerializer.Deserialize<FabrCoreConfiguration>(jsonContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (fabrcoreConfig is null || fabrcoreConfig.ModelConfigurations.Count == 0)
            return null;

        config ??= CreateDefaultConfig<TAgent>();
        AgentHost = new TestFabrCoreAgentHost(config.Handle ?? "test-agent");

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(fabrcoreConfig, loggerFactory));
        services.AddSingleton(new FabrCoreToolRegistry(loggerFactory.CreateLogger<FabrCoreToolRegistry>()));

        _serviceProvider = services.BuildServiceProvider();
        return CreateAgentInstance<TAgent>(config);
    }

    /// <summary>
    /// Initializes the agent by calling OnInitialize directly.
    /// </summary>
    public async Task InitializeAgent(FabrCoreAgentProxy agent)
    {
        await agent.OnInitialize();
    }

    /// <summary>
    /// Sends a message through the agent's OnMessage method.
    /// </summary>
    public async Task<AgentMessage> SendMessage(FabrCoreAgentProxy agent, string messageText, string? fromHandle = null)
    {
        var message = new AgentMessage
        {
            FromHandle = fromHandle ?? "test-user",
            ToHandle = AgentHost.GetHandle(),
            Message = messageText,
            Kind = MessageKind.Request
        };
        return await agent.OnMessage(message);
    }

    /// <summary>
    /// Convenience method: initializes then sends a message.
    /// </summary>
    public async Task<AgentMessage> InitializeAndMessage(FabrCoreAgentProxy agent, string messageText)
    {
        await InitializeAgent(agent);
        return await SendMessage(agent, messageText);
    }

    private TAgent CreateAgentInstance<TAgent>(AgentConfiguration config) where TAgent : FabrCoreAgentProxy
    {
        return (TAgent)Activator.CreateInstance(
            typeof(TAgent),
            config,
            _serviceProvider!,
            AgentHost)!;
    }

    private static AgentConfiguration CreateDefaultConfig<TAgent>()
    {
        var aliasAttr = typeof(TAgent).GetCustomAttributes(typeof(AgentAliasAttribute), false)
            .FirstOrDefault() as AgentAliasAttribute;
        var alias = aliasAttr?.Alias ?? typeof(TAgent).Name.ToLowerInvariant();

        return new AgentConfiguration
        {
            Handle = $"test:{alias}",
            AgentType = alias,
            Models = "default",
            SystemPrompt = "You are a helpful test agent."
        };
    }

    private static string? FindFabrcoreJson()
    {
        // Search from multiple starting points to handle different test runners
        // (CLI vs Visual Studio Test Explorer vs ReSharper, etc.)
        var searchRoots = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Environment.CurrentDirectory
        };

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = root;
            for (var i = 0; i < 8; i++)
            {
                var path = Path.Combine(dir, "fabrcore.json");
                if (File.Exists(path))
                    return path;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}

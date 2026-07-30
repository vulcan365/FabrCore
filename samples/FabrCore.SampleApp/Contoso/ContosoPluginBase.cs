using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk;
using FabrCore.Sdk.VerifiableExecution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.SampleApp.Contoso;

/// <summary>
/// Shared wiring for the Contoso Bike Shop demo plugins: store access plus
/// verifiable-execution recording so every fake in-memory read and write shows
/// up as an attested external effect when signing is enabled.
/// </summary>
public abstract class ContosoPluginBase : IFabrCorePlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private IVerifiableExecutionContext? evidence;
    private ILogger logger = default!;

    protected ContosoBikeShopStore Store { get; private set; } = default!;

    protected string AgentHandle { get; private set; } = string.Empty;

    protected abstract string TableName { get; }

    public virtual Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        Store = serviceProvider.GetRequiredService<ContosoBikeShopStore>();
        evidence = serviceProvider.GetService<IVerifiableExecutionContext>();
        logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());

        var agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        AgentHandle = agentHost.GetHandle();
        if (string.IsNullOrWhiteSpace(AgentHandle))
        {
            AgentHandle = config.Handle ?? "contoso-worker";
        }

        return Task.CompletedTask;
    }

    protected async Task<T> RecordDbEffect<T>(
        string operation,
        string subject,
        Func<T> effect,
        params string?[] parameterValues)
    {
        if (evidence is null)
        {
            return effect();
        }

        var parameterText = string.Join("|", parameterValues.Where(value => !string.IsNullOrWhiteSpace(value)));
        var result = await evidence.RecordDbEffectAsync(
            operation: operation,
            target: "ContosoBikeShopStore",
            subject: subject,
            effect: () => Task.FromResult(effect()),
            metadata: new Dictionary<string, string?>
            {
                ["db.system"] = "in-memory-demo",
                ["db.name"] = "ContosoBikeShop",
                ["db.table"] = TableName,
                ["agent_handle_hash"] = VerifiableExecutionHash.HashText(AgentHandle),
                ["operation"] = operation,
                ["parameter_hash"] = VerifiableExecutionHash.HashText(parameterText)
            },
            logger: logger,
            cancellationToken: CancellationToken.None);

        return result.Value!;
    }

    protected static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    protected static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

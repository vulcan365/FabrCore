using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk;
using FabrCore.Sdk.VerifiableExecution;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Text.Json;

[PluginAlias("orders")]
public sealed class OrdersPlugin : IFabrCorePlugin
{
    private IVerifiableExecutionContext? _evidence;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _evidence = serviceProvider.GetService<IVerifiableExecutionContext>();
        return Task.CompletedTask;
    }

    [Description("Updates an order status and records a verifiable external DB effect.")]
    public async Task<string> UpdateOrderStatus(
        [Description("Order id")] string orderId,
        [Description("New status")] string status)
    {
        var commandText = "UPDATE Orders SET Status = @status WHERE OrderId = @orderId";
        var parameters = JsonSerializer.Serialize(new { orderId = Hash(orderId), status });

        var result = await _evidence.RecordDbEffectAsync(
            operation: "UpdateOrderStatus",
            target: "Orders",
            subject: orderId,
            effect: () => ExecuteUpdateAsync(orderId, status),
            metadata: new Dictionary<string, string?>
            {
                ["db.system"] = "sqlserver",
                ["row_key_hash"] = VerifiableExecutionHash.HashText(orderId),
                ["command_hash"] = VerifiableExecutionHash.HashText(commandText),
                ["parameter_hash"] = VerifiableExecutionHash.HashText(parameters)
            });

        return $"Updated {result.Value} row(s).";
    }

    private static Task<int> ExecuteUpdateAsync(string orderId, string status)
        => throw new NotImplementedException();
}

using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
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

        var affectedRows = await ExecuteUpdateAsync(orderId, status);

        if (_evidence is not null)
        {
            await _evidence.RecordExternalEffectAsync(
                ExecutionRecordKind.ExternalDbEffect,
                "orders:update-status",
                new Dictionary<string, string?>
                {
                    ["db.system"] = "sqlserver",
                    ["db.table"] = "Orders",
                    ["operation"] = "UPDATE",
                    ["row_key_hash"] = Hash(orderId),
                    ["command_hash"] = Hash(commandText),
                    ["parameter_hash"] = Hash(parameters),
                    ["affected_rows"] = affectedRows.ToString()
                });
        }

        return $"Updated {affectedRows} row(s).";
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Task<int> ExecuteUpdateAsync(string orderId, string status)
        => throw new NotImplementedException();
}

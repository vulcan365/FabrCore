using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;
using Orleans;
namespace FabrCore.Host.Grains;
internal sealed class AdministrationOperationGrain(IUserScopedFabrCoreStorageProvider storage, IFabrCoreAgentService agents,
    IClusterClient cluster, ILogger<AdministrationOperationGrain> logger) : Grain, IAdministrationOperationGrain
{
    private bool active;
    private string[] Identity => JsonSerializer.Deserialize<string[]>(this.GetPrimaryKeyString())!;
    private string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.GetPrimaryKeyString())));
    public async Task<string> StartAsync(string request)
    {
        var identity = Identity;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
        var old = await storage.GetAsync<AdministrationOperation>(identity[0], "fabrcore.operations", Key);
        if (old is not null)
        {
            if (old.RequestDigest != digest) return Result(409, new { error = "Operation ID is already bound to another request." });
            return await ReadAsync();
        }
        var input = JsonSerializer.Deserialize<AdministrationOperationRequest>(request, JsonSerializerOptions.Web)!;
        if (input.Kind is not ("create" or "ensure" or "reset" or "restart" or "configure" or "test-message" or "test-event" or "deploy")) return Result(400, new { error = "Unsupported operation kind." });
        if (input.AgentHandle?.Contains(':') == true) return Result(400, new { error = "Use a local agent handle." });
        if (input.Configurations.Any(c => string.IsNullOrWhiteSpace(c.Handle) || c.Handle.Contains(':')))
            return Result(400, new { error = "Configurations must use local agent handles." });
        if (FabrCoreAdminChannels.IsReserved(input.Message?.Channel) || FabrCoreAdminChannels.IsReserved(input.Event?.Channel))
            return Result(400, new { error = "Reserved diagnostic channels cannot be used for test messages or events." });
        var receipt = new AdministrationOperation { Id = identity[2], RequestDigest = digest, StartedAt = DateTimeOffset.UtcNow };
        active = true;
        try { await storage.UpsertAsync(identity[0], "fabrcore.operations", Key, receipt); }
        catch { active = false; throw; }
        DelayDeactivation(TimeSpan.FromMinutes(15));
        _ = CompleteAsync(input, receipt);
        return Result(202, receipt);
    }
    public async Task<string> ReadAsync()
    {
        var receipt = await storage.GetAsync<AdministrationOperation>(Identity[0], "fabrcore.operations", Key);
        if (receipt is null) return Result(404, new { error = "Operation not found." });
        if (!active && receipt.Status == "running") { receipt.Status = "incomplete"; receipt.Error = "Execution was interrupted. Inspect current state before submitting a new operation."; }
        return Result(receipt.Status == "running" ? 202 : 200, receipt);
    }
    private async Task CompleteAsync(AdministrationOperationRequest input, AdministrationOperation receipt)
    {
        try
        {
            var principal = Identity[0];
            object? result;
            switch (input.Kind)
            {
                case "create":
                    foreach (var configuration in input.Configurations) configuration.ForceReconfigure = false;
                    result = await agents.ConfigureAgentsAsync(principal, input.Configurations); break;
                case "ensure": result = await agents.EnsureAgentsAsync(principal, input.Configurations); break;
                case "test-message":
                    ArgumentNullException.ThrowIfNull(input.Message);
                    input.Message.TraceId ??= System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
                    result = await agents.SendAndReceiveMessageAsync(principal, input.AgentHandle!, input.Message); break;
                case "test-event":
                    ArgumentNullException.ThrowIfNull(input.Event);
                    await agents.SendEventAsync(principal, input.AgentHandle!, input.Event); result = new { dispatched = true, input.Event.Id }; break;
                case "deploy":
                    result = JsonSerializer.Deserialize<JsonElement>(await cluster.GetGrain<IBlueprintAdministrationGrain>(principal)
                        .ExecuteAsync("deploy", input.BlueprintName!, JsonSerializer.Serialize(input.Deployment, JsonSerializerOptions.Web), null)); break;
                default:
                    result = JsonSerializer.Deserialize<JsonElement>(await cluster.GetGrain<IAgentGrain>($"{principal}:{input.AgentHandle}")
                        .ManageAsync(input.Kind, JsonSerializer.Serialize(input.Management ?? new(), JsonSerializerOptions.Web))); break;
            }
            receipt.ResultJson = JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
            receipt.Status = result is JsonElement json && json.TryGetProperty("statusCode", out var status) && status.GetInt32() >= 400 ? "failed" : "completed";
        }
        catch (Exception ex) { receipt.Status = "failed"; receipt.Error = ex.Message; }
        finally
        {
            receipt.CompletedAt = DateTimeOffset.UtcNow;
            try { await storage.UpsertAsync(Identity[0], "fabrcore.operations", Key, receipt); }
            catch (Exception ex) { logger.LogError(ex, "Operation result persistence failed for {Operation}", receipt.Id); }
            active = false;
        }
    }
    private static string Result(int status, object value) => JsonSerializer.Serialize(new AdminApiResult { StatusCode = status,
        Body = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web) }, JsonSerializerOptions.Web);
}

using System.Security.Cryptography;
using System.Text;
using FabrCore.Core.Interfaces;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Options;
using Orleans;

namespace FabrCore.Host.WebSocket;

public interface IWebSocketTicketService
{
    Task<FabrCoreWebSocketTicketResponse> IssueAsync(string principalHandle);
    Task<string?> RedeemAsync(string token);
}

public sealed class WebSocketTicketService(
    IClusterClient clusterClient,
    IOptions<FabrCoreWebSocketOptions> options,
    TimeProvider timeProvider) : IWebSocketTicketService
{
    public async Task<FabrCoreWebSocketTicketResponse> IssueAsync(string principalHandle)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Hash(token);
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + options.Value.TicketLifetime;
        await Registry(hash).Store(hash, new FabrCoreWebSocketTicketEntry
        {
            PrincipalHandle = principalHandle,
            IssuedAt = now,
            ExpiresAt = expiresAt,
        }, options.Value.MaxTicketsPerShard);
        return new FabrCoreWebSocketTicketResponse(token, expiresAt);
    }

    public Task<string?> RedeemAsync(string token)
    {
        var hash = Hash(token);
        return Registry(hash).Redeem(hash, timeProvider.GetUtcNow());
    }

    private IWebSocketTicketRegistryGrain Registry(string hash)
    {
        var shardCount = Math.Max(1, options.Value.TicketRegistryShards);
        var shard = Convert.ToInt32(hash[..2], 16) % shardCount;
        return clusterClient.GetGrain<IWebSocketTicketRegistryGrain>(shard);
    }

    private static string Hash(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

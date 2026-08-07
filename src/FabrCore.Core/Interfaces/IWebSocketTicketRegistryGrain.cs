using FabrCore.Core.WebSockets;
using Orleans;

namespace FabrCore.Core.Interfaces;

internal interface IWebSocketTicketRegistryGrain : IGrainWithIntegerKey
{
    Task Store(string hash, FabrCoreWebSocketTicketEntry ticket, int capacity);
    Task<string?> Redeem(string hash, DateTimeOffset now);
}

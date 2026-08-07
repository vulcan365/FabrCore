using FabrCore.Core.WebSockets;
using Orleans;
using Orleans.Concurrency;

namespace FabrCore.Core.Interfaces;

internal interface IWebSocketDeliveryGrain : IGrainWithStringKey
{
    Task<FabrCoreWebSocketRegistration> RegisterClient(string clientId, long? checkpoint);
    Task<FabrCoreWebSocketDeliveryRecord?> Append(AgentMessage message);
    Task Acknowledge(string clientId, long sequence);
    Task MarkDelivered(string clientId, long sequence);
}

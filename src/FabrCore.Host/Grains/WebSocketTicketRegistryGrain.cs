using FabrCore.Core.Interfaces;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using Orleans.Runtime;

namespace FabrCore.Host.Grains;

internal sealed class WebSocketTicketRegistryGrain : Grain, IWebSocketTicketRegistryGrain
{
    private readonly IPersistentState<FabrCoreWebSocketTicketState> state;

    public WebSocketTicketRegistryGrain(
        [PersistentState("webSocketTickets", FabrCoreOrleansConstants.StorageProviderName)]
        IPersistentState<FabrCoreWebSocketTicketState> state) => this.state = state;

    public async Task Store(string hash, FabrCoreWebSocketTicketEntry ticket, int capacity)
    {
        Prune(ticket.IssuedAt);
        if (state.State.Tickets.Count >= capacity)
        {
            var oldest = state.State.Tickets.OrderBy(x => x.Value.IssuedAt).First().Key;
            state.State.Tickets.Remove(oldest);
        }
        state.State.Tickets[hash] = ticket;
        await state.WriteStateAsync();
    }

    public async Task<string?> Redeem(string hash, DateTimeOffset now)
    {
        Prune(now);
        if (!state.State.Tickets.Remove(hash, out var ticket))
        {
            await state.WriteStateAsync();
            return null;
        }

        await state.WriteStateAsync();
        return ticket.ExpiresAt > now ? ticket.PrincipalHandle : null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in state.State.Tickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            state.State.Tickets.Remove(key);
    }
}

using System.Collections.Concurrent;
using FabrCore.Core;
using FabrCore.Core.Interfaces;
using Orleans;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Turn-scoped observer on a principal grain. FabrCore agents deliver surface messages
/// (<c>ui.render</c> and friends) to their principal rather than returning them from
/// <c>OnMessage</c>; in Copilot there is no surface client subscribed, so those messages would
/// queue on the grain forever. Subscribing this capture for the duration of a Copilot turn
/// receives both the live messages and any backlog the grain flushes on subscribe.
/// </summary>
internal sealed class PrincipalMessageCapture : IPrincipalGrainObserver, IAsyncDisposable
{
    private readonly ConcurrentQueue<AgentMessage> _messages = new();
    private readonly IGrainFactory _grainFactory;
    private readonly IPrincipalGrain _principal;
    private IPrincipalGrainObserver? _reference;

    private PrincipalMessageCapture(IGrainFactory grainFactory, IPrincipalGrain principal)
    {
        _grainFactory = grainFactory;
        _principal = principal;
    }

    public static async Task<PrincipalMessageCapture> SubscribeAsync(IGrainFactory grainFactory, string principalHandle)
    {
        var capture = new PrincipalMessageCapture(
            grainFactory, grainFactory.GetGrain<IPrincipalGrain>(principalHandle));
        capture._reference = grainFactory.CreateObjectReference<IPrincipalGrainObserver>(capture);
        await capture._principal.Subscribe(capture._reference);
        return capture;
    }

    public void OnMessageReceived(AgentMessage message) => _messages.Enqueue(message);

    /// <summary>
    /// Removes and returns the adaptive-card surface renders captured so far. Other captured
    /// message types (thinking/status updates and the like) are discarded — they have no
    /// rendering in the Copilot channel.
    /// </summary>
    public IReadOnlyList<AgentMessage> DrainAdaptiveCardRenders()
    {
        var renders = new List<AgentMessage>();
        while (_messages.TryDequeue(out var message))
        {
            if (CopilotActivityMapper.IsAdaptiveCardRender(message))
            {
                renders.Add(message);
            }
        }

        return renders;
    }

    public async ValueTask DisposeAsync()
    {
        if (_reference is null)
        {
            return;
        }

        try
        {
            await _principal.Unsubscribe(_reference);
        }
        catch
        {
            // The grain may have deactivated; the observer reference is deleted regardless.
        }
        finally
        {
            _grainFactory.DeleteObjectReference<IPrincipalGrainObserver>(_reference);
            _reference = null;
        }
    }
}

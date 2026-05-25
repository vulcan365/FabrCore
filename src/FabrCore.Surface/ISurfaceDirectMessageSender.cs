using FabrCore.Core;

namespace FabrCore.Surface;

public interface ISurfaceDirectMessageSender
{
    Task SendMessageAsync(AgentMessage message);

    Task SendEventAsync(EventMessage message, string? streamName = null);
}

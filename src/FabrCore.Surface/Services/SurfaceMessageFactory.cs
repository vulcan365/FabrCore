using System.Text.Json;
using FabrCore.Core;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Services;

public static class SurfaceMessageFactory
{
    public static AgentMessage CreateRenderMessage(
        AdaptiveCardSurfaceEnvelope envelope,
        AgentMessage sourceMessage,
        string? targetHandle = null)
    {
        var message = sourceMessage.Response();
        message.Kind = MessageKind.OneWay;
        message.ToHandle = string.IsNullOrWhiteSpace(targetHandle) ? message.ToHandle : targetHandle;
        message.MessageType = SurfaceMessageTypes.UiRender;
        message.DataType = SurfaceMessageTypes.DataType;
        message.Data = JsonSerializer.SerializeToUtf8Bytes(envelope, SurfaceJson.Options);
        message.Message = null;
        return message;
    }
}

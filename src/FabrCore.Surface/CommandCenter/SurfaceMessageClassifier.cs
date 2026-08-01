using System.Text.Json;
using FabrCore.Core;
using FabrCore.Surface.Ai.Swarm;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.CommandCenter;

public static class SurfaceMessageClassifier
{
    public static SurfaceTimelineItem Classify(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (IsAdaptiveCardRender(message, out var envelope))
        {
            return new SurfaceTimelineItem
            {
                AgentHandle = ResolveAdaptiveCardAgentHandle(message),
                Kind = SurfaceTimelineItemKind.AdaptiveCard,
                Author = message.FromHandle,
                SourceMessage = message,
                Envelope = envelope,
                MessageType = message.MessageType,
                Text = message.Message,
                DisplayInChat = !ShouldHideFromChat(message)
            };
        }

        if (string.Equals(message.MessageType, SystemMessageTypes.Error, StringComparison.OrdinalIgnoreCase))
        {
            return BuildSystem(message, SurfaceTimelineItemKind.Error, displayInChat: true);
        }

        if (string.Equals(message.MessageType, SystemMessageTypes.Status, StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.MessageType, SystemMessageTypes.Thinking, StringComparison.OrdinalIgnoreCase))
        {
            return BuildSystem(message, SurfaceTimelineItemKind.Status, displayInChat: false);
        }

        if (SystemMessageTypes.IsSystemMessage(message.MessageType))
        {
            return BuildSystem(message, SurfaceTimelineItemKind.Status, displayInChat: false);
        }

        return Build(message, SurfaceTimelineItemKind.Agent);
    }

    public static bool ShouldHideFromChat(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return IsHiddenMessageType(message.MessageType)
               || IsUnderscorePrefixed(message.Message);
    }

    private static string? ResolveAdaptiveCardAgentHandle(AgentMessage message)
    {
        // Cards relayed out of a squad belong to the squad channel timeline, the
        // same way ResolveTimelineAgentHandle buckets squad text traffic.
        if (GetMessageMetadata(message, SurfaceSquadArgs.SquadHandle) is { Length: > 0 } squadHandle
            && squadHandle.Contains(':', StringComparison.Ordinal))
        {
            return squadHandle;
        }

        if (message.Args is not null
            && message.Args.TryGetValue(SurfaceMessageArgs.SurfaceSourceHandle, out var sourceHandle)
            && !string.IsNullOrWhiteSpace(sourceHandle)
            && sourceHandle.Contains(':', StringComparison.Ordinal)
            && !string.Equals(sourceHandle, message.ToHandle, StringComparison.OrdinalIgnoreCase))
        {
            return sourceHandle;
        }

        return message.FromHandle;
    }

    public static bool IsAdaptiveCardRender(AgentMessage message, out AdaptiveCardSurfaceEnvelope? envelope)
    {
        envelope = null;
        if (!string.Equals(message.MessageType, SurfaceMessageTypes.UiRender, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(message.DataType, SurfaceMessageTypes.DataType, StringComparison.OrdinalIgnoreCase)
            || message.Data is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<AdaptiveCardSurfaceEnvelope>(message.Data, SurfaceJson.Options);
            return envelope is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SurfaceTimelineItem Build(AgentMessage message, SurfaceTimelineItemKind kind)
        => new()
        {
            AgentHandle = ResolveTimelineAgentHandle(message),
            Kind = kind,
            Author = message.FromHandle,
            Text = message.Message,
            MessageType = message.MessageType,
            SourceMessage = message,
            DisplayInChat = !ShouldHideFromChat(message)
        };

    private static SurfaceTimelineItem BuildSystem(
        AgentMessage message,
        SurfaceTimelineItemKind kind,
        bool displayInChat)
        => new()
        {
            AgentHandle = ResolveTimelineAgentHandle(message),
            Kind = kind,
            IsSystemMessage = true,
            DisplayInChat = displayInChat && !ShouldHideFromChat(message),
            Author = message.FromHandle,
            Text = string.IsNullOrWhiteSpace(message.Message)
                ? DefaultSystemMessageText(message.MessageType)
                : message.Message,
            MessageType = message.MessageType,
            SourceMessage = message
        };

    private static string? ResolveTimelineAgentHandle(AgentMessage message)
    {
        if (IsHandleShapedChannel(message.Channel))
        {
            return message.Channel;
        }

        if (GetMessageMetadata(message, SurfaceSquadArgs.SquadHandle) is { Length: > 0 } channelHandle)
        {
            return channelHandle;
        }

        return message.FromHandle;
    }

    private static bool IsHandleShapedChannel(string? channel)
        => !string.IsNullOrWhiteSpace(channel)
           && channel.Contains(':', StringComparison.Ordinal);

    private static string? GetMessageMetadata(AgentMessage message, string key)
    {
        if (message.Args is not null
            && message.Args.TryGetValue(key, out var argValue)
            && !string.IsNullOrWhiteSpace(argValue))
        {
            return argValue;
        }

        if (message.State is not null
            && message.State.TryGetValue(key, out var stateValue)
            && !string.IsNullOrWhiteSpace(stateValue))
        {
            return stateValue;
        }

        return null;
    }

    private static bool IsUnderscorePrefixed(string? value)
        => value?.TrimStart().StartsWith("_", StringComparison.Ordinal) == true;

    private static bool IsHiddenMessageType(string? messageType)
        => IsUnderscorePrefixed(messageType)
           && !string.Equals(messageType, SystemMessageTypes.Error, StringComparison.OrdinalIgnoreCase);

    private static string DefaultSystemMessageText(string? messageType)
        => messageType switch
        {
            SystemMessageTypes.Thinking => "Thinking",
            SystemMessageTypes.Status => "Working",
            SystemMessageTypes.Error => "Agent reported an error.",
            _ => "System update"
        };
}

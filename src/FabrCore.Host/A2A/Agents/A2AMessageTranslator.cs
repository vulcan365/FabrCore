using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Host.A2A.Protocol;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Converts between A2A messages and FabrCore <see cref="AgentMessage"/>s.
/// </summary>
/// <remarks>
/// A2A messages are multi-part; FabrCore agents take a single text body plus args. Text parts are
/// concatenated into the body, and everything else is preserved as args so an agent that cares
/// can read it without every agent having to.
/// </remarks>
internal static class A2AMessageTranslator
{
    /// <summary>Builds the FabrCore message for an inbound A2A turn.</summary>
    public static AgentMessage ToAgentMessage(
        A2AMessage message,
        A2AExposedAgent agent,
        string taskId,
        string contextId,
        string? caller,
        A2AInteropOptions interop)
    {
        var agentMessage = new AgentMessage
        {
            Message = ExtractText(message),
            Kind = MessageKind.Request,
            Channel = A2ADefaults.ChannelName,
            Args = new Dictionary<string, string>
            {
                [A2ADefaults.ArgContextId] = contextId,
                [A2ADefaults.ArgTaskId] = taskId,
                [A2ADefaults.ArgMessageId] = message.MessageId,
                [A2ADefaults.ArgAgentName] = agent.Name,
            },
        };

        if (!string.IsNullOrWhiteSpace(caller))
        {
            agentMessage.Args![A2ADefaults.ArgCaller] = caller!;
        }

        if (interop.PassMessageMetadataToAgent && message.Metadata is { Count: > 0 })
        {
            agentMessage.Args![A2ADefaults.ArgMetadata] = JsonSerializer.Serialize(message.Metadata, A2AJson.Options);
        }

        // Data and file parts are carried through verbatim rather than flattened into the prompt.
        // File parts stay as references: this server never fetches a caller-supplied URL, which
        // would turn every A2A client into a way to make this host issue outbound requests.
        var nonText = message.Parts
            .Where(p => !string.Equals(p.Kind, A2APartKinds.Text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonText.Count > 0)
        {
            agentMessage.Args![A2ADefaults.ArgNonTextParts] = JsonSerializer.Serialize(nonText, A2AJson.Options);
        }

        return agentMessage;
    }

    /// <summary>Concatenates every text part of a message, one per line.</summary>
    public static string ExtractText(A2AMessage message)
    {
        var builder = new StringBuilder();
        foreach (var part in message.Parts)
        {
            if (!string.Equals(part.Kind, A2APartKinds.Text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(part.Text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(part.Text);
        }

        return builder.ToString();
    }

    /// <summary>Builds the artifact carrying an agent's reply.</summary>
    public static A2AArtifact ToArtifact(AgentMessage? reply, A2AExposedAgent agent)
    {
        var artifact = new A2AArtifact
        {
            Name = agent.Name + "-response",
            Description = $"Response from {agent.DisplayName}.",
            Parts = new List<A2APart>(),
        };

        if (!string.IsNullOrEmpty(reply?.Message))
        {
            artifact.Parts.Add(A2APart.FromText(reply!.Message!));
        }

        // A reply may also carry a structured payload. Surface it as a data part when it is JSON;
        // anything else stays out of the artifact rather than being emitted as unreadable text.
        if (reply?.Data is { Length: > 0 } && LooksLikeJson(reply.DataType))
        {
            try
            {
                using var document = JsonDocument.Parse(reply.Data);
                artifact.Parts.Add(A2APart.FromData(document.RootElement.Clone()));
            }
            catch (JsonException)
            {
                // Declared as JSON but is not — leave it out; the text part still carries the answer.
            }
        }

        if (artifact.Parts.Count == 0)
        {
            artifact.Parts.Add(A2APart.FromText(string.Empty));
        }

        return artifact;
    }

    /// <summary>Builds the agent-role message recorded in task history and status.</summary>
    public static A2AMessage ToA2AMessage(string text, string taskId, string contextId)
        => new()
        {
            Role = A2ARoles.Agent,
            MessageId = Guid.NewGuid().ToString(),
            TaskId = taskId,
            ContextId = contextId,
            Parts = new List<A2APart> { A2APart.FromText(text) },
        };

    private static bool LooksLikeJson(string? dataType)
        => dataType is not null
           && (dataType.Contains("json", StringComparison.OrdinalIgnoreCase));
}

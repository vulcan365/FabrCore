using Microsoft.Extensions.AI;

namespace FabrCore.Sdk
{
    /// <summary>
    /// A delegating chat client that sanitizes messages for non-OpenAI providers.
    ///
    /// Some OpenAI-compatible providers (Grok, Gemini) reject the "name" field on
    /// non-user messages. The OpenAI SDK serializes <see cref="ChatMessage.AuthorName"/>
    /// as "name" on all roles, which causes 400 errors on these providers.
    ///
    /// This handler strips AuthorName from assistant, tool, and system messages
    /// before forwarding to the inner client.
    /// </summary>
    public class ProviderSanitizingChatClient : DelegatingChatClient
    {
        private readonly bool _stripNonUserAuthorName;

        /// <summary>
        /// Creates a new ProviderSanitizingChatClient.
        /// </summary>
        /// <param name="innerClient">The inner chat client to delegate to.</param>
        /// <param name="stripNonUserAuthorName">
        /// When true, removes AuthorName from all messages where Role != User.
        /// Enable this for providers that reject the "name" field on non-user messages (e.g., Grok, Gemini).
        /// </param>
        public ProviderSanitizingChatClient(IChatClient innerClient, bool stripNonUserAuthorName = true)
            : base(innerClient)
        {
            _stripNonUserAuthorName = stripNonUserAuthorName;
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sanitized = SanitizeMessages(messages);
            return base.GetResponseAsync(sanitized, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sanitized = SanitizeMessages(messages);
            return base.GetStreamingResponseAsync(sanitized, options, cancellationToken);
        }

        private IEnumerable<ChatMessage> SanitizeMessages(IEnumerable<ChatMessage> messages)
        {
            if (!_stripNonUserAuthorName)
                return messages;

            // Materialize to list so we can mutate AuthorName without affecting the caller's
            // original objects. We only clone messages that need modification.
            var result = new List<ChatMessage>();
            foreach (var msg in messages)
            {
                if (msg.AuthorName != null && msg.Role != ChatRole.User)
                {
                    // Clone the message with AuthorName stripped
                    var sanitized = new ChatMessage(msg.Role, msg.Contents)
                    {
                        AuthorName = null,
                        RawRepresentation = msg.RawRepresentation,
                        AdditionalProperties = msg.AdditionalProperties
                    };
                    result.Add(sanitized);
                }
                else
                {
                    result.Add(msg);
                }
            }

            return result;
        }
    }
}

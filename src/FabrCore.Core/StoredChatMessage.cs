using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// Represents a chat message stored in Orleans grain state.
    /// Stores full Contents as JSON to preserve FunctionCallContent, FunctionResultContent, etc.
    /// </summary>
    public class StoredChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Role { get; set; } = string.Empty;  // "user", "assistant", "system", "tool"

        public string? AuthorName { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// JSON-serialized Contents list from ChatMessage.
        /// Contains the full AIContent items including FunctionCallContent and FunctionResultContent.
        /// </summary>
        public string ContentsJson { get; set; } = "[]";
    }

    [GenerateSerializer]
    internal struct StoredChatMessageSurrogate
    {
        public StoredChatMessageSurrogate()
        {
        }

        [Id(0)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Id(1)]
        public string Role { get; set; } = string.Empty;

        [Id(2)]
        public string? AuthorName { get; set; }

        [Id(3)]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [Id(4)]
        public string ContentsJson { get; set; } = "[]";
    }

    [RegisterConverter]
    internal sealed class StoredChatMessageSurrogateConverter : IConverter<StoredChatMessage, StoredChatMessageSurrogate>
    {
        public StoredChatMessageSurrogateConverter()
        {
        }

        public StoredChatMessage ConvertFromSurrogate(in StoredChatMessageSurrogate surrogate)
        {
            return new StoredChatMessage
            {
                Id = surrogate.Id,
                Role = surrogate.Role,
                AuthorName = surrogate.AuthorName,
                Timestamp = surrogate.Timestamp,
                ContentsJson = surrogate.ContentsJson
            };
        }

        public StoredChatMessageSurrogate ConvertToSurrogate(in StoredChatMessage value)
        {
            return new StoredChatMessageSurrogate
            {
                Id = value.Id,
                Role = value.Role,
                AuthorName = value.AuthorName,
                Timestamp = value.Timestamp,
                ContentsJson = value.ContentsJson
            };
        }
    }
}

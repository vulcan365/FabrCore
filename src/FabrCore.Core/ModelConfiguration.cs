namespace FabrCore.Core
{
    public class ModelConfiguration
    {
        public required string Name { get; set; }
        public required string Provider { get; set; }
        public required string Uri { get; set; }
        public required string Model { get; set; }
        public required string ApiKeyAlias { get; set; }

        /// <summary>
        /// Network timeout in seconds. Default is 60 seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum number of tokens in the response. Default is null (no limit).
        /// Setting this can improve response time by limiting output length.
        /// </summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Total context window size in tokens for this model. Default is null (unknown).
        /// Used by compaction to determine when to summarize conversation history.
        /// </summary>
        public int? ContextWindowTokens { get; set; }
    }

    public class ApiKeyConfiguration
    {
        public required string Alias { get; set; }
        public required string Value { get; set; }
    }

    public class FabrCoreConfiguration
    {
        public List<ModelConfiguration> ModelConfigurations { get; set; } = new();
        public List<ApiKeyConfiguration> ApiKeys { get; set; } = new();
    }
}
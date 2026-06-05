using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// Result returned after attempting to permanently evict an agent grain.
    /// </summary>
    [GenerateSerializer]
    public record AgentEvictionResult
    {
        [Id(0)]
        public required string Handle { get; init; }

        [Id(1)]
        public required bool Success { get; init; }

        [Id(2)]
        public required bool Existed { get; init; }

        [Id(3)]
        public string? Message { get; init; }

        [Id(4)]
        public int TimersDisposed { get; init; }

        [Id(5)]
        public int RemindersUnregistered { get; init; }

        [Id(6)]
        public int StreamSubscriptionsRemoved { get; init; }

        [Id(7)]
        public bool StateCleared { get; init; }

        [Id(8)]
        public bool RegistryRemoved { get; init; }

        [Id(9)]
        public bool ClientTrackingRemoved { get; init; }

        [Id(10)]
        public required DateTime Timestamp { get; init; }
    }
}

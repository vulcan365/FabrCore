namespace FabrCore.SampleApp.Surface;

public sealed class InMemorySurfaceDemoDomainStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, SurfaceDemoDomainDataset> datasets = new(StringComparer.OrdinalIgnoreCase);

    public SurfaceDemoDomainDataset Seed(string agentHandle, SurfaceDemoDomainSeed seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);
        ArgumentNullException.ThrowIfNull(seed);

        lock (gate)
        {
            if (!datasets.TryGetValue(agentHandle, out var dataset))
            {
                dataset = SurfaceDemoDomainDataset.FromSeed(agentHandle, seed);
                datasets[agentHandle] = dataset;
            }

            return dataset.Clone();
        }
    }

    public SurfaceDemoDomainDataset GetDataset(string agentHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);

        lock (gate)
        {
            return RequireDataset(agentHandle).Clone();
        }
    }

    public IReadOnlyList<SurfaceDemoDomainRecord> SearchRecords(string agentHandle, string? search, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);

        lock (gate)
        {
            return RequireDataset(agentHandle).Records
                .Where(record => Matches(search, record.Id, record.Summary, record.Status))
                .OrderBy(record => record.Id)
                .Take(Math.Clamp(limit, 1, 25))
                .Select(record => record.Clone())
                .ToList();
        }
    }

    public SurfaceDemoDomainRecord AddRecord(string agentHandle, string summary, string? status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        lock (gate)
        {
            var dataset = RequireDataset(agentHandle);
            var record = new SurfaceDemoDomainRecord
            {
                Id = NextId(dataset),
                Summary = summary.Trim(),
                Status = string.IsNullOrWhiteSpace(status) ? "Open" : status.Trim(),
                UpdatedUtc = DateTime.UtcNow
            };
            dataset.Records.Add(record);
            dataset.UpdatedUtc = record.UpdatedUtc;
            return record.Clone();
        }
    }

    public SurfaceDemoDomainRecord UpdateRecord(string agentHandle, string recordId, string? summary, string? status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        lock (gate)
        {
            var dataset = RequireDataset(agentHandle);
            var record = dataset.Records.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, recordId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Demo record '{recordId}' was not found.");

            if (!string.IsNullOrWhiteSpace(summary))
            {
                record.Summary = summary.Trim();
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                record.Status = status.Trim();
            }

            record.UpdatedUtc = DateTime.UtcNow;
            dataset.UpdatedUtc = record.UpdatedUtc;
            return record.Clone();
        }
    }

    private SurfaceDemoDomainDataset RequireDataset(string agentHandle)
        => datasets.TryGetValue(agentHandle, out var dataset)
            ? dataset
            : throw new InvalidOperationException($"Demo domain data has not been seeded for '{agentHandle}'.");

    private static bool Matches(string? needle, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return true;
        }

        return values.Any(value => value?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string NextId(SurfaceDemoDomainDataset dataset)
    {
        var prefix = dataset.Records
            .Select(record => record.Id.Split('-', 2))
            .FirstOrDefault(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))?[0]
            ?? "DEMO";
        var next = dataset.Records
            .Select(record => record.Id.Split('-', 2))
            .Where(parts => parts.Length == 2 && string.Equals(parts[0], prefix, StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.TryParse(parts[1], out var parsed) ? parsed : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{prefix.ToUpperInvariant()}-{next:0000}";
    }
}

public sealed class SurfaceDemoDomainSeed
{
    public string Domain { get; init; } = "Demo Operations";

    public string Profile { get; init; } = "Domain Specialist";

    public IReadOnlyList<string> Responsibilities { get; init; } = [];

    public IReadOnlyList<string> Records { get; init; } = [];

    public IReadOnlyList<string> Decisions { get; init; } = [];

    public IReadOnlyList<string> Handoffs { get; init; } = [];
}

public sealed class SurfaceDemoDomainDataset
{
    public string AgentHandle { get; init; } = string.Empty;

    public string Domain { get; init; } = "Demo Operations";

    public string Profile { get; init; } = "Domain Specialist";

    public List<string> Responsibilities { get; init; } = [];

    public List<SurfaceDemoDomainRecord> Records { get; init; } = [];

    public List<string> Decisions { get; init; } = [];

    public List<string> Handoffs { get; init; } = [];

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public static SurfaceDemoDomainDataset FromSeed(string agentHandle, SurfaceDemoDomainSeed seed)
        => new()
        {
            AgentHandle = agentHandle,
            Domain = seed.Domain,
            Profile = seed.Profile,
            Responsibilities = [.. seed.Responsibilities],
            Records = [.. seed.Records.Select(ParseRecord)],
            Decisions = [.. seed.Decisions],
            Handoffs = [.. seed.Handoffs],
            UpdatedUtc = DateTime.UtcNow
        };

    public SurfaceDemoDomainDataset Clone()
        => new()
        {
            AgentHandle = AgentHandle,
            Domain = Domain,
            Profile = Profile,
            Responsibilities = [.. Responsibilities],
            Records = [.. Records.Select(record => record.Clone())],
            Decisions = [.. Decisions],
            Handoffs = [.. Handoffs],
            UpdatedUtc = UpdatedUtc
        };

    private static SurfaceDemoDomainRecord ParseRecord(string record, int index)
    {
        var parts = record.Split(':', 2, StringSplitOptions.TrimEntries);
        return new SurfaceDemoDomainRecord
        {
            Id = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0])
                ? parts[0]
                : $"DEMO-{index + 1:0000}",
            Summary = parts.Length == 2 ? parts[1] : record,
            Status = "Seeded",
            UpdatedUtc = DateTime.UtcNow
        };
    }
}

public sealed class SurfaceDemoDomainRecord
{
    public string Id { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Status { get; set; } = "Open";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public SurfaceDemoDomainRecord Clone()
        => new()
        {
            Id = Id,
            Summary = Summary,
            Status = Status,
            UpdatedUtc = UpdatedUtc
        };
}

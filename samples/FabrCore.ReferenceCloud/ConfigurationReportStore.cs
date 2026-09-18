using System.Text.Json;
using FabrCore.Core.CloudServer;

namespace FabrCore.ReferenceCloud;

// Intentionally uses only open wire contracts and the BCL. Replace this bounded in-memory
// store with tenant-scoped, transactional persistence in a production server.
internal sealed class ConfigurationReportStore(string clusterId, string environment)
{
    private readonly object gate = new();
    private readonly Dictionary<string, ConfigurationObservation> hosts = new(StringComparer.Ordinal);

    internal void Accept(CloudHeartbeatRequest heartbeat)
    {
        if (heartbeat.ClusterId != clusterId || heartbeat.Environment != environment ||
            string.IsNullOrWhiteSpace(heartbeat.HostInstanceId) || heartbeat.HostInstanceId.Length > 200)
            throw new ArgumentException("Invalid host scope.");
        var incoming = heartbeat.ConfigurationState is null ? null : Clone(heartbeat.ConfigurationState);
        if (incoming is not null) ValidateAndRedact(incoming);
        lock (gate)
        {
            if (!hosts.TryGetValue(heartbeat.HostInstanceId, out var previous) && hosts.Count >= 1000)
                throw new ArgumentException("Host limit reached.");
            var retained = previous?.ConfigurationState;
            // SDK hostInstanceId includes a process-specific identifier. A different boot
            // claiming the same identity cannot replace an already retained process report.
            if (incoming is not null && (retained is null ||
                incoming.BootId == retained.BootId && incoming.Sequence > retained.Sequence)) retained = incoming;
            hosts[heartbeat.HostInstanceId] = new(heartbeat.HostInstanceId, DateTimeOffset.UtcNow, retained);
        }
    }

    internal ConfigurationObservation[] Snapshot()
    {
        lock (gate) return hosts.Values.Select(h => h with
        { ConfigurationState = h.ConfigurationState is null ? null : Clone(h.ConfigurationState) }).ToArray();
    }

    internal Dictionary<string, string?> CreateDraft(string hostInstanceId, string[] keys, IDictionary<string, string?> desired)
    {
        lock (gate)
        {
            if (hostInstanceId is null || keys is null || keys.Length is 0 or > 256 ||
                !hosts.TryGetValue(hostInstanceId, out var host) || host.ConfigurationState is not { Started: true } report)
                throw new ArgumentException("No eligible observation.");
            var draft = new Dictionary<string, string?>(desired, StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var row = report.Settings.SingleOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
                if (row is null || !row.CanAdopt) throw new ArgumentException("Ineligible setting.");
                draft[row.Key] = row.AppliedValue;
            }
            return draft;
        }
    }

    private static CloudConfigurationState Clone(CloudConfigurationState value) =>
        JsonSerializer.Deserialize<CloudConfigurationState>(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;

    private static void ValidateAndRedact(CloudConfigurationState report)
    {
        if (report.ApiVersion != "1" || !Guid.TryParse(report.BootId, out _) || report.Sequence < 1 ||
            report.ObservedAt == default || report.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
            report.Settings is null || report.Settings.Count > 256 || report.ApplicationBuild?.Length > 256 ||
            report.DesiredRevision?.Length > 256 || report.RejectedRevision?.Length > 256 || report.Error?.Length > 512)
            throw new ArgumentException("Invalid report metadata.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in report.Settings)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Key) || row.Key.Length > 256 || !keys.Add(row.Key) ||
                row.Key.Split(':').Any(string.IsNullOrWhiteSpace) || Blocked(row.Key) ||
                string.IsNullOrWhiteSpace(row.Source) || row.Source.Length > 64 || row.SourceId?.Length > 256 ||
                row.Reason?.Length > 512 || row.DesiredValue?.Length > 8192 || row.ResolvedValue?.Length > 8192 ||
                row.AppliedValue?.Length > 8192 || row.AppliedRevision?.Length > 256 || row.ApplyMode is not ("Live" or "RestartRequired"))
                throw new ArgumentException("Invalid setting.");
            row.Secret |= row.Key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) ||
                new[] { "password", "secret", "apikey", "api-key", "token", "connectionstring", "credential", "privatekey" }
                    .Any(fragment => row.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (row.Secret) row.DesiredValue = row.ResolvedValue = row.AppliedValue = null;
            if (!report.Started || !row.AppliedKnown) { row.AppliedKnown = false; row.AppliedValue = null; row.PendingRestart = false; }
            row.CanAdopt &= row.AppliedKnown && !row.Secret && row.Source is not ("runtime" or "derived") &&
                !row.Key.StartsWith("Runtime:", StringComparison.OrdinalIgnoreCase) &&
                !row.Key.EndsWith(":ClusterId", StringComparison.OrdinalIgnoreCase) && !row.Key.EndsWith(":ServiceId", StringComparison.OrdinalIgnoreCase);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(report, JsonSerializerOptions.Web).Length > 196608)
            throw new ArgumentException("Report size limit exceeded.");
    }

    private static bool Blocked(string key) => new[] { "FabrCore:CloudServer", "FabrCore:RemoteAdministration", "FabrCore:HostUrl" }
        .Any(prefix => key.Equals(prefix, StringComparison.OrdinalIgnoreCase) || key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase));
}

internal sealed record ConfigurationObservation(string HostInstanceId, DateTimeOffset ReceivedAt, CloudConfigurationState? ConfigurationState);
internal sealed record ConfigurationDraftRequest(string HostInstanceId, string[] Keys);

using System.Reflection;
using System.Text.Json;
using FabrCore.Core.CloudServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FabrCore.Host.Configuration.Cloud;

internal sealed class RuntimeConfigurationState
{
    private readonly object gate = new();
    private readonly ConfigurationManager configuration;
    private readonly string environment;
    private readonly RuntimeConfigurationRule[] rules;
    private readonly RuleProvider provider = new();
    private readonly Dictionary<string, RuntimeSettingObservation> captured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Value, string Reason)> derived = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> startupInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> observationInputs = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RuntimeRuleValue> outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CloudSettingsState? cloud;
    private readonly string bootId = Guid.NewGuid().ToString("N");
    private long sequence;
    private string? startupRevision;
    private string? rejectedRevision, error;
    private Dictionary<string, string?> lastDesired;
    private string? receivedRevision;
    internal bool Started { get; private set; }
    internal Action<IServiceProvider, RuntimeConfigurationState>? CaptureInfrastructure { get; set; }
    internal Func<IConfiguration, IReadOnlyDictionary<string, string?>>? ResolveInfrastructure { get; set; }

    internal RuntimeConfigurationState(ConfigurationManager configuration, string environment,
        IEnumerable<RuntimeConfigurationRule> rules, CloudSettingsState? cloud)
    {
        this.configuration = configuration; this.environment = environment; this.rules = rules.ToArray(); this.cloud = cloud;
        lastDesired = cloud?.Provider.Snapshot() ?? [];
        receivedRevision = cloud?.AppliedSettingsVersion;
        if (this.rules.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != this.rules.Length)
            throw new InvalidOperationException("Runtime configuration rule names must be unique.");
        // A single overlay contains only explicit overrides and absent-key defaults. Operator sources stay last.
        var index = configuration.Sources.Count;
        while (index > 0 && configuration.Sources[index - 1] is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource) index--;
        ((IConfigurationBuilder)configuration).Sources.Insert(index, new RuleSource(provider));
        Commit(Evaluate(null));
        var a2aDefaults = new A2AOptions();
        Derived("A2A:Enabled", a2aDefaults.Enabled.ToString(), "Built-in A2A default.");
        Derived("A2A:Discovery:AgentTypes", a2aDefaults.Discovery.AgentTypes.ToString(), "Built-in A2A default.");
        cloud?.AttachRuntime(this);
    }

    private (Dictionary<string, string?> Values, Dictionary<string, RuntimeRuleValue> Rules) Evaluate(IDictionary<string, string?>? candidate)
    {
        var values = ReadInputs(candidate);
        var operators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ((IConfigurationRoot)configuration).Providers.Where(IsOperator))
            foreach (var key in values.Keys)
                if (p.TryGet(key, out _)) operators.Add(key);
        var results = new Dictionary<string, RuntimeRuleValue>(StringComparer.OrdinalIgnoreCase);
        var constraints = new List<(string Key, Func<string?, bool> Predicate, string Reason)>();
        foreach (var rule in rules)
            rule.Configure(new RuntimeConfigurationContext(environment, rule.Name, values, operators, results, constraints));
        foreach (var constraint in constraints)
            if (!constraint.Predicate(values.GetValueOrDefault(constraint.Key)))
                throw new InvalidOperationException($"Runtime constraint for '{constraint.Key}': {constraint.Reason}");
        return (values, results);
    }

    private Dictionary<string, string?> ReadInputs(IDictionary<string, string?>? candidate)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var keys = configuration.AsEnumerable().Select(p => p.Key).Concat(candidate?.Keys ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var p in ((IConfigurationRoot)configuration).Providers)
        {
            if (p == provider) continue;
            if (p == cloud?.Provider && candidate is not null)
            {
                foreach (var pair in candidate) result[pair.Key] = pair.Value;
                continue;
            }
            foreach (var key in keys) if (p.TryGet(key, out var value)) result[key] = value;
        }
        return result;
    }

    private void Commit((Dictionary<string, string?> Values, Dictionary<string, RuntimeRuleValue> Rules) evaluated)
    {
        outputs = evaluated.Rules;
        provider.Replace(outputs.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.OrdinalIgnoreCase));
    }

    internal void Apply(CloudConfigurationEnvelope envelope, Action commitCloud)
    {
        lock (gate)
        {
            lastDesired = CloudSettingsPolicy.Filter(envelope.Settings).Accepted;
            receivedRevision = envelope.ConfigurationVersion;
            try
            {
                var candidate = Evaluate(lastDesired);
                Commit(candidate);
                commitCloud(); // Change tokens fire only after code outputs and the validated cloud layer are installed.
                rejectedRevision = null; error = null;
            }
            catch
            {
                rejectedRevision = envelope.ConfigurationVersion;
                error = "Configuration could not be applied. The host log identifies the failed rule or consumer.";
                throw;
            }
        }
    }

    internal CloudConfigurationState Preview(IDictionary<string, string?> settings, IServiceProvider services)
    {
        lock (gate)
        {
            if (cloud is null) throw new InvalidOperationException("Cloud runtime settings are disabled on this host.");
            var filtered = CloudSettingsPolicy.Filter(settings);
            if (filtered.Rejected.Count != 0) throw new ArgumentException("The candidate contains unsupported or oversized settings.");
            var evaluated = Evaluate(filtered.Accepted);
            return BuildReport(services, filtered.Accepted, evaluated.Values, evaluated.Rules, preview: true);
        }
    }

    internal void Derived(string key, string? value, string reason) { lock (gate) derived[key] = (value, reason); }
    internal void Capture(RuntimeSettingObservation observation, bool refreshBaseline = false)
    {
        lock (gate)
        {
            if (refreshBaseline || !observationInputs.ContainsKey(observation.Key))
                observationInputs[observation.Key] = configuration[observation.Key] ?? startupInputs.GetValueOrDefault(observation.Key) ?? derived.GetValueOrDefault(observation.Key).Value;
            captured[observation.Key] = observation;
        }
    }
    internal void MarkStarted(IServiceProvider services)
    {
        lock (gate)
        {
            foreach (var pair in configuration.AsEnumerable()) startupInputs[pair.Key] = pair.Value;
            foreach (var pair in derived) if (!startupInputs.ContainsKey(pair.Key)) startupInputs[pair.Key] = pair.Value.Value;
            using var inputConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(configuration.AsEnumerable()).Build();
            try
            {
                foreach (var pair in ResolveInfrastructure?.Invoke(inputConfiguration) ?? new Dictionary<string, string?>())
                    if (startupInputs.GetValueOrDefault(pair.Key) is null) startupInputs[pair.Key] = pair.Value;
            }
            catch { error = "Infrastructure configuration could not be resolved; applied observations are retained."; }
            try { CaptureInfrastructure?.Invoke(services, this); }
            catch { error = "Infrastructure reporting is incomplete; a provider could not be inspected."; }
            startupRevision = cloud?.AppliedSettingsVersion;
            Started = true;
        }
    }
    internal CloudConfigurationState Report(IServiceProvider services)
    {
        lock (gate)
            return BuildReport(services, lastDesired, null, outputs, preview: false);
    }

    private CloudConfigurationState BuildReport(IServiceProvider services, IDictionary<string, string?> desired,
        Dictionary<string, string?>? previewValues, Dictionary<string, RuntimeRuleValue> ruleValues, bool preview)
    {
        var catalog = services.GetService<FabrCoreSettingsCatalog>() ?? new FabrCoreSettingsCatalog();
        var reportError = error;
        var observations = new Dictionary<string, RuntimeSettingObservation>(captured, StringComparer.OrdinalIgnoreCase);
        if (Started)
            foreach (var contributor in services.GetServices<IFabrCoreRuntimeSettingsContributor>())
            {
                try { foreach (var value in contributor.Observe()) observations[value.Key] = value; }
                catch { reportError = "A runtime contributor could not be inspected."; }
            }
        var resolvedDefaults = new Dictionary<string, (string? Value, string Reason)>(derived, StringComparer.OrdinalIgnoreCase);
        using var resolvedConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection((IEnumerable<KeyValuePair<string, string?>>?)previewValues ?? configuration.AsEnumerable()).Build();
        try
        {
            foreach (var pair in ResolveInfrastructure?.Invoke(resolvedConfiguration) ?? new Dictionary<string, string?>())
                resolvedDefaults[pair.Key] = (pair.Value, "Resolved from host defaults and database configuration.");
        }
        catch
        {
            if (preview) throw;
            reportError = "Infrastructure configuration could not be resolved; applied observations are retained.";
        }
        var keys = configuration.AsEnumerable().Select(p => p.Key).Concat(desired.Keys).Concat(observations.Keys).Concat(resolvedDefaults.Keys)
            .Concat(ruleValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var report = new CloudConfigurationState
        {
            BootId = bootId, Sequence = preview ? Math.Max(1, sequence) : ++sequence, ObservedAt = DateTimeOffset.UtcNow,
            DesiredRevision = preview ? null : receivedRevision, RejectedRevision = rejectedRevision, Error = reportError,
            Started = Started, ApplicationBuild = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"
        };
        var budget = 0;
        foreach (var key in keys)
        {
            if (CloudSettingsPolicy.IsBlocked(key) || catalog.Find(key) is null && !key.StartsWith("Runtime:", StringComparison.Ordinal)) continue;
            var hasDesired = desired.TryGetValue(key, out var desiredValue);
            var resolved = previewValues is null ? configuration[key] : previewValues.GetValueOrDefault(key);
            var source = "unknown"; string? sourceId = null, reason = null;
            IConfigurationProvider? winner = null;
            foreach (var p in ((IConfigurationRoot)configuration).Providers.Reverse())
            {
                if (p == provider && preview) continue;
                if (p == cloud?.Provider && preview) { if (desired.ContainsKey(key)) { winner = p; break; } else continue; }
                if (p.TryGet(key, out _)) { winner = p; break; }
            }
            if (winner is not null)
            {
                source = winner == cloud?.Provider ? "cloud" : IsOperator(winner) ? "operator" : "file-or-provider";
                sourceId = winner is JsonConfigurationProvider json ? Path.GetFileName(json.Source.Path) : winner.GetType().Name;
            }
            if (ruleValues.TryGetValue(key, out var rule) && (winner == provider || preview && (winner is null || !IsOperator(winner))))
            { source = rule.Kind; sourceId = rule.Rule; reason = rule.Reason; }
            if (resolved is null && resolvedDefaults.TryGetValue(key, out var derivation))
            { resolved = derivation.Value; source = "derived"; sourceId = "FabrCore.Database"; reason = derivation.Reason; }
            observations.TryGetValue(key, out var observation);
            if (resolved is null && observation is null && !hasDesired) continue;
            var known = Started && observation?.AppliedKnown == true;
            var resolvedKnown = true;
            var observedInput = observationInputs.TryGetValue(key, out var capturedInput) ? capturedInput : startupInputs.GetValueOrDefault(key);
            if (known && !key.StartsWith("Runtime:", StringComparison.Ordinal) &&
                !ValuesEqual(key, observedInput, observation!.Value))
            {
                // A consumer changed the bound options. The original callback cannot safely be replayed here.
                source = "code-or-consumer"; sourceId = observation.SourceId;
                reason = "Final consumer options differ from configuration. Use a named runtime rule for refresh and preview resolution.";
                resolvedKnown = ValuesEqual(key, resolved, observedInput);
                resolved = resolvedKnown ? observation.Value : null;
            }
            if (key.StartsWith("Runtime:", StringComparison.Ordinal))
            { source = "runtime"; sourceId = observation?.SourceId; resolved = observation?.Value; }
            var secret = FabrCoreSettingsCatalog.IsSecret(key) || observation?.Secret == true;
            var mode = catalog.GetApplyMode(key).ToString();
            var row = new CloudRuntimeSetting
            {
                Key = key, HasDesiredValue = hasDesired, DesiredValue = secret ? null : desiredValue,
                ResolvedValue = secret ? null : resolved, AppliedValue = secret || !known ? null : observation!.Value,
                AppliedKnown = known, Source = source, SourceId = sourceId, Reason = reason ?? observation?.Reason,
                Secret = secret, ApplyMode = mode,
                Overridden = hasDesired && source != "cloud",
                PendingRestart = known && resolvedKnown && mode == "RestartRequired" && !ValuesEqual(key, resolved, observation!.Value),
                AppliedRevision = known ? mode == "Live" && ValuesEqual(key, resolved, observation!.Value) ? cloud?.AppliedSettingsVersion : startupRevision : null,
                CanAdopt = known && !secret && source is not ("derived" or "runtime") &&
                    !key.EndsWith(":ClusterId", StringComparison.OrdinalIgnoreCase) && !key.EndsWith(":ServiceId", StringComparison.OrdinalIgnoreCase)
            };
            if (row.Key.Length > 256 || row.SourceId?.Length > 256 || row.Reason?.Length > 512 ||
                row.DesiredValue?.Length > 8192 || row.ResolvedValue?.Length > 8192 || row.AppliedValue?.Length > 8192)
            { report.Truncated = true; continue; }
            budget += key.Length + (row.DesiredValue?.Length ?? 0) + (row.ResolvedValue?.Length ?? 0) + (row.AppliedValue?.Length ?? 0) + 512;
            if (report.Settings.Count >= 256 || budget > 64000) { report.Truncated = true; break; }
            report.Settings.Add(row);
        }
        while (report.Settings.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(report, JsonSerializerOptions.Web).Length > 196608)
        { report.Truncated = true; report.Settings.RemoveAt(report.Settings.Count - 1); }
        return report;
    }

    private static bool IsOperator(IConfigurationProvider p) => p is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider;
    private static bool ValuesEqual(string key, string? left, string? right) =>
        bool.TryParse(left, out var a) && bool.TryParse(right, out var b) ? a == b :
        string.Equals(left, right, key.EndsWith(":ClusteringMode", StringComparison.OrdinalIgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private sealed class RuleProvider : ConfigurationProvider
    {
        internal void Replace(Dictionary<string, string?> values) => Data = values;
    }
    private sealed class RuleSource(RuleProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }
}

internal sealed class RuntimeConfigurationLifecycle(RuntimeConfigurationState state, IServiceProvider services) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken) { state.MarkStarted(services); return Task.CompletedTask; }
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

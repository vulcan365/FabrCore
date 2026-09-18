namespace FabrCore.Host.Configuration.Cloud;

/// <summary>Pure, bounded configuration transformation. Runs before consumers are constructed and on cloud refresh/preview.</summary>
internal sealed record RuntimeConfigurationRule(string Name, Action<RuntimeConfigurationContext> Configure);

/// <summary>Named rule context. Do not register services or perform external side effects in a rule.</summary>
public sealed class RuntimeConfigurationContext
{
    private readonly Dictionary<string, string?> values;
    private readonly HashSet<string> operatorKeys;
    private readonly Dictionary<string, RuntimeRuleValue> outputs;
    private readonly List<(string Key, Func<string?, bool> Predicate, string Reason)> constraints;
    private readonly string name;
    internal RuntimeConfigurationContext(string environment, string name, Dictionary<string, string?> values,
        HashSet<string> operatorKeys, Dictionary<string, RuntimeRuleValue> outputs,
        List<(string Key, Func<string?, bool> Predicate, string Reason)> constraints)
    {
        EnvironmentName = environment; this.name = name; this.values = values;
        this.operatorKeys = operatorKeys; this.outputs = outputs; this.constraints = constraints;
    }
    public string EnvironmentName { get; }
    public string? Get(string key) => values.GetValueOrDefault(key);
    public bool Contains(string key) => values.ContainsKey(key);
    public void Default(string key, string? value, string reason)
    {
        Validate(key, reason);
        if (!values.ContainsKey(key)) Set(key, value, "code-default", reason);
    }
    public void Override(string key, string? value, string reason) => Set(key, value, "code-override", reason);
    public void Require(string key, Func<string?, bool> predicate, string reason)
    {
        Validate(key, reason); ArgumentNullException.ThrowIfNull(predicate);
        constraints.Add((key, predicate, reason));
    }
    private void Set(string key, string? value, string kind, string reason)
    {
        Validate(key, reason);
        if (outputs.TryGetValue(key, out var previous) && previous.Rule != name)
            throw new InvalidOperationException($"Runtime setting '{key}' has competing code owners '{previous.Rule}' and '{name}'.");
        // Operator overrides are resolved after rules; preserve their value for subsequent rules and validation.
        outputs[key] = new(value, kind, name, reason);
        if (!operatorKeys.Contains(key)) values[key] = value;
    }
    private static void Validate(string key, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key); ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 512 || key.Length > 256 || CloudSettingsPolicy.IsBlocked(key) || key.Split(':').Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Runtime rules require a supported key and a reason of at most 512 characters.");
    }
}

internal sealed record RuntimeRuleValue(string? Value, string Kind, string Rule, string Reason);

/// <summary>A component's observation of the options/provider it actually uses. Values are redacted by the reporter.</summary>
public sealed record RuntimeSettingObservation(string Key, string? Value, string SourceId,
    bool AppliedKnown = true, bool Secret = false, string? Reason = null);

/// <summary>Implement for custom consumers. Observe actual instances; do not construct replacement services to infer state.</summary>
public interface IFabrCoreRuntimeSettingsContributor
{
    IEnumerable<RuntimeSettingObservation> Observe();
}

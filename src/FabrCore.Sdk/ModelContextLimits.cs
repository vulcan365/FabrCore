using System.Collections.Concurrent;
using FabrCore.Core;

namespace FabrCore.Sdk;

/// <summary>
/// Context limits providers have stated when rejecting an oversized prompt, shared by every agent in
/// the process and keyed by model configuration name.
/// </summary>
/// <remarks>
/// A stated limit only ever caps a configured window — it never raises one and never stands in for a
/// window that was not configured. Entries are dropped when the model configuration changes and after
/// <see cref="Lifetime"/>, because one configuration name can front more than one provider target.
/// </remarks>
internal sealed class ModelContextLimitRegistry(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int? Get(string modelConfigurationName, string fingerprint)
        => _entries.TryGetValue(modelConfigurationName, out var entry) && IsCurrent(entry, fingerprint)
            ? entry.Tokens
            : null;

    public void Record(string modelConfigurationName, string fingerprint, int tokens)
        => _entries.AddOrUpdate(
            modelConfigurationName,
            _ => new Entry(fingerprint, tokens, _time.GetUtcNow()),
            (_, existing) => IsCurrent(existing, fingerprint) && existing.Tokens <= tokens
                ? existing
                : new Entry(fingerprint, tokens, _time.GetUtcNow()));

    internal static string Fingerprint(ModelConfiguration model)
        => $"{model.Provider}|{model.Uri}|{model.Model}|{model.ChatApi}|{model.ContextWindowTokens}";

    private bool IsCurrent(Entry entry, string fingerprint)
        => entry.Fingerprint == fingerprint && _time.GetUtcNow() - entry.RecordedAt <= Lifetime;

    private sealed record Entry(string Fingerprint, int Tokens, DateTimeOffset RecordedAt);
}

/// <summary>
/// One model configuration's context limits as one agent activation sees them: the configured layer 1
/// settings, capped by anything a provider has since stated.
/// </summary>
/// <remarks>
/// Two things can be learned from a rejection. A limit the provider <i>stated</i> is a fact about the
/// model: it goes to the shared <see cref="ModelContextLimitRegistry"/> and is treated as the
/// configured window from then on. A rejection of a prompt our estimate said would fit is a fact about
/// this conversation's content: it stays in the activation and only tightens trimming, never a stop.
/// </remarks>
internal sealed class ModelContextLimits(string modelConfigurationName, ModelContextLimitRegistry registry)
{
    private const int MinStatedLimit = 2048;

    private string _fingerprint = string.Empty;
    private volatile int _estimateCeiling;

    public string ModelConfigurationName { get; } = modelConfigurationName;

    /// <summary>The configured layer 1 settings, before any learned cap.</summary>
    public ContextCompactionConfig Configured { get; private set; } = new();

    /// <summary>The limit a provider stated for this model configuration, if one is current.</summary>
    public int? StatedLimit => registry.Get(ModelConfigurationName, _fingerprint);

    /// <summary>Refreshes the configured settings; a changed model configuration forgets what was learned.</summary>
    public void Configure(ModelConfiguration? model, ContextCompactionConfig configured)
    {
        var fingerprint = model is null ? string.Empty : ModelContextLimitRegistry.Fingerprint(model);
        if (fingerprint != _fingerprint)
        {
            _fingerprint = fingerprint;
            _estimateCeiling = 0;
        }

        Configured = configured;
    }

    /// <summary>
    /// The configured settings capped by what has been learned. Hard stops use the stated limit only;
    /// trimming (<paramref name="forTrim"/>) also honours the activation's estimate ceiling.
    /// </summary>
    public ContextCompactionConfig Effective(bool forTrim)
    {
        var configured = Configured;
        var window = configured.MaxContextWindowTokens;
        if (window <= 0)
            return configured;

        var cap = StatedLimit ?? int.MaxValue;
        if (forTrim && _estimateCeiling is > 0 and var ceiling)
            cap = Math.Min(cap, ceiling);
        if (cap >= window)
            return configured;

        return configured with
        {
            MaxContextWindowTokens = cap,
            MaxOutputTokens = configured.OutputReserveIsDerived
                ? ContextCompaction.DeriveOutputReserve(cap)
                : configured.MaxOutputTokens,
            WindowIsLearned = true
        };
    }

    /// <summary>Records what a rejection of a prompt estimated at <paramref name="promptEstimate"/> tokens taught us.</summary>
    /// <returns>True when the provider stated a limit that now caps the configured window.</returns>
    public bool Record(ContextOverflowInfo overflow, long promptEstimate)
    {
        var window = Configured.MaxContextWindowTokens;
        var stated = overflow.LimitTokens is >= MinStatedLimit and var limit && (window <= 0 || limit < window)
            ? limit
            : (int?)null;
        if (stated is { } tokens)
            registry.Record(ModelConfigurationName, _fingerprint, tokens);

        if (window > 0)
        {
            // The prompt passed the local guard and was still refused, so the estimate undercounts
            // this conversation. Scale the provider's limit into estimate units when it gave both
            // numbers; otherwise the failing estimate itself is the ceiling. Never drop below half
            // the window in force, so one odd rejection cannot collapse the working set.
            var inForce = Math.Min(window, StatedLimit ?? int.MaxValue);
            var ceiling = overflow is { LimitTokens: > 0, RequestTokens: > 0 }
                ? promptEstimate * overflow.LimitTokens.Value / overflow.RequestTokens.Value
                : promptEstimate;
            ceiling = Math.Max(ceiling, inForce / 2);
            if (ceiling < inForce && (_estimateCeiling == 0 || ceiling < _estimateCeiling))
                _estimateCeiling = (int)ceiling;
        }

        return stated is not null && window > 0;
    }
}

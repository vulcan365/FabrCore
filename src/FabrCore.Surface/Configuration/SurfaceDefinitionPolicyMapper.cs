using FabrCore.Surface.Services;

namespace FabrCore.Surface.Configuration;

public static class SurfaceDefinitionPolicyMapper
{
    public static void ApplyTo(SurfaceDefinition definition, SurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        options.MaxAdaptiveCardVersion = definition.MaxAdaptiveCardVersion;
        if (definition.MaxPayloadBytes.HasValue)
        {
            options.MaxPayloadBytes = definition.MaxPayloadBytes.Value;
        }

        if (definition.MaxDepth.HasValue)
        {
            options.MaxDepth = definition.MaxDepth.Value;
        }

        if (definition.AllowHttpUrls.HasValue)
        {
            options.AllowHttpUrls = definition.AllowHttpUrls.Value;
        }

        if (definition.AllowAnyActionVerb.HasValue)
        {
            options.AllowAnyActionVerb = definition.AllowAnyActionVerb.Value;
        }

        if (definition.AllowUnknownTargetAgents.HasValue)
        {
            options.AllowUnknownTargetAgents = definition.AllowUnknownTargetAgents.Value;
        }

        if (definition.EnableDiagnostics.HasValue)
        {
            options.EnableDiagnostics = definition.EnableDiagnostics.Value;
        }

        ReplaceSet(options.AllowedActionTypes, definition.AllowedActionTypes);
        MergeSet(options.AllowedActionVerbs, definition.AllowedActionVerbs);
        MergeSet(options.AllowedTargetAgents, definition.AllowedTargetAgents);

        foreach (var requiredAction in definition.RequiredActions)
        {
            if (!string.IsNullOrWhiteSpace(requiredAction.Verb))
            {
                options.AllowedActionVerbs.Add(requiredAction.Verb);
            }

            if (!string.IsNullOrWhiteSpace(requiredAction.TargetAgent))
            {
                options.AllowedTargetAgents.Add(requiredAction.TargetAgent);
            }
        }
    }

    public static SurfaceOptions ToOptions(SurfaceDefinition definition)
    {
        var options = new SurfaceOptions();
        ApplyTo(definition, options);
        return options;
    }

    private static void ReplaceSet(HashSet<string> target, IEnumerable<string> values)
    {
        target.Clear();
        MergeSet(target, values);
    }

    private static void MergeSet(HashSet<string> target, IEnumerable<string> values)
    {
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            target.Add(value);
        }
    }
}

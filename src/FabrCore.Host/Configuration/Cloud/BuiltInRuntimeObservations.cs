using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Storage;
using Orleans.Streams;

namespace FabrCore.Host.Configuration.Cloud;

internal static class BuiltInRuntimeObservations
{
    internal static void CaptureOrleans(IServiceProvider services, RuntimeConfigurationState state)
    {
        var membership = services.GetService<Orleans.IMembershipTable>();
        var type = membership?.GetType();
        string? mode = null;
        if (type?.Assembly.GetName().Name == "Orleans.Clustering.AdoNet" && type.Name == "AdoNetClusteringTable")
            mode = services.GetRequiredService<IOptions<AdoNetClusteringSiloOptions>>().Value.Invariant == "Microsoft.Data.SqlClient" ? "SqlServer" : null;
        else if (type?.Assembly.GetName().Name == "Orleans.Runtime" && type.Name == "GrainBasedMembershipTable") mode = "Localhost";
        else if (type?.Assembly.GetName().Name == "Orleans.Clustering.AzureStorage" && type.Name == "AzureBasedMembershipTable") mode = "AzureStorage";
        state.Capture(new("FabrCore:Orleans:ClusteringMode", mode, type?.FullName ?? "No membership provider", AppliedKnown: mode is not null,
            Reason: mode is null ? "Custom membership implementation; use a runtime contributor to report its mode." : null));
        var cluster = services.GetRequiredService<IOptions<ClusterOptions>>().Value;
        state.Capture(new("FabrCore:Orleans:ClusterId", cluster.ClusterId, "Orleans.ClusterOptions"));
        state.Capture(new("FabrCore:Orleans:ServiceId", cluster.ServiceId, "Orleans.ClusterOptions"));
        CaptureProvider("Membership", membership);
        CaptureProvider("GrainStorage", services.GetKeyedService<IGrainStorage>(FabrCoreOrleansConstants.StorageProviderName));
        CaptureProvider("PubSubStorage", services.GetKeyedService<IGrainStorage>(FabrCoreOrleansConstants.PubSubStoreName));
        CaptureProvider("Reminders", services.GetService<Orleans.IReminderTable>());
        CaptureProvider("Streams", services.GetKeyedService<IStreamProvider>(FabrCoreOrleansConstants.StreamProviderName));
        void CaptureProvider(string name, object? provider) => state.Capture(new("Runtime:Orleans:" + name,
            provider?.GetType().FullName, "Orleans service registration", AppliedKnown: provider is not null));
    }
}

/// <summary>Reports the exact singleton A2A options supplied to endpoint consumers, including code callbacks.</summary>
internal sealed class A2ARuntimeSettingsContributor(IOptions<A2AOptions> options) : IFabrCoreRuntimeSettingsContributor
{
    public IEnumerable<RuntimeSettingObservation> Observe()
    {
        const string source = "A2A options pipeline";
        var value = options.Value;
        yield return new("A2A:Enabled", value.Enabled.ToString(), source);
        yield return new("A2A:Discovery:AgentTypes", value.Discovery.AgentTypes.ToString(), source);
        // Nothing consumes these while the endpoint is off, so they are not evidence of applied state.
        var defaults = new A2AOptions();
        yield return new("A2A:Authentication:Mode", value.Authentication.Mode.ToString(), source, AppliedKnown: value.Enabled)
        { DefaultValue = defaults.Authentication.Mode.ToString() };
        yield return new("A2A:Principal:Strategy", value.Principal.Strategy.ToString(), source, AppliedKnown: value.Enabled)
        { DefaultValue = defaults.Principal.Strategy.ToString() };
        yield return new("A2A:PublicBaseUrl", value.PublicBaseUrl, source, AppliedKnown: value.Enabled);
    }
}

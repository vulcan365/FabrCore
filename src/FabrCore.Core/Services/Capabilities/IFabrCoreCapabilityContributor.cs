using System.Collections.ObjectModel;

namespace FabrCore.Services.Contracts.Capabilities;

/// <summary>
/// Lets an add-on advertise itself to management consoles without the host knowing it exists.
/// Register with <c>services.AddSingleton&lt;IFabrCoreCapabilityContributor, MyContributor&gt;()</c>.
/// <para>
/// Register the contributor even when the feature is switched off and report
/// <see cref="ClusterServiceCapability.Available"/> as false, so a console can tell "installed
/// but disabled" apart from "not installed".
/// </para>
/// <para>
/// This lives in <c>FabrCore.Core</c> rather than the host so packages that reference only the
/// SDK can implement it. Implementations must be cheap and side-effect free: they run on every
/// capability request and every heartbeat. A contributor that throws is skipped.
/// </para>
/// </summary>
public interface IFabrCoreCapabilityContributor
{
    /// <summary>
    /// Services for the capability document served at <c>/fabrcoreapi/capabilities</c>. A service
    /// whose name the host or an earlier contributor already claimed is ignored.
    /// </summary>
    IEnumerable<ClusterServiceCapability> GetServices();

    /// <summary>
    /// Flat flags for the cloud-server heartbeat <c>capabilities</c> map. Keys the host already
    /// set are never overwritten.
    /// </summary>
    IReadOnlyDictionary<string, string> GetHeartbeatCapabilities() => ReadOnlyDictionary<string, string>.Empty;
}

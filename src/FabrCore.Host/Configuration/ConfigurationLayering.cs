using FabrCore.Host.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace FabrCore.Host.Configuration;

/// <summary>Where a layer FabrCore adds to configuration goes.</summary>
internal static class ConfigurationLayering
{
    /// <summary>
    /// The position just before the trailing run of an operator's own sources: environment variables, the command line and the host's own
    /// database (<see cref="FabrCoreHostDatabase"/>). Scanned backwards, because a WebApplicationBuilder carries environment sources on both
    /// sides of the appsettings files.
    /// </summary>
    internal static int TrailingIndex(IList<IConfigurationSource> sources)
    {
        var index = sources.Count;
        while (index > 0 && sources[index - 1] is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource or HostDatabaseConfigurationSource)
            index--;
        return index;
    }
}

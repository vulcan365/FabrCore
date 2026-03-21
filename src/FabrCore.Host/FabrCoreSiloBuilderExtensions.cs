using FabrCore.Host.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace FabrCore.Host
{
    /// <summary>
    /// Extension methods for <see cref="ISiloBuilder"/> to register FabrCore grain assemblies.
    /// Use these when configuring Orleans manually instead of using <see cref="FabrCoreHostExtensions.AddFabrCoreServer"/>.
    /// </summary>
    public static class FabrCoreSiloBuilderExtensions
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.SiloBuilder");
        private static readonly Meter Meter = new("FabrCore.Host.SiloBuilder");

        private static readonly Counter<long> AssembliesLoadedCounter = Meter.CreateCounter<long>(
            "fabrcore.host.silobuilder.assemblies.loaded",
            description: "Number of additional assemblies loaded via AddFabrCore");

        /// <summary>
        /// Registers FabrCore grain assemblies with the Orleans silo.
        /// <para>
        /// Call this inside your own <c>builder.UseOrleans(siloBuilder => { ... })</c> when you want
        /// full control over Orleans providers. You are responsible for registering:
        /// <list type="bullet">
        ///   <item>A grain storage provider named <see cref="FabrCoreOrleansConstants.StorageProviderName"/> ("fabrcoreStorage")</item>
        ///   <item>A grain storage provider named <see cref="FabrCoreOrleansConstants.PubSubStoreName"/> ("fabrcorePubSub")</item>
        ///   <item>A stream provider named <see cref="FabrCoreOrleansConstants.StreamProviderName"/> ("fabrcoreStreams")</item>
        ///   <item>A reminder service</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="siloBuilder">The Orleans silo builder.</param>
        /// <param name="additionalAssemblies">Assemblies containing agents, plugins, and tools to discover.</param>
        /// <returns>The silo builder for chaining.</returns>
        public static ISiloBuilder AddFabrCore(this ISiloBuilder siloBuilder, List<Assembly> additionalAssemblies)
        {
            using var activity = ActivitySource.StartActivity("AddFabrCore", ActivityKind.Internal);

            var loadedCount = 0;
            foreach (var assembly in additionalAssemblies)
            {
                using var assemblyActivity = ActivitySource.StartActivity("LoadAssembly", ActivityKind.Internal);
                assemblyActivity?.SetTag("assembly.name", assembly.GetName().Name);

                Assembly.Load(assembly.GetName().Name!);
                loadedCount++;

                AssembliesLoadedCounter.Add(1,
                    new KeyValuePair<string, object?>("assembly.name", assembly.GetName().Name));

                assemblyActivity?.SetStatus(ActivityStatusCode.Ok);
            }

            activity?.SetTag("assemblies.loaded", loadedCount);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return siloBuilder;
        }
    }
}

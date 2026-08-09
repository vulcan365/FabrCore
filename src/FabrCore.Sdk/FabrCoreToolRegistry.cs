using FabrCore.Core;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Sdk.VerifiableExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Reflection;

namespace FabrCore.Sdk
{
    public sealed class FabrCoreToolRegistry
    {
        private readonly ILogger<FabrCoreToolRegistry> _logger;
        private readonly IReadOnlyList<Assembly>? _assemblies;
        private readonly Lazy<Dictionary<string, Type>> _pluginTypes;
        private readonly Lazy<Dictionary<string, MethodInfo>> _toolMethods;

        public FabrCoreToolRegistry(ILogger<FabrCoreToolRegistry> logger)
            : this(logger, (IReadOnlyList<Assembly>?)null)
        {
        }

        /// <summary>
        /// Creates a tool registry which scans only the supplied assemblies. Pass an empty
        /// collection to create an empty registry. The single-argument constructor retains the
        /// legacy process-wide scan for backwards compatibility.
        /// </summary>
        public FabrCoreToolRegistry(ILogger<FabrCoreToolRegistry> logger, IEnumerable<Assembly> assemblies)
            : this(
                logger,
                (IReadOnlyList<Assembly>)(assemblies?.Distinct().ToArray()
                    ?? throw new ArgumentNullException(nameof(assemblies))))
        {
        }

        private FabrCoreToolRegistry(
            ILogger<FabrCoreToolRegistry> logger,
            IReadOnlyList<Assembly>? assemblies)
        {
            _logger = logger;
            _assemblies = assemblies;
            _pluginTypes = new Lazy<Dictionary<string, Type>>(ScanPlugins);
            _toolMethods = new Lazy<Dictionary<string, MethodInfo>>(ScanTools);
        }

        public async Task<List<AITool>> ResolveToolsAsync(
            IServiceProvider serviceProvider,
            IEnumerable<string>? pluginAliases,
            IEnumerable<string>? toolAliases,
            AgentConfiguration config,
            IFabrCoreAgentHost? agentHost = null)
        {
            var tools = new List<AITool>();
            var resolvedNames = new List<string>();

            if (pluginAliases != null)
            {
                foreach (var alias in pluginAliases)
                {
                    var (resolved, names, _) = await ResolvePluginAsync(serviceProvider, alias, config, agentHost);
                    tools.AddRange(resolved);
                    resolvedNames.AddRange(names);
                }
            }

            if (toolAliases != null)
            {
                foreach (var alias in toolAliases)
                {
                    var resolved = ResolveStandaloneTool(alias, serviceProvider);
                    if (resolved != null)
                    {
                        tools.Add(resolved);
                        resolvedNames.Add(alias);
                        _logger.LogInformation("Resolved standalone tool '{Alias}'", alias);
                    }
                }
            }

            _logger.LogInformation("Resolved {ToolCount} tools from {PluginCount} plugins and {StandaloneCount} standalone tools: [{ToolNames}]",
                tools.Count,
                pluginAliases?.Count() ?? 0,
                toolAliases?.Count() ?? 0,
                string.Join(", ", resolvedNames));

            return tools;
        }

        /// <summary>
        /// Resolves a required, isolated tool set and retains ownership of disposable plugin instances.
        /// Unlike <see cref="ResolveToolsAsync"/>, every requested alias must resolve to at least one tool.
        /// </summary>
        public async Task<FabrCoreResolvedToolScope> ResolveToolScopeAsync(
            IServiceProvider serviceProvider,
            IEnumerable<string>? pluginAliases,
            IEnumerable<string>? toolAliases,
            AgentConfiguration config,
            IFabrCoreAgentHost? agentHost = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(config);

            var tools = new List<AITool>();
            var resources = new List<object>();
            try
            {
                foreach (var alias in pluginAliases ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (resolved, _, instance) = await ResolvePluginAsync(serviceProvider, alias, config, agentHost);
                    if (instance is IDisposable or IAsyncDisposable) resources.Add(instance);
                    if (resolved.Count == 0)
                        throw new InvalidOperationException($"Required plugin alias '{alias}' did not resolve to any tools.");

                    tools.AddRange(resolved);
                }

                foreach (var alias in toolAliases ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolved = ResolveStandaloneTool(alias, serviceProvider)
                        ?? throw new InvalidOperationException($"Required tool alias '{alias}' could not be resolved.");
                    tools.Add(resolved);
                }

                return new FabrCoreResolvedToolScope(tools, resources);
            }
            catch
            {
                await FabrCoreResolvedToolScope.DisposeResourcesAsync(resources);
                throw;
            }
        }

        private async Task<(List<AITool> Tools, List<string> Names, object? Instance)> ResolvePluginAsync(
            IServiceProvider serviceProvider,
            string alias,
            AgentConfiguration config,
            IFabrCoreAgentHost? agentHost)
        {
            if (!_pluginTypes.Value.TryGetValue(alias, out var pluginType))
            {
                _logger.LogWarning("Plugin alias '{Alias}' not found", alias);
                return (new List<AITool>(), new List<string>(), null);
            }

            // Create plugin-scoped provider that includes IFabrCoreAgentHost
            var pluginServiceProvider = agentHost != null
                ? new PluginServiceProvider(serviceProvider, agentHost)
                : serviceProvider;

            object instance;
            try
            {
                instance = ActivatorUtilities.CreateInstance(pluginServiceProvider, pluginType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create plugin instance for alias '{Alias}' (type: {Type})", alias, pluginType.FullName);
                return (new List<AITool>(), new List<string>(), null);
            }

            if (instance is IFabrCorePlugin fabrcorePlugin)
            {
                try
                {
                    await fabrcorePlugin.InitializeAsync(config, pluginServiceProvider);
                    _logger.LogInformation("Initialized plugin '{Alias}'", alias);
                }
                catch
                {
                    await FabrCoreResolvedToolScope.DisposeResourcesAsync([instance]);
                    throw;
                }
            }

            var tools = new List<AITool>();
            var toolNames = new List<string>();
            var methods = pluginType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null && m.DeclaringType != typeof(object));

            foreach (var method in methods)
            {
                try
                {
                    var tool = AIFunctionFactory.Create(method, instance);
                    var verifiableExecution = pluginServiceProvider.GetService<IVerifiableExecutionContext>();
                    if (tool is AIFunction function)
                    {
                        tool = new VerifiableExecutionAIFunction(
                            function,
                            verifiableExecution,
                            ExecutionRecordKind.PluginCall,
                            alias,
                            method.Name,
                            _logger);
                    }

                    tools.Add(tool);
                    toolNames.Add(method.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create AITool from method '{Method}' on plugin '{Alias}'", method.Name, alias);
                }
            }

            _logger.LogInformation("Plugin '{Alias}' provided {ToolCount} tools: [{ToolNames}]", alias, tools.Count, string.Join(", ", toolNames));
            return (tools, toolNames, instance);
        }

        private AITool? ResolveStandaloneTool(string alias, IServiceProvider serviceProvider)
        {
            if (!_toolMethods.Value.TryGetValue(alias, out var method))
            {
                _logger.LogWarning("Tool alias '{Alias}' not found", alias);
                return null;
            }

            try
            {
                var tool = AIFunctionFactory.Create(method, target: null);
                var verifiableExecution = serviceProvider.GetService<IVerifiableExecutionContext>();
                return tool is AIFunction function
                    ? new VerifiableExecutionAIFunction(
                        function,
                        verifiableExecution,
                        ExecutionRecordKind.ToolCall,
                        alias,
                        method.Name,
                        _logger)
                    : tool;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create AITool for alias '{Alias}'", alias);
                return null;
            }
        }

        private Dictionary<string, Type> ScanPlugins()
        {
            var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    var aliases = type.GetCustomAttributes<PluginAliasAttribute>();
                    foreach (var attr in aliases)
                    {
                        if (!string.IsNullOrEmpty(attr.Alias))
                        {
                            result[attr.Alias] = type;
                            _logger.LogTrace("Registered plugin alias '{Alias}' -> {Type}", attr.Alias, type.FullName);
                        }
                    }
                }
            }

            _logger.LogInformation("Plugin scan complete: {Count} plugin aliases registered", result.Count);
            return result;
        }

        private Dictionary<string, MethodInfo> ScanTools()
        {
            var result = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    foreach (var method in methods)
                    {
                        var aliases = method.GetCustomAttributes<ToolAliasAttribute>();
                        foreach (var attr in aliases)
                        {
                            if (!string.IsNullOrEmpty(attr.Alias))
                            {
                                result[attr.Alias] = method;
                                _logger.LogTrace("Registered tool alias '{Alias}' -> {Type}.{Method}", attr.Alias, type.FullName, method.Name);
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("Tool scan complete: {Count} tool aliases registered", result.Count);
            return result;
        }

        private IEnumerable<Assembly> GetAssemblies() =>
            _assemblies ?? AppDomain.CurrentDomain.GetAssemblies();

        private sealed class PluginServiceProvider : IServiceProvider
        {
            private readonly IServiceProvider _inner;
            private readonly IFabrCoreAgentHost _agentHost;

            public PluginServiceProvider(IServiceProvider inner, IFabrCoreAgentHost agentHost)
            {
                _inner = inner;
                _agentHost = agentHost;
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IFabrCoreAgentHost))
                    return _agentHost;
                return _inner.GetService(serviceType);
            }
        }
    }

    /// <summary>Owns a required tool set and any disposable plugin instances created for it.</summary>
    public sealed class FabrCoreResolvedToolScope : IAsyncDisposable
    {
        private readonly IReadOnlyList<object> resources;
        private int disposed;

        internal FabrCoreResolvedToolScope(IReadOnlyList<AITool> tools, IReadOnlyList<object> resources)
        {
            Tools = tools;
            this.resources = resources;
        }

        public IReadOnlyList<AITool> Tools { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await DisposeResourcesAsync(resources);
        }

        internal static async Task DisposeResourcesAsync(IEnumerable<object> resources)
        {
            foreach (var resource in resources.Reverse())
            {
                switch (resource)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
    }
}

using Fabr.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Reflection;

namespace Fabr.Sdk
{
    public sealed class FabrToolRegistry
    {
        private readonly ILogger<FabrToolRegistry> _logger;
        private readonly Lazy<Dictionary<string, Type>> _pluginTypes;
        private readonly Lazy<Dictionary<string, MethodInfo>> _toolMethods;

        public FabrToolRegistry(ILogger<FabrToolRegistry> logger)
        {
            _logger = logger;
            _pluginTypes = new Lazy<Dictionary<string, Type>>(ScanPlugins);
            _toolMethods = new Lazy<Dictionary<string, MethodInfo>>(ScanTools);
        }

        public async Task<List<AITool>> ResolveToolsAsync(
            IServiceProvider serviceProvider,
            IEnumerable<string>? pluginAliases,
            IEnumerable<string>? toolAliases,
            AgentConfiguration config,
            IFabrAgentHost? agentHost = null)
        {
            var tools = new List<AITool>();
            var resolvedNames = new List<string>();

            if (pluginAliases != null)
            {
                foreach (var alias in pluginAliases)
                {
                    var (resolved, names) = await ResolvePluginAsync(serviceProvider, alias, config, agentHost);
                    tools.AddRange(resolved);
                    resolvedNames.AddRange(names);
                }
            }

            if (toolAliases != null)
            {
                foreach (var alias in toolAliases)
                {
                    var resolved = ResolveStandaloneTool(alias);
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

        private async Task<(List<AITool> Tools, List<string> Names)> ResolvePluginAsync(
            IServiceProvider serviceProvider,
            string alias,
            AgentConfiguration config,
            IFabrAgentHost? agentHost)
        {
            if (!_pluginTypes.Value.TryGetValue(alias, out var pluginType))
            {
                _logger.LogWarning("Plugin alias '{Alias}' not found", alias);
                return (new List<AITool>(), new List<string>());
            }

            // Create plugin-scoped provider that includes IFabrAgentHost
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
                return (new List<AITool>(), new List<string>());
            }

            if (instance is IFabrPlugin fabrPlugin)
            {
                await fabrPlugin.InitializeAsync(config, pluginServiceProvider);
                _logger.LogInformation("Initialized plugin '{Alias}'", alias);
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
                    tools.Add(tool);
                    toolNames.Add(method.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create AITool from method '{Method}' on plugin '{Alias}'", method.Name, alias);
                }
            }

            _logger.LogInformation("Plugin '{Alias}' provided {ToolCount} tools: [{ToolNames}]", alias, tools.Count, string.Join(", ", toolNames));
            return (tools, toolNames);
        }

        private AITool? ResolveStandaloneTool(string alias)
        {
            if (!_toolMethods.Value.TryGetValue(alias, out var method))
            {
                _logger.LogWarning("Tool alias '{Alias}' not found", alias);
                return null;
            }

            try
            {
                return AIFunctionFactory.Create(method, target: null);
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

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

        private sealed class PluginServiceProvider : IServiceProvider
        {
            private readonly IServiceProvider _inner;
            private readonly IFabrAgentHost _agentHost;

            public PluginServiceProvider(IServiceProvider inner, IFabrAgentHost agentHost)
            {
                _inner = inner;
                _agentHost = agentHost;
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IFabrAgentHost))
                    return _agentHost;
                return _inner.GetService(serviceType);
            }
        }
    }
}

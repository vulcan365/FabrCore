using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Fabr.Sdk
{
    public sealed class FabrRegistry : IFabrRegistry
    {
        private readonly ILogger<FabrRegistry> _logger;
        private readonly Lazy<Dictionary<string, Type>> _agentTypes;
        private readonly Lazy<Dictionary<string, Type>> _pluginTypes;
        private readonly Lazy<Dictionary<string, MethodInfo>> _toolMethods;

        public FabrRegistry(ILogger<FabrRegistry> logger)
        {
            _logger = logger;
            _agentTypes = new Lazy<Dictionary<string, Type>>(ScanAgents);
            _pluginTypes = new Lazy<Dictionary<string, Type>>(ScanPlugins);
            _toolMethods = new Lazy<Dictionary<string, MethodInfo>>(ScanTools);
        }

        public List<RegistryEntry> GetAgentTypes()
        {
            return _agentTypes.Value
                .GroupBy(kv => kv.Value.FullName ?? kv.Value.Name)
                .Select(g => new RegistryEntry
                {
                    TypeName = g.Key,
                    Aliases = g.Select(kv => kv.Key).ToList()
                })
                .ToList();
        }

        public List<RegistryEntry> GetPlugins()
        {
            return _pluginTypes.Value
                .GroupBy(kv => kv.Value.FullName ?? kv.Value.Name)
                .Select(g => new RegistryEntry
                {
                    TypeName = g.Key,
                    Aliases = g.Select(kv => kv.Key).ToList()
                })
                .ToList();
        }

        public List<RegistryEntry> GetTools()
        {
            return _toolMethods.Value
                .GroupBy(kv => $"{kv.Value.DeclaringType?.FullName ?? kv.Value.DeclaringType?.Name ?? "Unknown"}.{kv.Value.Name}")
                .Select(g => new RegistryEntry
                {
                    TypeName = g.Key,
                    Aliases = g.Select(kv => kv.Key).ToList()
                })
                .ToList();
        }

        public Type? FindAgentType(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                return null;

            _agentTypes.Value.TryGetValue(alias, out var type);
            return type;
        }

        private Dictionary<string, Type> ScanAgents()
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
                    var aliases = type.GetCustomAttributes<AgentAliasAttribute>();
                    foreach (var attr in aliases)
                    {
                        if (!string.IsNullOrEmpty(attr.Alias))
                        {
                            result[attr.Alias] = type;
                            _logger.LogTrace("Registered agent alias '{Alias}' -> {Type}", attr.Alias, type.FullName);
                        }
                    }
                }
            }

            _logger.LogInformation("Agent scan complete: {Count} agent aliases registered", result.Count);
            return result;
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
    }
}

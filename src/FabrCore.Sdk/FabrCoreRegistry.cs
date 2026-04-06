using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Reflection;

namespace FabrCore.Sdk
{
    public sealed class FabrCoreRegistry : IFabrCoreRegistry
    {
        private readonly ILogger<FabrCoreRegistry> _logger;
        private readonly Lazy<Dictionary<string, Type>> _agentTypes;
        private readonly Lazy<Dictionary<string, Type>> _pluginTypes;
        private readonly Lazy<Dictionary<string, MethodInfo>> _toolMethods;
        private readonly List<RegistryCollision> _collisions = new();

        public FabrCoreRegistry(ILogger<FabrCoreRegistry> logger)
        {
            _logger = logger;
            _agentTypes = new Lazy<Dictionary<string, Type>>(ScanAgents);
            _pluginTypes = new Lazy<Dictionary<string, Type>>(ScanPlugins);
            _toolMethods = new Lazy<Dictionary<string, MethodInfo>>(ScanTools);
        }

        public List<RegistryEntry> GetAgentTypes()
        {
            return _agentTypes.Value
                .Where(kv => kv.Value.GetCustomAttribute<FabrCoreHiddenAttribute>() == null)
                .GroupBy(kv => kv.Value.FullName ?? kv.Value.Name)
                .Select(g =>
                {
                    var type = g.First().Value;
                    return new RegistryEntry
                    {
                        TypeName = g.Key,
                        Aliases = g.Select(kv => kv.Key).ToList(),
                        Description = type.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        Capabilities = type.GetCustomAttribute<FabrCoreCapabilitiesAttribute>()?.Description,
                        Notes = type.GetCustomAttributes<FabrCoreNoteAttribute>()
                            .Select(a => a.Note)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .ToList()
                    };
                })
                .ToList();
        }

        public List<RegistryEntry> GetPlugins()
        {
            return _pluginTypes.Value
                .Where(kv => kv.Value.GetCustomAttribute<FabrCoreHiddenAttribute>() == null)
                .GroupBy(kv => kv.Value.FullName ?? kv.Value.Name)
                .Select(g =>
                {
                    var type = g.First().Value;
                    return new RegistryEntry
                    {
                        TypeName = g.Key,
                        Aliases = g.Select(kv => kv.Key).ToList(),
                        Description = type.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        Capabilities = type.GetCustomAttribute<FabrCoreCapabilitiesAttribute>()?.Description,
                        Notes = type.GetCustomAttributes<FabrCoreNoteAttribute>()
                            .Select(a => a.Note)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .ToList(),
                        Methods = type
                            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null && m.DeclaringType != typeof(object))
                            .Select(m => new RegistryMethodEntry
                            {
                                Name = m.Name,
                                Description = m.GetCustomAttribute<DescriptionAttribute>()!.Description
                            })
                            .ToList()
                    };
                })
                .ToList();
        }

        public List<RegistryEntry> GetTools()
        {
            return _toolMethods.Value
                .Where(kv => kv.Value.GetCustomAttribute<FabrCoreHiddenAttribute>() == null)
                .GroupBy(kv => $"{kv.Value.DeclaringType?.FullName ?? kv.Value.DeclaringType?.Name ?? "Unknown"}.{kv.Value.Name}")
                .Select(g =>
                {
                    var method = g.First().Value;
                    var desc = method.GetCustomAttribute<DescriptionAttribute>();
                    return new RegistryEntry
                    {
                        TypeName = g.Key,
                        Aliases = g.Select(kv => kv.Key).ToList(),
                        Description = desc?.Description,
                        Capabilities = method.GetCustomAttribute<FabrCoreCapabilitiesAttribute>()?.Description,
                        Notes = method.GetCustomAttributes<FabrCoreNoteAttribute>()
                            .Select(a => a.Note)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .ToList(),
                        Methods = new List<RegistryMethodEntry>
                        {
                            new RegistryMethodEntry
                            {
                                Name = method.Name,
                                Description = desc?.Description ?? string.Empty
                            }
                        }
                    };
                })
                .ToList();
        }

        public List<RegistryCollision> GetCollisions()
        {
            // Force all scans to run so collisions are fully populated
            _ = _agentTypes.Value;
            _ = _pluginTypes.Value;
            _ = _toolMethods.Value;
            return _collisions.ToList();
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
                            if (result.TryGetValue(attr.Alias, out var existing) && existing != type)
                            {
                                RecordCollision("agent", attr.Alias, existing.FullName ?? existing.Name, type.FullName ?? type.Name);
                            }
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
                            if (result.TryGetValue(attr.Alias, out var existing) && existing != type)
                            {
                                RecordCollision("plugin", attr.Alias, existing.FullName ?? existing.Name, type.FullName ?? type.Name);
                            }
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
                                if (result.TryGetValue(attr.Alias, out var existing) && existing != method)
                                {
                                    var existingName = $"{existing.DeclaringType?.FullName ?? existing.DeclaringType?.Name}.{existing.Name}";
                                    var newName = $"{type.FullName ?? type.Name}.{method.Name}";
                                    RecordCollision("tool", attr.Alias, existingName, newName);
                                }
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

        private void RecordCollision(string category, string alias, string existingType, string newType)
        {
            _logger.LogWarning("Registry collision: {Category} alias '{Alias}' claimed by both '{ExistingType}' and '{NewType}' — '{NewType}' wins",
                category, alias, existingType, newType);

            var collision = _collisions.FirstOrDefault(c => c.Alias == alias && c.Category == category);
            if (collision != null)
            {
                if (!collision.Types.Contains(newType))
                    collision.Types.Add(newType);
            }
            else
            {
                _collisions.Add(new RegistryCollision
                {
                    Alias = alias,
                    Category = category,
                    Types = new List<string> { existingType, newType }
                });
            }
        }
    }
}

using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Core.Blueprints;

namespace FabrCore.Services.Connections;

/// <summary>Expands a package-owned agent group with reusable connection defaults; never provisions consent.</summary>
public sealed class ConnectionBlueprintExpander : IBlueprintPreviewExpander
{
    public string ExtensionKey => "connectedAgents";
    public JsonElement? ConfigurationSchema => JsonSerializer.SerializeToElement(new {
        type = "object", required = new[] { "agents" }, properties = new {
            connections = new { type = "object" }, agents = new { type = "array", items = new { type = "object" } }
        }
    });
    public ValueTask<BlueprintExpansion> ExpandAsync(BlueprintExpansionContext context, JsonElement extension, CancellationToken cancellationToken = default)
    {
        var group = extension.Deserialize<ConnectedAgents>(JsonSerializerOptions.Web) ?? throw new ArgumentException("Invalid connectedAgents definition.");
        var expansion = new BlueprintExpansion();
        foreach (var definition in group.Agents)
        {
            var agent = JsonSerializer.Deserialize<AgentConfiguration>(JsonSerializer.Serialize(definition), JsonSerializerOptions.Web)!;
            var bindings = new Dictionary<string, ConnectionBinding>(group.Connections, StringComparer.Ordinal);
            if (agent.Args.TryGetValue("connections", out var overrides))
                foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, ConnectionBinding>>(overrides, JsonSerializerOptions.Web)!) bindings[entry.Key] = entry.Value;
            foreach (var key in bindings.Keys.ToArray())
            {
                var binding = bindings[key];
                if (string.IsNullOrWhiteSpace(binding.Name)) throw new ArgumentException("Connection names cannot be empty.");
                if (string.IsNullOrWhiteSpace(binding.OwnerPrincipal) || binding.OwnerPrincipal == "$principal")
                    bindings[key] = binding with { OwnerPrincipal = context.PrincipalId };
            }
            agent.Args["connections"] = JsonSerializer.Serialize(bindings, JsonSerializerOptions.Web);
            expansion.Agents.Add(agent);
        }
        return ValueTask.FromResult(expansion);
    }
    private sealed class ConnectedAgents
    {
        public Dictionary<string, ConnectionBinding> Connections { get; set; } = [];
        public List<AgentConfiguration> Agents { get; set; } = [];
    }
}

using FabrCore.Core;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceAgentConfigurationDraft
{
    public string Handle { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string Models { get; set; } = "default";

    public string Description { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public string PluginAliases { get; set; } = string.Empty;

    public string ToolAliases { get; set; } = string.Empty;

    public string Streams { get; set; } = string.Empty;

    public List<SurfaceKeyValueDraftRow> Args { get; } = [new()];

    public List<SurfaceMcpServerDraft> McpServers { get; } = [];

    public bool ForceReconfigure { get; set; }

    public AgentConfiguration Build()
    {
        return new AgentConfiguration
        {
            Handle = Required(Handle, "Handle"),
            AgentType = Required(AgentType, "Agent type"),
            Models = Optional(Models),
            Description = Optional(Description),
            SystemPrompt = Optional(SystemPrompt),
            Plugins = SplitList(PluginAliases),
            Tools = SplitList(ToolAliases),
            Streams = SurfaceEventStreamSubscriptions.Split(Streams),
            Args = Args
                .Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .ToDictionary(row => row.Key.Trim(), row => row.Value.Trim(), StringComparer.OrdinalIgnoreCase),
            McpServers = McpServers
                .Select(server => server.Build())
                .Where(server => !string.IsNullOrWhiteSpace(server.Name)
                                 || !string.IsNullOrWhiteSpace(server.Command)
                                 || !string.IsNullOrWhiteSpace(server.Url))
                .ToList(),
            ForceReconfigure = ForceReconfigure
        };
    }

    public void TogglePluginAlias(string alias)
        => PluginAliases = ToggleAlias(PluginAliases, alias);

    public void ToggleToolAlias(string alias)
        => ToolAliases = ToggleAlias(ToolAliases, alias);

    public bool HasPluginAlias(string alias)
        => SplitList(PluginAliases).Contains(alias, StringComparer.OrdinalIgnoreCase);

    public bool HasToolAlias(string alias)
        => SplitList(ToolAliases).Contains(alias, StringComparer.OrdinalIgnoreCase);

    private static string ToggleAlias(string value, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return value;
        }

        var aliases = SplitList(value);
        var existing = aliases.FirstOrDefault(item => string.Equals(item, alias, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            aliases.Remove(existing);
        }
        else
        {
            aliases.Add(alias);
        }

        return string.Join(Environment.NewLine, aliases);
    }

    private static string Required(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string? Optional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static List<string> SplitList(string value)
        => value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

}

public sealed class SurfaceKeyValueDraftRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class SurfaceMcpServerDraft
{
    public string Name { get; set; } = string.Empty;

    public McpTransportType TransportType { get; set; } = McpTransportType.Stdio;

    public string Command { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public List<SurfaceKeyValueDraftRow> Env { get; } = [new()];

    public List<SurfaceKeyValueDraftRow> Headers { get; } = [new()];

    public McpServerConfig Build()
    {
        return new McpServerConfig
        {
            Name = Optional(Name),
            TransportType = TransportType,
            Command = Optional(Command),
            Arguments = SurfaceAgentConfigurationDraft.SplitList(Arguments),
            Url = Optional(Url),
            Env = BuildDictionary(Env),
            Headers = BuildDictionary(Headers)
        };
    }

    private static Dictionary<string, string> BuildDictionary(IEnumerable<SurfaceKeyValueDraftRow> rows)
        => rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .ToDictionary(row => row.Key.Trim(), row => row.Value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string? Optional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

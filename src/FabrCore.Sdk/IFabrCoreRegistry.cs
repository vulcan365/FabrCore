namespace FabrCore.Sdk
{
    public class RegistryMethodEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class RegistryEntry
    {
        public string TypeName { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
        public string? Description { get; set; }
        public string? Capabilities { get; set; }
        public List<string> Notes { get; set; } = new();
        public List<RegistryMethodEntry> Methods { get; set; } = new();
    }

    public interface IFabrCoreRegistry
    {
        List<RegistryEntry> GetAgentTypes();
        List<RegistryEntry> GetPlugins();
        List<RegistryEntry> GetTools();
        Type? FindAgentType(string alias);
    }
}

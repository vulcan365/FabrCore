namespace FabrCore.Sdk
{
    public class RegistryEntry
    {
        public string TypeName { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
    }

    public interface IFabrCoreRegistry
    {
        List<RegistryEntry> GetAgentTypes();
        List<RegistryEntry> GetPlugins();
        List<RegistryEntry> GetTools();
        Type? FindAgentType(string alias);
    }
}


namespace FabrCore.Sdk
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class AgentAliasAttribute : Attribute
    {
        public string Alias { get; }
        public AgentAliasAttribute(string alias)
        {
            Alias = alias?.Trim() ?? string.Empty;
        }
    }
}

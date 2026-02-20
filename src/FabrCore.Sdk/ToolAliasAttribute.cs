
namespace FabrCore.Sdk
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class ToolAliasAttribute : Attribute
    {
        public string Alias { get; }
        public ToolAliasAttribute(string alias)
        {
            Alias = alias?.Trim() ?? string.Empty;
        }
    }
}

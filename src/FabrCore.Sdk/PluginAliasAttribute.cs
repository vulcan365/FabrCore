
namespace Fabr.Sdk
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class PluginAliasAttribute : Attribute
    {
        public string Alias { get; }
        public PluginAliasAttribute(string alias)
        {
            Alias = alias?.Trim() ?? string.Empty;
        }
    }
}


namespace FabrCore.Sdk
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class FabrCoreCapabilitiesAttribute : Attribute
    {
        public string Description { get; }
        public FabrCoreCapabilitiesAttribute(string description)
        {
            Description = description?.Trim() ?? string.Empty;
        }
    }
}

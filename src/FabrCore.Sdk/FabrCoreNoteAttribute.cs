
namespace FabrCore.Sdk
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class FabrCoreNoteAttribute : Attribute
    {
        public string Note { get; }
        public FabrCoreNoteAttribute(string note)
        {
            Note = note?.Trim() ?? string.Empty;
        }
    }
}

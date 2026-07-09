using Orleans;

namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Thrown when an operation is blocked by an ACL decision in
    /// <see cref="AclEnforcementMode.Enforce"/> mode. Subclasses
    /// <see cref="UnauthorizedAccessException"/> so existing catch sites and client
    /// expectations keep working.
    /// </summary>
    [GenerateSerializer]
    public class AclDeniedException : UnauthorizedAccessException
    {
        public AclDeniedException(string message) : base(message)
        {
        }
    }
}

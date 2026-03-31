namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Configuration options for the ACL system, bound from the <c>FabrCore:Acl</c> config section.
    /// </summary>
    public class FabrCoreAclOptions
    {
        /// <summary>
        /// ACL rules defining cross-owner access permissions.
        /// Rules are evaluated in order; first match wins.
        /// </summary>
        public List<AclRule> Rules { get; set; } = new();

        /// <summary>
        /// Named groups of owner identifiers used in <c>group:name</c> CallerPattern references.
        /// Key is the group name, value is the list of member owner IDs.
        /// </summary>
        public Dictionary<string, List<string>> Groups { get; set; } = new();
    }
}

namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Permissions that can be granted via ACL rules.
    /// </summary>
    [Flags]
    public enum AclPermission
    {
        None = 0,

        /// <summary>Can send messages to the agent.</summary>
        Message = 1,

        /// <summary>Can create or reconfigure the agent.</summary>
        Configure = 2,

        /// <summary>Can read threads, state, and health.</summary>
        Read = 4,

        /// <summary>Can modify ACL rules for this agent's owner.</summary>
        Admin = 8,

        All = Message | Configure | Read | Admin
    }
}

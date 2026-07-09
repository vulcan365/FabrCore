namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Well-known FabrCore action stems checked by built-in enforcement points.
    /// Consuming applications may define their own <see cref="AclAction"/>s in
    /// non-reserved entity space and evaluate them through <c>IAclEvaluator</c>.
    /// </summary>
    public static class FabrActions
    {
        /// <summary>Send a message or event to an agent (was <c>AclPermission.Message</c>).</summary>
        public static readonly AclAction AgentMessage = new("agent", "message");

        /// <summary>Create an agent under a target principal (was part of <c>AclPermission.Configure</c>).</summary>
        public static readonly AclAction AgentCreate = new("agent", "create");

        /// <summary>Reset or reconfigure an existing agent (was part of <c>AclPermission.Configure</c>).</summary>
        public static readonly AclAction AgentReconfigure = new("agent", "reconfigure");

        /// <summary>Untrack or evict an agent (was part of <c>AclPermission.Configure</c>).</summary>
        public static readonly AclAction AgentDestroy = new("agent", "destroy");

        /// <summary>Read an agent's threads, state, health, and monitor data (was <c>AclPermission.Read</c>).</summary>
        public static readonly AclAction AgentRead = new("agent", "read");

        /// <summary>Manage ACL entities: principals, roles, groups, grants (was <c>AclPermission.Admin</c>).</summary>
        public static readonly AclAction AclManage = new("acl", "manage");

        /// <summary>Read ACL entities and run evaluate/check queries (addon query surface).</summary>
        public static readonly AclAction AclRead = new("acl", "read");
    }

    /// <summary>
    /// String constants for the built-in permission names, for use in configuration,
    /// persistence, and the management API.
    /// </summary>
    public static class FabrPermissions
    {
        public const string AgentMessageAllow = "agent.message.allow";
        public const string AgentMessageDeny = "agent.message.deny";
        public const string AgentCreateAllow = "agent.create.allow";
        public const string AgentCreateDeny = "agent.create.deny";
        public const string AgentReconfigureAllow = "agent.reconfigure.allow";
        public const string AgentReconfigureDeny = "agent.reconfigure.deny";
        public const string AgentDestroyAllow = "agent.destroy.allow";
        public const string AgentDestroyDeny = "agent.destroy.deny";
        public const string AgentReadAllow = "agent.read.allow";
        public const string AgentReadDeny = "agent.read.deny";
        public const string AclManageAllow = "acl.manage.allow";
        public const string AclManageDeny = "acl.manage.deny";
        public const string AclReadAllow = "acl.read.allow";
        public const string AclReadDeny = "acl.read.deny";
    }
}

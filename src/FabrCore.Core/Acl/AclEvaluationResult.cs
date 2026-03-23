namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Result of an ACL evaluation.
    /// </summary>
    public record AclEvaluationResult(
        bool Allowed,
        AclPermission GrantedPermissions,
        string? DeniedReason = null);
}

using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;

namespace FabrCore.Sdk
{
    /// <summary>
    /// Response from the audit config endpoint.
    /// </summary>
    public class AuditConfigResponse
    {
        public string ProviderType { get; set; } = string.Empty;

        /// <summary>False when the host runs the null audit provider (recording disabled).</summary>
        public bool RecordingAvailable { get; set; }

        public string DefaultLevel { get; set; } = string.Empty;
        public Dictionary<string, string> Categories { get; set; } = new();
        public int MaxBufferedEvents { get; set; }
    }

    /// <summary>
    /// ACL management and security audit surface of the Host API client. The
    /// <paramref name="callerUserHandle"/> on every method is the identity sent as the
    /// <c>x-user-handle</c> header; mutations require <c>acl.manage.allow</c>, reads require
    /// <c>acl.read.allow</c> (the System principal bypasses both).
    /// </summary>
    public partial interface IFabrCoreHostApiClient
    {
        // ── ACL principals ──

        Task<List<AclPrincipal>> GetAclPrincipalsAsync(string callerUserHandle, CancellationToken cancellationToken = default);
        Task<AclPrincipal?> GetAclPrincipalAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default);
        Task UpsertAclPrincipalAsync(string callerUserHandle, AclPrincipal principal, CancellationToken cancellationToken = default);
        Task<bool> DeleteAclPrincipalAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default);

        // ── ACL roles ──

        Task<List<AclRole>> GetAclRolesAsync(string callerUserHandle, CancellationToken cancellationToken = default);
        Task<AclRole?> GetAclRoleAsync(string callerUserHandle, string roleName, CancellationToken cancellationToken = default);
        Task UpsertAclRoleAsync(string callerUserHandle, AclRole role, CancellationToken cancellationToken = default);
        Task<bool> DeleteAclRoleAsync(string callerUserHandle, string roleName, CancellationToken cancellationToken = default);

        // ── ACL groups ──

        Task<List<AclGroup>> GetAclGroupsAsync(string callerUserHandle, CancellationToken cancellationToken = default);
        Task<AclGroup?> GetAclGroupAsync(string callerUserHandle, string groupName, CancellationToken cancellationToken = default);
        Task UpsertAclGroupAsync(string callerUserHandle, AclGroup group, CancellationToken cancellationToken = default);
        Task<bool> DeleteAclGroupAsync(string callerUserHandle, string groupName, CancellationToken cancellationToken = default);
        Task AddAclGroupMemberAsync(string callerUserHandle, string groupName, GroupMember member, CancellationToken cancellationToken = default);
        Task<bool> RemoveAclGroupMemberAsync(string callerUserHandle, string groupName, GroupMember member, CancellationToken cancellationToken = default);

        // ── ACL grants ──

        Task<List<PermissionGrant>> GetAclGrantsAsync(string callerUserHandle, CancellationToken cancellationToken = default);
        Task<PermissionGrant?> GetAclGrantAsync(string callerUserHandle, string grantId, CancellationToken cancellationToken = default);
        Task UpsertAclGrantAsync(string callerUserHandle, PermissionGrant grant, CancellationToken cancellationToken = default);
        Task<bool> DeleteAclGrantAsync(string callerUserHandle, string grantId, CancellationToken cancellationToken = default);

        // ── Effective memberships and permission queries (addon surface) ──

        /// <summary>Gets a principal's effective roles: direct, via groups, and via dynamic groups.</summary>
        Task<List<string>> GetPrincipalRolesAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default);

        /// <summary>Gets a principal's group memberships, including dynamic groups.</summary>
        Task<List<string>> GetPrincipalGroupsAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default);

        /// <summary>Whether the principal's effective roles include <paramref name="roleName"/>.</summary>
        Task<bool> IsPrincipalInRoleAsync(string callerUserHandle, string principalHandle, string roleName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks whether a principal holds an application-defined (or built-in) permission,
        /// e.g. <c>CheckPermissionAsync("surface-svc", "alice", "surface.adminview")</c>.
        /// <paramref name="action"/> is the effect-less <c>entity.behavior</c> stem.
        /// </summary>
        Task<AclEvaluationResponse> CheckPermissionAsync(string callerUserHandle, string principalHandle, string action, string resource = "*:*", CancellationToken cancellationToken = default);

        /// <summary>Dry-run evaluation with full decision detail (matched grant, outcome, mode).</summary>
        Task<AclEvaluationResponse> EvaluateAclAsync(string callerUserHandle, AclEvaluationRequest request, CancellationToken cancellationToken = default);

        // ── ACL configuration ──

        Task<AclConfigResponse> GetAclConfigAsync(string callerUserHandle, CancellationToken cancellationToken = default);

        /// <summary>Sets the runtime enforcement mode ("Disabled" | "AuditOnly" | "Enforce") or clears the override with null.</summary>
        Task SetAclEnforcementModeAsync(string callerUserHandle, string? mode, CancellationToken cancellationToken = default);

        // ── Security audit ──

        Task<List<AuditEvent>> GetAuditEventsAsync(
            string callerUserHandle,
            string? category = null,
            string? outcome = null,
            string? subjectPrincipal = null,
            DateTimeOffset? since = null,
            int? limit = null,
            CancellationToken cancellationToken = default);

        Task<AuditConfigResponse> GetAuditConfigAsync(string callerUserHandle, CancellationToken cancellationToken = default);
    }

    public partial class FabrCoreHostApiClient
    {
        // ── ACL principals ──

        public Task<List<AclPrincipal>> GetAclPrincipalsAsync(string callerUserHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<List<AclPrincipal>>(callerUserHandle, "acl/principals", "GetAclPrincipals", cancellationToken);

        public Task<AclPrincipal?> GetAclPrincipalAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default)
            => AclGetAsync<AclPrincipal>(callerUserHandle, $"acl/principals/{Uri.EscapeDataString(principalHandle)}", "GetAclPrincipal", cancellationToken);

        public Task UpsertAclPrincipalAsync(string callerUserHandle, AclPrincipal principal, CancellationToken cancellationToken = default)
            => AclPutAsync(callerUserHandle, $"acl/principals/{Uri.EscapeDataString(principal.Handle)}", principal, "UpsertAclPrincipal", cancellationToken);

        public Task<bool> DeleteAclPrincipalAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default)
            => AclDeleteAsync(callerUserHandle, $"acl/principals/{Uri.EscapeDataString(principalHandle)}", "DeleteAclPrincipal", cancellationToken);

        // ── ACL roles ──

        public Task<List<AclRole>> GetAclRolesAsync(string callerUserHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<List<AclRole>>(callerUserHandle, "acl/roles", "GetAclRoles", cancellationToken);

        public Task<AclRole?> GetAclRoleAsync(string callerUserHandle, string roleName, CancellationToken cancellationToken = default)
            => AclGetAsync<AclRole>(callerUserHandle, $"acl/roles/{Uri.EscapeDataString(roleName)}", "GetAclRole", cancellationToken);

        public Task UpsertAclRoleAsync(string callerUserHandle, AclRole role, CancellationToken cancellationToken = default)
            => AclPutAsync(callerUserHandle, $"acl/roles/{Uri.EscapeDataString(role.Name)}", role, "UpsertAclRole", cancellationToken);

        public Task<bool> DeleteAclRoleAsync(string callerUserHandle, string roleName, CancellationToken cancellationToken = default)
            => AclDeleteAsync(callerUserHandle, $"acl/roles/{Uri.EscapeDataString(roleName)}", "DeleteAclRole", cancellationToken);

        // ── ACL groups ──

        public Task<List<AclGroup>> GetAclGroupsAsync(string callerUserHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<List<AclGroup>>(callerUserHandle, "acl/groups", "GetAclGroups", cancellationToken);

        public Task<AclGroup?> GetAclGroupAsync(string callerUserHandle, string groupName, CancellationToken cancellationToken = default)
            => AclGetAsync<AclGroup>(callerUserHandle, $"acl/groups/{Uri.EscapeDataString(groupName)}", "GetAclGroup", cancellationToken);

        public Task UpsertAclGroupAsync(string callerUserHandle, AclGroup group, CancellationToken cancellationToken = default)
            => AclPutAsync(callerUserHandle, $"acl/groups/{Uri.EscapeDataString(group.Name)}", group, "UpsertAclGroup", cancellationToken);

        public Task<bool> DeleteAclGroupAsync(string callerUserHandle, string groupName, CancellationToken cancellationToken = default)
            => AclDeleteAsync(callerUserHandle, $"acl/groups/{Uri.EscapeDataString(groupName)}", "DeleteAclGroup", cancellationToken);

        public Task AddAclGroupMemberAsync(string callerUserHandle, string groupName, GroupMember member, CancellationToken cancellationToken = default)
            => AclPostAsync<GroupMember, object?>(callerUserHandle, $"acl/groups/{Uri.EscapeDataString(groupName)}/members", member, "AddAclGroupMember", cancellationToken, expectBody: false);

        public Task<bool> RemoveAclGroupMemberAsync(string callerUserHandle, string groupName, GroupMember member, CancellationToken cancellationToken = default)
            => AclDeleteAsync(
                callerUserHandle,
                $"acl/groups/{Uri.EscapeDataString(groupName)}/members?kind={member.Kind}&handle={Uri.EscapeDataString(member.Handle)}",
                "RemoveAclGroupMember",
                cancellationToken);

        // ── ACL grants ──

        public Task<List<PermissionGrant>> GetAclGrantsAsync(string callerUserHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<List<PermissionGrant>>(callerUserHandle, "acl/grants", "GetAclGrants", cancellationToken);

        public Task<PermissionGrant?> GetAclGrantAsync(string callerUserHandle, string grantId, CancellationToken cancellationToken = default)
            => AclGetAsync<PermissionGrant>(callerUserHandle, $"acl/grants/{Uri.EscapeDataString(grantId)}", "GetAclGrant", cancellationToken);

        public Task UpsertAclGrantAsync(string callerUserHandle, PermissionGrant grant, CancellationToken cancellationToken = default)
            => AclPutAsync(callerUserHandle, $"acl/grants/{Uri.EscapeDataString(grant.Id)}", grant, "UpsertAclGrant", cancellationToken);

        public Task<bool> DeleteAclGrantAsync(string callerUserHandle, string grantId, CancellationToken cancellationToken = default)
            => AclDeleteAsync(callerUserHandle, $"acl/grants/{Uri.EscapeDataString(grantId)}", "DeleteAclGrant", cancellationToken);

        // ── Effective memberships and permission queries ──

        public Task<List<string>> GetPrincipalRolesAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<List<string>>(callerUserHandle, $"acl/principals/{Uri.EscapeDataString(principalHandle)}/roles", "GetPrincipalRoles", cancellationToken);

        public Task<List<string>> GetPrincipalGroupsAsync(string callerUserHandle, string principalHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<List<string>>(callerUserHandle, $"acl/principals/{Uri.EscapeDataString(principalHandle)}/groups", "GetPrincipalGroups", cancellationToken);

        public async Task<bool> IsPrincipalInRoleAsync(string callerUserHandle, string principalHandle, string roleName, CancellationToken cancellationToken = default)
        {
            var roles = await GetPrincipalRolesAsync(callerUserHandle, principalHandle, cancellationToken);
            return roles.Contains(roleName, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<AclEvaluationResponse> CheckPermissionAsync(string callerUserHandle, string principalHandle, string action, string resource = "*:*", CancellationToken cancellationToken = default)
            => await AclPostAsync<AclCheckRequest, AclEvaluationResponse>(
                    callerUserHandle,
                    "acl/check",
                    new AclCheckRequest { Principal = principalHandle, Action = action, Resource = resource },
                    "CheckPermission",
                    cancellationToken)
                ?? throw new HttpRequestException("CheckPermission returned no content.");

        public async Task<AclEvaluationResponse> EvaluateAclAsync(string callerUserHandle, AclEvaluationRequest request, CancellationToken cancellationToken = default)
            => await AclPostAsync<AclEvaluationRequest, AclEvaluationResponse>(callerUserHandle, "acl/evaluate", request, "EvaluateAcl", cancellationToken)
                ?? throw new HttpRequestException("EvaluateAcl returned no content.");

        // ── ACL configuration ──

        public Task<AclConfigResponse> GetAclConfigAsync(string callerUserHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<AclConfigResponse>(callerUserHandle, "acl/config", "GetAclConfig", cancellationToken);

        public Task SetAclEnforcementModeAsync(string callerUserHandle, string? mode, CancellationToken cancellationToken = default)
            => AclPutAsync(callerUserHandle, "acl/config/enforcement-mode", new SetEnforcementModeRequest { Mode = mode }, "SetAclEnforcementMode", cancellationToken);

        // ── Security audit ──

        public Task<List<AuditEvent>> GetAuditEventsAsync(
            string callerUserHandle,
            string? category = null,
            string? outcome = null,
            string? subjectPrincipal = null,
            DateTimeOffset? since = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(category)) query.Add($"category={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrWhiteSpace(outcome)) query.Add($"outcome={Uri.EscapeDataString(outcome)}");
            if (!string.IsNullOrWhiteSpace(subjectPrincipal)) query.Add($"subject={Uri.EscapeDataString(subjectPrincipal)}");
            if (since.HasValue) query.Add($"since={Uri.EscapeDataString(since.Value.ToString("O"))}");
            if (limit.HasValue) query.Add($"limit={limit.Value}");

            var path = "audit/events" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
            return AclGetRequiredAsync<List<AuditEvent>>(callerUserHandle, path, "GetAuditEvents", cancellationToken);
        }

        public Task<AuditConfigResponse> GetAuditConfigAsync(string callerUserHandle, CancellationToken cancellationToken = default)
            => AclGetRequiredAsync<AuditConfigResponse>(callerUserHandle, "audit/config", "GetAuditConfig", cancellationToken);

        // ── Shared HTTP helpers ──

        private async Task<T?> AclGetAsync<T>(string callerUserHandle, string path, string operation, CancellationToken cancellationToken)
        {
            ValidateCaller(callerUserHandle);

            using var activity = ActivitySource.StartActivity(operation, ActivityKind.Client);
            activity?.SetTag("acl.caller", callerUserHandle);
            activity?.SetTag("acl.path", path);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/fabrcoreapi/{path}");
                request.Headers.Add("x-user-handle", callerUserHandle);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RecordSuccess(activity, startTime, operation);
                    return default;
                }

                await EnsureAclSuccessAsync(response, operation, cancellationToken);

                var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
                RecordSuccess(activity, startTime, operation);
                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, operation, ex);
                _logger.LogError(ex, "{Operation} failed for caller {Caller}", operation, callerUserHandle);
                throw;
            }
        }

        private async Task<T> AclGetRequiredAsync<T>(string callerUserHandle, string path, string operation, CancellationToken cancellationToken)
            => await AclGetAsync<T>(callerUserHandle, path, operation, cancellationToken)
                ?? throw new HttpRequestException($"{operation} returned no content.");

        private async Task AclPutAsync<T>(string callerUserHandle, string path, T body, string operation, CancellationToken cancellationToken)
        {
            ValidateCaller(callerUserHandle);

            using var activity = ActivitySource.StartActivity(operation, ActivityKind.Client);
            activity?.SetTag("acl.caller", callerUserHandle);
            activity?.SetTag("acl.path", path);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/fabrcoreapi/{path}");
                request.Headers.Add("x-user-handle", callerUserHandle);
                request.Content = JsonContent.Create(body, options: JsonOptions);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                await EnsureAclSuccessAsync(response, operation, cancellationToken);
                RecordSuccess(activity, startTime, operation);
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, operation, ex);
                _logger.LogError(ex, "{Operation} failed for caller {Caller}", operation, callerUserHandle);
                throw;
            }
        }

        private async Task<bool> AclDeleteAsync(string callerUserHandle, string path, string operation, CancellationToken cancellationToken)
        {
            ValidateCaller(callerUserHandle);

            using var activity = ActivitySource.StartActivity(operation, ActivityKind.Client);
            activity?.SetTag("acl.caller", callerUserHandle);
            activity?.SetTag("acl.path", path);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/fabrcoreapi/{path}");
                request.Headers.Add("x-user-handle", callerUserHandle);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RecordSuccess(activity, startTime, operation);
                    return false;
                }

                await EnsureAclSuccessAsync(response, operation, cancellationToken);
                RecordSuccess(activity, startTime, operation);
                return true;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, operation, ex);
                _logger.LogError(ex, "{Operation} failed for caller {Caller}", operation, callerUserHandle);
                throw;
            }
        }

        private async Task<TResponse?> AclPostAsync<TRequest, TResponse>(
            string callerUserHandle, string path, TRequest body, string operation, CancellationToken cancellationToken, bool expectBody = true)
        {
            ValidateCaller(callerUserHandle);

            using var activity = ActivitySource.StartActivity(operation, ActivityKind.Client);
            activity?.SetTag("acl.caller", callerUserHandle);
            activity?.SetTag("acl.path", path);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/fabrcoreapi/{path}");
                request.Headers.Add("x-user-handle", callerUserHandle);
                request.Content = JsonContent.Create(body, options: JsonOptions);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                await EnsureAclSuccessAsync(response, operation, cancellationToken);

                TResponse? result = default;
                if (expectBody)
                    result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);

                RecordSuccess(activity, startTime, operation);
                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, operation, ex);
                _logger.LogError(ex, "{Operation} failed for caller {Caller}", operation, callerUserHandle);
                throw;
            }
        }

        private static async Task EnsureAclSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"{operation} failed. Status: {(int)response.StatusCode} {response.ReasonPhrase}. Response: {responseBody}",
                null,
                response.StatusCode);
        }

        private static void ValidateCaller(string callerUserHandle)
        {
            if (string.IsNullOrWhiteSpace(callerUserHandle))
                throw new ArgumentException("Caller user handle cannot be null or empty.", nameof(callerUserHandle));
        }
    }
}

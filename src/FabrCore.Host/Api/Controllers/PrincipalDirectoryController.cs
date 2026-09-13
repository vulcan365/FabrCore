using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Core.Acl;
using FabrCore.Core.CloudServer;
using FabrCore.Host.Security;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Api.Controllers;

[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/principals")]
public sealed class PrincipalDirectoryController(IFabrCoreAgentService agents, IServiceProvider services) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int offset = 0, int limit = 100, string? revision = null, CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, PrincipalSummary>(StringComparer.OrdinalIgnoreCase);
        if (services.GetService<IAclEntityStore>() is { } registry)
            foreach (var principal in (await registry.GetSnapshotAsync(cancellationToken)).Principals)
                entries[principal.Handle] = new() { Handle = principal.Handle, DisplayName = principal.DisplayName,
                    Description = principal.Description, IsSystem = principal.IsSystem, Registered = true };
        foreach (var runtime in await agents.GetPrincipalsAsync())
        {
            var handle = string.IsNullOrEmpty(runtime.Handle) ? runtime.Key : runtime.Handle;
            if (!entries.TryGetValue(handle, out var entry)) entries[handle] = entry = new() { Handle = handle };
            entry.RuntimeDiscovered = true; entry.RuntimeStatus = runtime.Status;
        }
        var all = entries.Values.OrderBy(p => p.Handle, StringComparer.OrdinalIgnoreCase).ToList();
        var current = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(all)));
        if (revision is not null && revision != current) return StatusCode(412, new { error = "Principal directory changed; restart pagination." });
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 1000);
        Response.Headers.ETag = $"\"{current}\"";
        return Ok(new AdministrationPage<PrincipalSummary> { Items = all.Skip(offset).Take(limit).ToList(), Revision = current,
            NextCursor = offset + limit < all.Count ? (offset + limit).ToString() : null });
    }
}

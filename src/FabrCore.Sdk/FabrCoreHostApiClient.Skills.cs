using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FabrCore.Core.Skills;

namespace FabrCore.Sdk;

public partial interface IFabrCoreHostApiClient
{
    Task<List<FabrCoreSkillCatalogEntry>> ListHarnessSkillsAsync(
        string principalId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host API client does not implement Harness Skill administration.");

    Task<FabrCoreSkillManifest?> GetHarnessSkillAsync(
        string principalId,
        string name,
        string version,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host API client does not implement Harness Skill administration.");

    Task<FabrCoreSkillPublishResult> PublishHarnessSkillAsync(
        string principalId,
        string name,
        string version,
        Stream zipStream,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host API client does not implement Harness Skill administration.");

    Task<bool> DeleteHarnessSkillAsync(
        string principalId,
        string name,
        string version,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host API client does not implement Harness Skill administration.");
}

public partial class FabrCoreHostApiClient
{
    public async Task<List<FabrCoreSkillCatalogEntry>> ListHarnessSkillsAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ValidateSkillAddress(principalId);
        using var response = await _httpClient.GetAsync(SkillsUrl(principalId), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<FabrCoreSkillCatalogEntry>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<FabrCoreSkillManifest?> GetHarnessSkillAsync(
        string principalId,
        string name,
        string version,
        CancellationToken cancellationToken = default)
    {
        ValidateSkillAddress(principalId, name, version);
        using var response = await _httpClient.GetAsync(SkillVersionUrl(principalId, name, version), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FabrCoreSkillManifest>(JsonOptions, cancellationToken);
    }

    public async Task<FabrCoreSkillPublishResult> PublishHarnessSkillAsync(
        string principalId,
        string name,
        string version,
        Stream zipStream,
        CancellationToken cancellationToken = default)
    {
        ValidateSkillAddress(principalId, name, version);
        ArgumentNullException.ThrowIfNull(zipStream);

        using var request = new HttpRequestMessage(HttpMethod.Put, SkillVersionUrl(principalId, name, version));
        request.Content = new StreamContent(zipStream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FabrCoreSkillPublishResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Skill publish response was empty.");
    }

    public async Task<bool> DeleteHarnessSkillAsync(
        string principalId,
        string name,
        string version,
        CancellationToken cancellationToken = default)
    {
        ValidateSkillAddress(principalId, name, version);
        using var response = await _httpClient.DeleteAsync(SkillVersionUrl(principalId, name, version), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private string SkillsUrl(string principalId) =>
        $"{_baseUrl}/fabrcoreapi/admin/v1/principals/{Uri.EscapeDataString(principalId)}/skills";

    private string SkillVersionUrl(string principalId, string name, string version) =>
        $"{SkillsUrl(principalId)}/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}";

    private static void ValidateSkillAddress(string principalId, string? name = null, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        if (name is null && version is null)
        {
            return;
        }

        if (!FabrCoreSkillReference.IsValidSkillName(name))
        {
            throw new ArgumentException("Skill name is invalid.", nameof(name));
        }

        if (!FabrCoreSkillStorage.TryValidateVersion(version, out var reason))
        {
            throw new ArgumentException(reason, nameof(version));
        }
    }
}

#pragma warning disable MAAI001 // Agent Skills APIs are experimental upstream.
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core.Skills;
using Microsoft.Agents.AI;

namespace FabrCore.Sdk;

/// <summary>
/// Resolves immutable, principal-scoped FabrCore skill manifests from typed Host storage.
/// </summary>
public sealed class FabrCoreStoredAgentSkillsSource : AgentSkillsSource
{
    private readonly IPrincipalScopedFabrCoreStorageProvider storage;
    private readonly string principalId;
    private readonly IReadOnlyList<FabrCoreSkillReference> references;
    private IReadOnlyList<AgentSkill>? skills;

    public FabrCoreStoredAgentSkillsSource(
        IPrincipalScopedFabrCoreStorageProvider storage,
        string principalId,
        IEnumerable<FabrCoreSkillReference> references)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        this.principalId = principalId;
        this.references = references?.DistinctBy(reference => reference.ToString(), StringComparer.Ordinal).ToList()
            ?? throw new ArgumentNullException(nameof(references));
    }

    /// <summary>Loads and validates every pinned manifest so configuration errors fail agent initialization.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (skills is not null)
        {
            return;
        }

        var loaded = new List<AgentSkill>(references.Count);
        var errors = new List<string>();
        foreach (var reference in references)
        {
            try
            {
                var manifest = await storage.GetAsync<FabrCoreSkillManifest>(
                    principalId,
                    FabrCoreSkillStorage.Container,
                    FabrCoreSkillStorage.ManifestKey(reference.Name, reference.Version),
                    cancellationToken);

                if (manifest is null)
                {
                    errors.Add($"{reference}: not found");
                    continue;
                }

                ValidateManifest(reference, manifest);
                loaded.Add(new StoredAgentSkill(storage, principalId, manifest));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{reference}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Harness skill configuration could not be loaded: " + string.Join("; ", errors));
        }

        skills = loaded;
    }

    public override Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context,
        CancellationToken cancellationToken = default)
    {
        if (skills is null)
        {
            throw new InvalidOperationException(
                $"{nameof(FabrCoreStoredAgentSkillsSource)} must be initialized before it is used.");
        }

        return Task.FromResult<IList<AgentSkill>>([.. skills]);
    }

    private static void ValidateManifest(FabrCoreSkillReference reference, FabrCoreSkillManifest manifest)
    {
        if (manifest.SchemaVersion != FabrCoreSkillStorage.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"unsupported schema version {manifest.SchemaVersion}");
        }

        if (!string.Equals(manifest.Name, reference.Name, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, reference.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("stored manifest identity does not match its reference");
        }

        _ = new AgentSkillFrontmatter(manifest.Name, manifest.Description, manifest.Compatibility);

        var skillMarkdownBytes = Encoding.UTF8.GetByteCount(manifest.SkillMarkdown);
        if (skillMarkdownBytes > FabrCoreSkillStorage.MaxSkillMarkdownBytes)
        {
            throw new InvalidDataException("SKILL.md exceeds the supported size limit");
        }

        if (!IsSha256(manifest.DigestSha256))
        {
            throw new InvalidDataException("package digest is invalid");
        }

        if (manifest.Resources is null ||
            manifest.Resources.Count > FabrCoreSkillStorage.MaxEntries - 1 ||
            manifest.Resources.Select(resource => resource.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Resources.Count ||
            manifest.Resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != manifest.Resources.Count)
        {
            throw new InvalidDataException("resource manifest is invalid or contains duplicates");
        }

        long totalBytes = skillMarkdownBytes;
        foreach (var resource in manifest.Resources)
        {
            var normalizedName = resource.Name.Normalize(NormalizationForm.FormC);
            var segments = normalizedName.Split('/');
            var expectedResourceId = Sha256(Encoding.UTF8.GetBytes(normalizedName));
            if (!string.Equals(resource.Name, normalizedName, StringComparison.Ordinal) ||
                normalizedName.Length == 0 ||
                normalizedName.Length > FabrCoreSkillStorage.MaxResourcePathLength ||
                normalizedName.StartsWith('/') ||
                normalizedName.Contains('\\') ||
                segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..") ||
                segments.Length - 1 > FabrCoreSkillStorage.MaxResourceDepth ||
                resource.Length is < 0 or > FabrCoreSkillStorage.MaxResourceBytes ||
                !string.Equals(resource.ResourceId, expectedResourceId, StringComparison.Ordinal) ||
                !IsSha256(resource.DigestSha256))
            {
                throw new InvalidDataException($"resource descriptor '{resource.Name}' is invalid");
            }

            totalBytes += resource.Length;
        }

        if (totalBytes != manifest.TotalUncompressedBytes || totalBytes > FabrCoreSkillStorage.MaxPackageBytes ||
            JsonSerializer.SerializeToUtf8Bytes(manifest).Length > FabrCoreSkillStorage.MaxSerializedEntityBytes)
        {
            throw new InvalidDataException("manifest size metadata is invalid");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class StoredAgentSkill : AgentSkill
    {
        private readonly IReadOnlyDictionary<string, StoredResource> resources;
        private readonly string content;

        public StoredAgentSkill(
            IPrincipalScopedFabrCoreStorageProvider storage,
            string principalId,
            FabrCoreSkillManifest manifest)
        {
            Frontmatter = new AgentSkillFrontmatter(manifest.Name, manifest.Description, manifest.Compatibility)
            {
                License = manifest.License,
                AllowedTools = manifest.AllowedTools,
                Metadata = manifest.Metadata is null
                    ? null
                    : new Microsoft.Extensions.AI.AdditionalPropertiesDictionary(
                        manifest.Metadata.ToDictionary(pair => pair.Key, pair => (object?)pair.Value))
            };

            resources = manifest.Resources.ToDictionary(
                descriptor => descriptor.Name,
                descriptor => new StoredResource(storage, principalId, manifest.Name, manifest.Version, descriptor),
                StringComparer.Ordinal);

            var resourceList = resources.Count == 0
                ? "<available_resources />"
                : "<available_resources>\n" + string.Join("\n", resources.Keys.Order(StringComparer.Ordinal)
                    .Select(name => $"  <resource name=\"{SecurityElement.Escape(name)}\" />")) + "\n</available_resources>";

            content = $"{manifest.SkillMarkdown.TrimEnd()}\n{resourceList}\n<available_scripts />";
        }

        public override AgentSkillFrontmatter Frontmatter { get; }

        public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(content);

        public override ValueTask<AgentSkillResource?> GetResourceAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentSkillResource?>(resources.GetValueOrDefault(name));
    }

    private sealed class StoredResource : AgentSkillResource
    {
        private readonly IPrincipalScopedFabrCoreStorageProvider storage;
        private readonly string principalId;
        private readonly string skillName;
        private readonly string version;
        private readonly FabrCoreSkillResourceDescriptor descriptor;
        private readonly SemaphoreSlim gate = new(1, 1);
        private string? cachedContent;

        public StoredResource(
            IPrincipalScopedFabrCoreStorageProvider storage,
            string principalId,
            string skillName,
            string version,
            FabrCoreSkillResourceDescriptor descriptor)
            : base(descriptor.Name)
        {
            this.storage = storage;
            this.principalId = principalId;
            this.skillName = skillName;
            this.version = version;
            this.descriptor = descriptor;
        }

        public override async Task<object?> ReadAsync(
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            if (cachedContent is not null)
            {
                return cachedContent;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (cachedContent is not null)
                {
                    return cachedContent;
                }

                var document = await storage.GetAsync<FabrCoreSkillResourceDocument>(
                    principalId,
                    FabrCoreSkillStorage.Container,
                    FabrCoreSkillStorage.ResourceKey(skillName, version, descriptor.ResourceId),
                    cancellationToken)
                    ?? throw new InvalidDataException(
                        $"Skill resource '{descriptor.Name}' is missing from {skillName}@{version}.");

                var bytes = Encoding.UTF8.GetBytes(document.Content);
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (document.SchemaVersion != FabrCoreSkillStorage.CurrentSchemaVersion ||
                    !string.Equals(document.Name, descriptor.Name, StringComparison.Ordinal) ||
                    !string.Equals(document.MediaType, descriptor.MediaType, StringComparison.Ordinal) ||
                    !string.Equals(document.DigestSha256, descriptor.DigestSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(digest, descriptor.DigestSha256, StringComparison.OrdinalIgnoreCase) ||
                    bytes.LongLength != descriptor.Length ||
                    JsonSerializer.SerializeToUtf8Bytes(document).Length > FabrCoreSkillStorage.MaxSerializedEntityBytes)
                {
                    throw new InvalidDataException(
                        $"Skill resource '{descriptor.Name}' failed integrity validation.");
                }

                cachedContent = document.Content;
                return cachedContent;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

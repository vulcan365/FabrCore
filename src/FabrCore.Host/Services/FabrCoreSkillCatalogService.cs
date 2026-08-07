using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Skills;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Host.Services;

public interface IFabrCoreSkillCatalogService
{
    Task<IReadOnlyList<FabrCoreSkillCatalogEntry>> ListAsync(string principalId, CancellationToken cancellationToken = default);
    Task<FabrCoreSkillManifest?> GetAsync(string principalId, string name, string version, CancellationToken cancellationToken = default);
    Task<FabrCoreSkillPublishResult> PublishAsync(string principalId, string name, string version, Stream zipStream, CancellationToken cancellationToken = default);
    Task<FabrCoreSkillManifest?> DeleteAsync(string principalId, string name, string version, CancellationToken cancellationToken = default);
}

public sealed class FabrCoreSkillValidationException(string message) : Exception(message);
public sealed class FabrCoreSkillConflictException(string message) : Exception(message);

internal sealed class FabrCoreSkillCatalogService(
    IPrincipalScopedFabrCoreStorageProvider storage,
    IGrainFactory grains,
    ILogger<FabrCoreSkillCatalogService> logger) : IFabrCoreSkillCatalogService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AllowedResourceExtensions =
        [".md", ".json", ".yaml", ".yml", ".csv", ".xml", ".txt"];
    private static readonly HashSet<string> ExecutableExtensions =
        [".py", ".js", ".sh", ".ps1", ".cs", ".csx", ".exe", ".dll", ".bat", ".cmd", ".com"];

    public async Task<IReadOnlyList<FabrCoreSkillCatalogEntry>> ListAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principalId);
        cancellationToken.ThrowIfCancellationRequested();
        return await Catalog(principalId).ListAsync();
    }

    public Task<FabrCoreSkillManifest?> GetAsync(
        string principalId,
        string name,
        string version,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(principalId, name, version);
        return storage.GetAsync<FabrCoreSkillManifest>(
            principalId,
            FabrCoreSkillStorage.Container,
            FabrCoreSkillStorage.ManifestKey(name, version),
            cancellationToken);
    }

    public async Task<FabrCoreSkillPublishResult> PublishAsync(
        string principalId,
        string name,
        string version,
        Stream zipStream,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(principalId, name, version);
        ArgumentNullException.ThrowIfNull(zipStream);

        var package = await ReadPackageAsync(name, version, zipStream, cancellationToken);
        var manifestKey = FabrCoreSkillStorage.ManifestKey(name, version);
        var existing = await storage.GetAsync<FabrCoreSkillManifest>(
            principalId, FabrCoreSkillStorage.Container, manifestKey, cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(existing.DigestSha256, package.Manifest.DigestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new FabrCoreSkillConflictException(
                    $"Skill '{name}@{version}' already exists with different content.");
            }

            await Catalog(principalId).UpsertAsync(existing.ToCatalogEntry());
            return new FabrCoreSkillPublishResult { Manifest = existing, AlreadyExisted = true };
        }

        // Resource writes are intentionally first. The manifest is the commit marker, so an interrupted
        // publication can leave only unreachable resource entities and never a partially visible skill.
        foreach (var resource in package.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await storage.UpsertAsync(
                principalId,
                FabrCoreSkillStorage.Container,
                FabrCoreSkillStorage.ResourceKey(name, version, resource.Descriptor.ResourceId),
                resource.Document,
                cancellationToken);
        }

        await storage.UpsertAsync(
            principalId,
            FabrCoreSkillStorage.Container,
            manifestKey,
            package.Manifest,
            cancellationToken);
        await Catalog(principalId).UpsertAsync(package.Manifest.ToCatalogEntry());

        logger.LogInformation(
            "Published harness skill {SkillReference} for principal {Principal}; digest {Digest}, resources {ResourceCount}.",
            $"{name}@{version}", principalId, package.Manifest.DigestSha256, package.Manifest.Resources.Count);

        return new FabrCoreSkillPublishResult { Manifest = package.Manifest };
    }

    public async Task<FabrCoreSkillManifest?> DeleteAsync(
        string principalId,
        string name,
        string version,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(principalId, name, version);
        var manifest = await GetAsync(principalId, name, version, cancellationToken);
        if (manifest is null)
        {
            // A prior delete may have committed the manifest removal and stopped before updating the
            // catalog. Retrying still repairs that stale visibility marker.
            await Catalog(principalId).RemoveAsync(name, version);
            return null;
        }

        await storage.DeleteAsync(
            principalId,
            FabrCoreSkillStorage.Container,
            FabrCoreSkillStorage.ManifestKey(name, version),
            cancellationToken);
        await Catalog(principalId).RemoveAsync(name, version);

        foreach (var resource in manifest.Resources)
        {
            try
            {
                await storage.DeleteAsync(
                    principalId,
                    FabrCoreSkillStorage.Container,
                    FabrCoreSkillStorage.ResourceKey(name, version, resource.ResourceId),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Skill resource cleanup failed after deleting {SkillReference}; resource {ResourceName} remains unreachable.",
                    $"{name}@{version}", resource.Name);
            }
        }

        logger.LogInformation(
            "Deleted harness skill {SkillReference} for principal {Principal}; digest {Digest}.",
            $"{name}@{version}", principalId, manifest.DigestSha256);
        return manifest;
    }

    private IFabrCoreSkillCatalogGrain Catalog(string principalId) =>
        grains.GetGrain<IFabrCoreSkillCatalogGrain>(principalId);

    private static async Task<ParsedPackage> ReadPackageAsync(
        string expectedName,
        string version,
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var files = archive.Entries.Where(entry => !IsDirectory(entry)).ToList();
            if (files.Count == 0 || files.Count > FabrCoreSkillStorage.MaxEntries)
            {
                throw new FabrCoreSkillValidationException(
                    $"Skill archive must contain 1-{FabrCoreSkillStorage.MaxEntries} files.");
            }

            var normalized = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in files)
            {
                var path = NormalizeEntryPath(entry);
                if (!normalized.TryAdd(path, entry))
                {
                    throw new FabrCoreSkillValidationException($"Duplicate archive path '{path}'.");
                }
            }

            var skillFiles = normalized.Keys.Where(path =>
                string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.Ordinal)).ToList();
            if (skillFiles.Count != 1)
            {
                throw new FabrCoreSkillValidationException("Archive must contain exactly one SKILL.md file.");
            }

            var skillPath = skillFiles[0];
            var prefix = skillPath[..^"SKILL.md".Length].TrimEnd('/');
            if (prefix.Contains('/') || (prefix.Length > 0 && !string.Equals(prefix, expectedName, StringComparison.Ordinal)))
            {
                throw new FabrCoreSkillValidationException(
                    "SKILL.md must be at the archive root or inside one top-level directory matching the skill name.");
            }

            var rootPrefix = prefix.Length == 0 ? string.Empty : prefix + "/";
            if (normalized.Keys.Any(path => rootPrefix.Length > 0 && !path.StartsWith(rootPrefix, StringComparison.Ordinal)))
            {
                throw new FabrCoreSkillValidationException("Archive contains files outside the single skill root.");
            }

            var markdownBytes = await ReadEntryAsync(
                normalized[skillPath], FabrCoreSkillStorage.MaxSkillMarkdownBytes, cancellationToken);
            var markdown = DecodeUtf8(markdownBytes, "SKILL.md");
            var frontmatter = ParseFrontmatter(markdown);
            if (!string.Equals(frontmatter.Name, expectedName, StringComparison.Ordinal))
            {
                throw new FabrCoreSkillValidationException(
                    $"SKILL.md name '{frontmatter.Name}' does not match route name '{expectedName}'.");
            }

            var parsedResources = new List<ParsedResource>();
            long totalBytes = markdownBytes.LongLength;
            foreach (var (path, entry) in normalized.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (path == skillPath)
                {
                    continue;
                }

                var relative = rootPrefix.Length == 0 ? path : path[rootPrefix.Length..];
                ValidateResourcePath(relative);
                var bytes = await ReadEntryAsync(entry, FabrCoreSkillStorage.MaxResourceBytes, cancellationToken);
                totalBytes += bytes.LongLength;
                if (totalBytes > FabrCoreSkillStorage.MaxPackageBytes)
                {
                    throw new FabrCoreSkillValidationException(
                        $"Skill package exceeds {FabrCoreSkillStorage.MaxPackageBytes} uncompressed bytes.");
                }

                var content = DecodeUtf8(bytes, relative);
                var digest = Sha256(bytes);
                var resourceId = Sha256(Encoding.UTF8.GetBytes(relative));
                var descriptor = new FabrCoreSkillResourceDescriptor
                {
                    Name = relative,
                    ResourceId = resourceId,
                    MediaType = MediaType(relative),
                    Length = bytes.LongLength,
                    DigestSha256 = digest
                };
                var document = new FabrCoreSkillResourceDocument
                {
                    Name = relative,
                    MediaType = descriptor.MediaType,
                    Content = content,
                    DigestSha256 = digest
                };
                EnsureEntitySize(document, relative);
                parsedResources.Add(new ParsedResource(descriptor, document));
            }

            var packageDigest = ComputePackageDigest(markdownBytes, parsedResources);
            var manifest = new FabrCoreSkillManifest
            {
                Name = expectedName,
                Version = version,
                Description = frontmatter.Description,
                SkillMarkdown = markdown,
                License = frontmatter.License,
                Compatibility = frontmatter.Compatibility,
                AllowedTools = frontmatter.AllowedTools,
                Metadata = frontmatter.Metadata,
                Resources = parsedResources.Select(resource => resource.Descriptor).ToList(),
                PublishedUtc = DateTimeOffset.UtcNow,
                DigestSha256 = packageDigest,
                TotalUncompressedBytes = totalBytes
            };
            EnsureEntitySize(manifest, "SKILL.md manifest");
            return new ParsedPackage(manifest, parsedResources);
        }
        catch (InvalidDataException ex)
        {
            throw new FabrCoreSkillValidationException($"Invalid ZIP archive: {ex.Message}");
        }
    }

    private static string NormalizeEntryPath(ZipArchiveEntry entry)
    {
        var raw = entry.FullName.Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\\') || raw.StartsWith('/') || raw.Contains(':'))
        {
            throw new FabrCoreSkillValidationException($"Unsafe archive path '{raw}'.");
        }

        var segments = raw.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new FabrCoreSkillValidationException($"Unsafe archive path '{raw}'.");
        }

        // Unix symlink file type in the high mode bits, or a Windows reparse-point attribute.
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new FabrCoreSkillValidationException($"Symbolic links are not allowed: '{raw}'.");
        }

        return string.Join('/', segments);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') && string.IsNullOrEmpty(entry.Name);

    private static void ValidateResourcePath(string path)
    {
        if (path.Length > FabrCoreSkillStorage.MaxResourcePathLength)
        {
            throw new FabrCoreSkillValidationException(
                $"Resource path exceeds {FabrCoreSkillStorage.MaxResourcePathLength} characters: '{path}'.");
        }

        var segments = path.Split('/');
        var extension = Path.GetExtension(path);
        if (segments.Any(segment => string.Equals(segment, "scripts", StringComparison.OrdinalIgnoreCase)) ||
            ExecutableExtensions.Contains(extension))
        {
            throw new FabrCoreSkillValidationException($"Executable skill content is not supported: '{path}'.");
        }

        if (!AllowedResourceExtensions.Contains(extension))
        {
            throw new FabrCoreSkillValidationException($"Resource extension '{extension}' is not allowed: '{path}'.");
        }

        if (segments.Length - 1 > FabrCoreSkillStorage.MaxResourceDepth)
        {
            throw new FabrCoreSkillValidationException($"Resource path exceeds the maximum depth: '{path}'.");
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumBytes)
        {
            throw new FabrCoreSkillValidationException($"Archive entry '{entry.FullName}' exceeds {maximumBytes} bytes.");
        }

        await using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, maximumBytes));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new FabrCoreSkillValidationException($"Archive entry '{entry.FullName}' exceeds {maximumBytes} bytes.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static string DecodeUtf8(byte[] bytes, string path)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FabrCoreSkillValidationException($"'{path}' is not valid UTF-8 text.");
        }
    }

    private static void EnsureEntitySize<T>(T value, string name)
    {
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        if (serializedBytes > FabrCoreSkillStorage.MaxSerializedEntityBytes)
        {
            throw new FabrCoreSkillValidationException(
                $"'{name}' serializes to {serializedBytes} bytes, exceeding the safe storage-entity limit of " +
                $"{FabrCoreSkillStorage.MaxSerializedEntityBytes} bytes.");
        }
    }

    private static Frontmatter ParseFrontmatter(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || lines[0].TrimStart('\uFEFF').Trim() != "---")
        {
            throw new FabrCoreSkillValidationException("SKILL.md must begin with YAML frontmatter delimited by ---.");
        }

        var closing = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (closing < 0)
        {
            throw new FabrCoreSkillValidationException("SKILL.md frontmatter is missing its closing --- delimiter.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? metadata = null;
        for (var index = 1; index < closing; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                throw new FabrCoreSkillValidationException(
                    $"Unexpected indented SKILL.md frontmatter line '{line.Trim()}'.");
            }

            var pair = SplitYamlPair(line);
            if (values.ContainsKey(pair.Key) ||
                (metadata is not null && string.Equals(pair.Key, "metadata", StringComparison.OrdinalIgnoreCase)))
            {
                throw new FabrCoreSkillValidationException(
                    $"Duplicate SKILL.md frontmatter key '{pair.Key}'.");
            }

            if (string.Equals(pair.Key, "metadata", StringComparison.OrdinalIgnoreCase) && pair.Value.Length == 0)
            {
                metadata = new(StringComparer.Ordinal);
                while (index + 1 < closing &&
                       (string.IsNullOrWhiteSpace(lines[index + 1]) || char.IsWhiteSpace(lines[index + 1][0])))
                {
                    var metadataLine = lines[++index];
                    if (string.IsNullOrWhiteSpace(metadataLine) || metadataLine.TrimStart().StartsWith('#'))
                    {
                        continue;
                    }

                    var metadataPair = SplitYamlPair(metadataLine.Trim());
                    if (!metadata.TryAdd(metadataPair.Key, metadataPair.Value))
                    {
                        throw new FabrCoreSkillValidationException(
                            $"Duplicate SKILL.md metadata key '{metadataPair.Key}'.");
                    }
                }

                continue;
            }

            if (pair.Value is ">" or ">-" or ">+" or "|" or "|-" or "|+")
            {
                var blockLines = new List<string>();
                while (index + 1 < closing &&
                       (string.IsNullOrWhiteSpace(lines[index + 1]) || char.IsWhiteSpace(lines[index + 1][0])))
                {
                    blockLines.Add(lines[++index].Trim());
                }

                if (blockLines.Count == 0)
                {
                    throw new FabrCoreSkillValidationException(
                        $"SKILL.md frontmatter block '{pair.Key}' is empty.");
                }

                values[pair.Key] = pair.Value[0] == '>'
                    ? string.Join(' ', blockLines.Where(value => value.Length > 0))
                    : string.Join('\n', blockLines).TrimEnd();
                continue;
            }

            values[pair.Key] = pair.Value;
        }

        values.TryGetValue("name", out var name);
        values.TryGetValue("description", out var description);
        if (!FabrCoreSkillReference.IsValidSkillName(name))
        {
            throw new FabrCoreSkillValidationException("SKILL.md contains an invalid or missing name.");
        }

        if (string.IsNullOrWhiteSpace(description) || description.Length > 1024)
        {
            throw new FabrCoreSkillValidationException("SKILL.md description is required and must not exceed 1024 characters.");
        }

        values.TryGetValue("compatibility", out var compatibility);
        if (compatibility?.Length > 500)
        {
            throw new FabrCoreSkillValidationException("SKILL.md compatibility must not exceed 500 characters.");
        }

        values.TryGetValue("license", out var license);
        values.TryGetValue("allowed-tools", out var allowedTools);
        return new Frontmatter(name!, description!, license, compatibility, allowedTools, metadata);
    }

    private static KeyValuePair<string, string> SplitYamlPair(string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            throw new FabrCoreSkillValidationException($"Invalid SKILL.md frontmatter line '{line}'.");
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return new(key, value);
    }

    private static string ComputePackageDigest(byte[] markdown, IReadOnlyList<ParsedResource> resources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("fabrcore-skill-package-v1\0"u8);
        AppendLengthPrefixed(hash, markdown);
        foreach (var resource in resources.OrderBy(item => item.Descriptor.Name, StringComparer.Ordinal))
        {
            AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(resource.Descriptor.Name));
            AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(resource.Document.Content));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".yaml" or ".yml" => "application/yaml",
        ".csv" => "text/csv",
        ".xml" => "application/xml",
        _ => "text/plain"
    };

    private static void ValidatePrincipal(string principalId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

    private static void ValidateIdentity(string principalId, string name, string version)
    {
        ValidatePrincipal(principalId);
        if (!FabrCoreSkillReference.IsValidSkillName(name))
        {
            throw new ArgumentException("Skill name is invalid.", nameof(name));
        }

        if (!FabrCoreSkillStorage.TryValidateVersion(version, out var reason))
        {
            throw new ArgumentException(reason, nameof(version));
        }
    }

    private sealed record ParsedPackage(FabrCoreSkillManifest Manifest, List<ParsedResource> Resources);
    private sealed record ParsedResource(FabrCoreSkillResourceDescriptor Descriptor, FabrCoreSkillResourceDocument Document);
    private sealed record Frontmatter(
        string Name,
        string Description,
        string? License,
        string? Compatibility,
        string? AllowedTools,
        Dictionary<string, string>? Metadata);
}

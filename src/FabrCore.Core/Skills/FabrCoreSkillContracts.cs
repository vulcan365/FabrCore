namespace FabrCore.Core.Skills;

/// <summary>Storage and validation conventions for immutable FabrCore harness skills.</summary>
public static class FabrCoreSkillStorage
{
    public const string Container = "fabrcore.harness-skills";
    public const int CurrentSchemaVersion = 1;
    public const int MaxSkillMarkdownBytes = 256 * 1024;
    public const int MaxResourceBytes = 512 * 1024;
    public const int MaxPackageBytes = 4 * 1024 * 1024;
    public const int MaxEntries = 128;
    public const int MaxResourceDepth = 2;
    public const int MaxResourcePathLength = 256;
    public const int MaxSerializedEntityBytes = 700 * 1024;

    public static string ManifestKey(string name, string version) =>
        $"packages/{name}/{version}/manifest";

    public static string ResourceKey(string name, string version, string resourceId) =>
        $"packages/{name}/{version}/resources/{resourceId}";

    public static bool TryValidateVersion(string? version, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            reason = "Skill version is required.";
            return false;
        }

        if (version.Length > 64)
        {
            reason = "Skill version must be 64 characters or fewer.";
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(version[0]) ||
            version.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            reason = "Skill version must start with a letter or digit and use only letters, digits, '.', '_', or '-'.";
            return false;
        }

        reason = null;
        return true;
    }
}

/// <summary>A pinned, principal-local harness skill reference.</summary>
public sealed record FabrCoreSkillReference(string Name, string Version)
{
    public override string ToString() => $"{Name}@{Version}";

    public static bool TryParse(string? value, out FabrCoreSkillReference? reference, out string? reason)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "Skill reference is empty.";
            return false;
        }

        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf('@');
        if (separator <= 0 || separator == trimmed.Length - 1 || trimmed.IndexOf('@') != separator)
        {
            reason = $"Skill reference '{trimmed}' must use name@version.";
            return false;
        }

        var name = trimmed[..separator];
        var version = trimmed[(separator + 1)..];
        if (!IsValidSkillName(name))
        {
            reason = $"Skill name '{name}' must be kebab-case, 1-64 characters, with no leading, trailing, or consecutive hyphens.";
            return false;
        }

        if (!FabrCoreSkillStorage.TryValidateVersion(version, out reason))
        {
            return false;
        }

        reference = new FabrCoreSkillReference(name, version);
        reason = null;
        return true;
    }

    public static bool IsValidSkillName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            !char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in name)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (!(character is >= 'a' and <= 'z') && !char.IsDigit(character))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }
}

/// <summary>Normalized, immutable manifest committed last when a skill version is published.</summary>
public sealed class FabrCoreSkillManifest
{
    public int SchemaVersion { get; set; } = FabrCoreSkillStorage.CurrentSchemaVersion;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SkillMarkdown { get; set; } = string.Empty;
    public string? License { get; set; }
    public string? Compatibility { get; set; }
    public string? AllowedTools { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public List<FabrCoreSkillResourceDescriptor> Resources { get; set; } = [];
    public DateTimeOffset PublishedUtc { get; set; }
    public string DigestSha256 { get; set; } = string.Empty;
    public long TotalUncompressedBytes { get; set; }

    public FabrCoreSkillCatalogEntry ToCatalogEntry() => new()
    {
        Name = Name,
        Version = Version,
        Description = Description,
        PublishedUtc = PublishedUtc,
        DigestSha256 = DigestSha256,
        ResourceCount = Resources.Count,
        TotalUncompressedBytes = TotalUncompressedBytes
    };
}

public sealed class FabrCoreSkillResourceDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string MediaType { get; set; } = "text/plain";
    public long Length { get; set; }
    public string DigestSha256 { get; set; } = string.Empty;
}

/// <summary>A separately stored textual skill resource.</summary>
public sealed class FabrCoreSkillResourceDocument
{
    public int SchemaVersion { get; set; } = FabrCoreSkillStorage.CurrentSchemaVersion;
    public string Name { get; set; } = string.Empty;
    public string MediaType { get; set; } = "text/plain";
    public string Content { get; set; } = string.Empty;
    public string DigestSha256 { get; set; } = string.Empty;
}

[GenerateSerializer]
public sealed class FabrCoreSkillCatalogEntry
{
    [Id(0)] public string Name { get; set; } = string.Empty;
    [Id(1)] public string Version { get; set; } = string.Empty;
    [Id(2)] public string Description { get; set; } = string.Empty;
    [Id(3)] public DateTimeOffset PublishedUtc { get; set; }
    [Id(4)] public string DigestSha256 { get; set; } = string.Empty;
    [Id(5)] public int ResourceCount { get; set; }
    [Id(6)] public long TotalUncompressedBytes { get; set; }

    public string Reference => $"{Name}@{Version}";
}

public sealed class FabrCoreSkillPublishResult
{
    public required FabrCoreSkillManifest Manifest { get; init; }
    public bool AlreadyExisted { get; init; }
}

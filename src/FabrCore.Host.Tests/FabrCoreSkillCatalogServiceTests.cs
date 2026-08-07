using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Skills;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class FabrCoreSkillCatalogServiceTests
{
    [TestMethod]
    public async Task PublishCommitsManifestLastAndIndexesPrincipalCatalog()
    {
        var storage = new RecordingStorageProvider();
        var grains = SkillGrainFactoryProxy.Create();
        var service = CreateService(storage, grains);

        await using var zip = CreateZip(
            ("SKILL.md", SkillMarkdown("policy-review")),
            ("references/policy.md", "Read this policy."));

        var result = await service.PublishAsync("principal-a", "policy-review", "1.2.0", zip);

        Assert.IsFalse(result.AlreadyExisted);
        Assert.AreEqual("policy-review", result.Manifest.Name);
        Assert.AreEqual("1.2.0", result.Manifest.Version);
        Assert.HasCount(1, result.Manifest.Resources);
        Assert.AreEqual(FabrCoreSkillStorage.Container, storage.Writes[0].Container);
        StringAssert.Contains(storage.Writes[0].EntityKey, "/resources/");
        Assert.AreEqual(FabrCoreSkillStorage.ManifestKey("policy-review", "1.2.0"), storage.Writes[1].EntityKey);
        Assert.IsTrue(storage.Writes.All(write => write.PrincipalId == "principal-a"));
        Assert.IsTrue(storage.Writes.All(write => write.Value is not byte[]));

        var catalog = await service.ListAsync("principal-a");
        Assert.HasCount(1, catalog);
        Assert.AreEqual(result.Manifest.DigestSha256, catalog[0].DigestSha256);
        Assert.IsEmpty(await service.ListAsync("principal-b"));

        // Even the largest permitted resource remains comfortably below Azure Table's 1 MiB entity ceiling.
        var storedResource = storage.Values.Values.OfType<FabrCoreSkillResourceDocument>().Single();
        Assert.IsLessThan(1024 * 1024, JsonSerializer.SerializeToUtf8Bytes(storedResource).Length);
    }

    [TestMethod]
    public async Task RepeatedDigestIsIdempotentAndChangedVersionContentConflicts()
    {
        var service = CreateService(new RecordingStorageProvider(), SkillGrainFactoryProxy.Create());

        await using (var first = CreateZip(("SKILL.md", SkillMarkdown("policy-review"))))
        {
            var published = await service.PublishAsync("principal-a", "policy-review", "1.0.0", first);
            Assert.IsFalse(published.AlreadyExisted);
        }

        await using (var retry = CreateZip(("SKILL.md", SkillMarkdown("policy-review"))))
        {
            var published = await service.PublishAsync("principal-a", "policy-review", "1.0.0", retry);
            Assert.IsTrue(published.AlreadyExisted);
        }

        await using var changed = CreateZip(("SKILL.md", SkillMarkdown("policy-review") + "\nChanged."));
        await Assert.ThrowsExactlyAsync<FabrCoreSkillConflictException>(() =>
            service.PublishAsync("principal-a", "policy-review", "1.0.0", changed));
    }

    [TestMethod]
    public async Task DeleteRemovesCommitMarkerAndCatalogBeforeResources()
    {
        var storage = new RecordingStorageProvider();
        var service = CreateService(storage, SkillGrainFactoryProxy.Create());
        await using (var zip = CreateZip(
            ("policy-review/SKILL.md", SkillMarkdown("policy-review")),
            ("policy-review/references/policy.md", "Policy")))
        {
            await service.PublishAsync("principal-a", "policy-review", "2026-08-01", zip);
        }

        storage.Operations.Clear();
        var deleted = await service.DeleteAsync("principal-a", "policy-review", "2026-08-01");

        Assert.IsNotNull(deleted);
        Assert.AreEqual(
            "delete:" + FabrCoreSkillStorage.ManifestKey("policy-review", "2026-08-01"),
            storage.Operations[0]);
        StringAssert.Contains(storage.Operations[1], "/resources/");
        Assert.IsNull(await service.GetAsync("principal-a", "policy-review", "2026-08-01"));
        Assert.IsEmpty(await service.ListAsync("principal-a"));
    }

    [TestMethod]
    public async Task DeleteRetryRepairsAStaleCatalogAfterManifestRemoval()
    {
        var storage = new RecordingStorageProvider();
        var service = CreateService(storage, SkillGrainFactoryProxy.Create());
        await using (var zip = CreateZip(("SKILL.md", SkillMarkdown("policy-review"))))
        {
            await service.PublishAsync("principal-a", "policy-review", "1.0.0", zip);
        }

        await storage.DeleteAsync(
            "principal-a",
            FabrCoreSkillStorage.Container,
            FabrCoreSkillStorage.ManifestKey("policy-review", "1.0.0"));

        Assert.IsNull(await service.DeleteAsync("principal-a", "policy-review", "1.0.0"));
        Assert.IsEmpty(await service.ListAsync("principal-a"));
    }

    [TestMethod]
    public async Task OrphanResourcesWithoutManifestAreNotVisibleAndCanBeRecoveredByPublish()
    {
        var storage = new RecordingStorageProvider();
        var service = CreateService(storage, SkillGrainFactoryProxy.Create());
        await storage.UpsertAsync(
            "principal-a",
            FabrCoreSkillStorage.Container,
            FabrCoreSkillStorage.ResourceKey("policy-review", "1.0.0", "orphan"),
            new FabrCoreSkillResourceDocument { Name = "orphan.md", Content = "uncommitted" });

        Assert.IsNull(await service.GetAsync("principal-a", "policy-review", "1.0.0"));
        Assert.IsEmpty(await service.ListAsync("principal-a"));

        await using var zip = CreateZip(("SKILL.md", SkillMarkdown("policy-review")));
        var result = await service.PublishAsync("principal-a", "policy-review", "1.0.0", zip);
        Assert.IsFalse(result.AlreadyExisted);
        Assert.IsNotNull(await service.GetAsync("principal-a", "policy-review", "1.0.0"));
    }

    [TestMethod]
    public async Task ConcurrentPublicationsRetainEveryPrincipalCatalogEntry()
    {
        var service = CreateService(new RecordingStorageProvider(), SkillGrainFactoryProxy.Create());
        var publications = Enumerable.Range(1, 20).Select(async version =>
        {
            await using var zip = CreateZip(("SKILL.md", SkillMarkdown("policy-review")));
            await service.PublishAsync("principal-a", "policy-review", $"1.0.{version}", zip);
        });

        await Task.WhenAll(publications);

        var catalog = await service.ListAsync("principal-a");
        Assert.HasCount(20, catalog);
    }

    [TestMethod]
    [DataRow("../SKILL.md")]
    [DataRow("/SKILL.md")]
    [DataRow("C:/SKILL.md")]
    [DataRow("policy-review/scripts/rules.txt")]
    [DataRow("policy-review/rules.py")]
    [DataRow("policy-review/rules.bin")]
    [DataRow("policy-review/a/b/c/rules.md")]
    public async Task RejectsUnsafeOrUnsupportedPaths(string invalidPath)
    {
        var service = CreateService(new RecordingStorageProvider(), SkillGrainFactoryProxy.Create());
        var entries = invalidPath.EndsWith("SKILL.md", StringComparison.Ordinal)
            ? new[] { (invalidPath, SkillMarkdown("policy-review")) }
            : new[]
            {
                ("policy-review/SKILL.md", SkillMarkdown("policy-review")),
                (invalidPath, "content")
            };
        await using var zip = CreateZip(entries);

        await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
            service.PublishAsync("principal-a", "policy-review", "1.0.0", zip));
    }

    [TestMethod]
    public async Task RejectsDuplicateNormalizedPathsAndSymlinks()
    {
        var service = CreateService(new RecordingStorageProvider(), SkillGrainFactoryProxy.Create());
        await using (var duplicate = CreateZip(
            ("SKILL.md", SkillMarkdown("policy-review")),
            ("references/rule.md", "one"),
            ("references/rule.md", "two")))
        {
            await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
                service.PublishAsync("principal-a", "policy-review", "1.0.0", duplicate));
        }

        await using var symlink = CreateZip(
            ("SKILL.md", Encoding.UTF8.GetBytes(SkillMarkdown("policy-review")), 0),
            ("references/link.md", Encoding.UTF8.GetBytes("target"), 0xA000 << 16));
        await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
            service.PublishAsync("principal-a", "policy-review", "1.0.0", symlink));
    }

    [TestMethod]
    public async Task RejectsInvalidUtf8FrontmatterMismatchAndEntrySizeLimits()
    {
        var service = CreateService(new RecordingStorageProvider(), SkillGrainFactoryProxy.Create());

        await using (var invalidUtf8 = CreateZip(("SKILL.md", new byte[] { 0xFF, 0xFE }, 0)))
        {
            await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
                service.PublishAsync("principal-a", "policy-review", "1.0.0", invalidUtf8));
        }

        await using (var mismatch = CreateZip(("SKILL.md", SkillMarkdown("other-skill"))))
        {
            await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
                service.PublishAsync("principal-a", "policy-review", "1.0.0", mismatch));
        }

        var oversizedMarkdown = SkillMarkdown("policy-review")
            + new string('x', FabrCoreSkillStorage.MaxSkillMarkdownBytes);
        await using var oversized = CreateZip(("SKILL.md", oversizedMarkdown));
        await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
            service.PublishAsync("principal-a", "policy-review", "1.0.0", oversized));
    }

    [TestMethod]
    public async Task NormalizesBlockFrontmatterAndEnforcesResourcePackageAndEntityLimits()
    {
        var service = CreateService(new RecordingStorageProvider(), SkillGrainFactoryProxy.Create());
        var blockMarkdown = """
            ---
            name: policy-review
            description: >
              Reviews a policy against
              the required checklist.
            ---
            Follow the checklist.
            """;
        await using (var valid = CreateZip(("SKILL.md", blockMarkdown)))
        {
            var result = await service.PublishAsync("principal-a", "policy-review", "2.0.0", valid);
            Assert.AreEqual("Reviews a policy against the required checklist.", result.Manifest.Description);
        }

        await using (var resourceTooLarge = CreateZip(
            ("SKILL.md", SkillMarkdown("policy-review")),
            ("large.txt", new string('x', FabrCoreSkillStorage.MaxResourceBytes + 1))))
        {
            await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
                service.PublishAsync("principal-b", "policy-review", "1.0.0", resourceTooLarge));
        }

        var tooManyEntries = new List<(string Path, string Content)>
        {
            ("SKILL.md", SkillMarkdown("policy-review"))
        };
        tooManyEntries.AddRange(Enumerable.Range(0, FabrCoreSkillStorage.MaxEntries)
            .Select(index => ($"r{index}.txt", "x")));
        await using (var entries = CreateZip(tooManyEntries.ToArray()))
        {
            await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
                service.PublishAsync("principal-b", "policy-review", "1.0.0", entries));
        }

        var packageEntries = new List<(string Path, string Content)>
        {
            ("SKILL.md", SkillMarkdown("policy-review"))
        };
        packageEntries.AddRange(Enumerable.Range(0, 9)
            .Select(index => ($"r{index}.txt", new string('x', FabrCoreSkillStorage.MaxResourceBytes))));
        await using (var package = CreateZip(packageEntries.ToArray()))
        {
            await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
                service.PublishAsync("principal-b", "policy-review", "1.0.0", package));
        }

        await using var escapedEntity = CreateZip(
            ("SKILL.md", SkillMarkdown("policy-review")),
            ("escaped.txt", new string('\\', 400 * 1024)));
        await Assert.ThrowsExactlyAsync<FabrCoreSkillValidationException>(() =>
            service.PublishAsync("principal-b", "policy-review", "1.0.0", escapedEntity));
    }

    private static FabrCoreSkillCatalogService CreateService(
        RecordingStorageProvider storage,
        IGrainFactory grains) =>
        new(storage, grains, NullLogger<FabrCoreSkillCatalogService>.Instance);

    private static string SkillMarkdown(string name) => $$"""
        ---
        name: {{name}}
        description: Test skill
        ---
        # Instructions

        Follow the policy.
        """;

    private static MemoryStream CreateZip(params (string Path, string Content)[] entries) =>
        CreateZip(entries.Select(entry =>
            (entry.Path, Encoding.UTF8.GetBytes(entry.Content), 0)).ToArray());

    private static MemoryStream CreateZip(params (string Path, byte[] Content, int Attributes)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path);
                entry.ExternalAttributes = item.Attributes;
                using var output = entry.Open();
                output.Write(item.Content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class RecordingStorageProvider : IPrincipalScopedFabrCoreStorageProvider
    {
        public ConcurrentDictionary<(string Principal, string Container, string Key), object> Values { get; } = [];
        public List<StorageWrite> Writes { get; } = [];
        public List<string> Operations { get; } = [];

        public Task<T?> GetAsync<T>(
            string principalId,
            string container,
            string entityKey,
            CancellationToken cancellationToken = default)
        {
            Values.TryGetValue((principalId, container, entityKey), out var value);
            return Task.FromResult((T?)value);
        }

        public Task UpsertAsync<T>(
            string principalId,
            string container,
            string entityKey,
            T value,
            CancellationToken cancellationToken = default)
        {
            Values[(principalId, container, entityKey)] = value!;
            lock (Writes)
            {
                Writes.Add(new StorageWrite(principalId, container, entityKey, value!));
            }
            lock (Operations)
            {
                Operations.Add("upsert:" + entityKey);
            }
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(
            string principalId,
            string container,
            string entityKey,
            CancellationToken cancellationToken = default)
        {
            lock (Operations)
            {
                Operations.Add("delete:" + entityKey);
            }
            return Task.FromResult(Values.TryRemove((principalId, container, entityKey), out _));
        }
    }

    private sealed record StorageWrite(string PrincipalId, string Container, string EntityKey, object Value);

    private class SkillGrainFactoryProxy : DispatchProxy
    {
        private readonly Dictionary<string, FakeSkillCatalogGrain> _grains = new(StringComparer.Ordinal);

        public static IGrainFactory Create() => DispatchProxy.Create<IGrainFactory, SkillGrainFactoryProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IFabrCoreSkillCatalogGrain)
                && args is { Length: > 0 }
                && args[0] is string principalId)
            {
                if (!_grains.TryGetValue(principalId, out var grain))
                {
                    grain = new FakeSkillCatalogGrain();
                    _grains[principalId] = grain;
                }

                return grain;
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private sealed class FakeSkillCatalogGrain : IFabrCoreSkillCatalogGrain
    {
        private readonly Dictionary<string, FabrCoreSkillCatalogEntry> _entries = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<List<FabrCoreSkillCatalogEntry>> ListAsync()
        {
            await _gate.WaitAsync();
            try
            {
                return _entries.Values.ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task UpsertAsync(FabrCoreSkillCatalogEntry entry)
        {
            await _gate.WaitAsync();
            try
            {
                _entries[entry.Reference] = entry;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> RemoveAsync(string name, string version)
        {
            await _gate.WaitAsync();
            try
            {
                return _entries.Remove($"{name}@{version}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

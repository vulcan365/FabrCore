using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryIndexManagerTests
{
    private const string Scope = "unit:index";
    private string? _indexJson;
    private IMemoryStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _indexJson = null;
        _store = Substitute.For<IMemoryStore>();
        _store.GetIndexContentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_indexJson));
        _store.ModifyIndexContentAsync(
                Arg.Any<string>(), Arg.Any<Func<string?, string?>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var transform = call.Arg<Func<string?, string?>>()!;
                var updated = transform(_indexJson);
                if (updated is not null)
                    _indexJson = updated;
                return Task.CompletedTask;
            });
        _store.UpsertIndexContentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _indexJson = call.ArgAt<string>(1);
                return Task.CompletedTask;
            });
    }

    [TestMethod]
    public async Task AddIndexEntry_PersistsAndReplacesSameMemory()
    {
        var manager = CreateManager();
        var id = Guid.NewGuid();

        await manager.AddIndexEntryAsync(Scope, Entry(id, "original"));
        await manager.AddIndexEntryAsync(Scope, Entry(id, "updated"));

        var index = await manager.GetIndexAsync(Scope);
        Assert.HasCount(1, index.Entries);
        Assert.AreEqual("updated", index.Entries[0].Title);
        Assert.IsGreaterThan(0, index.TotalEstimatedTokens);
    }

    [TestMethod]
    public async Task AddIndexEntry_OverEntryCapEvictsOldest()
    {
        var manager = CreateManager(maxEntries: 3);
        var baseline = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
            await manager.AddIndexEntryAsync(Scope, Entry(Guid.NewGuid(), $"m{i}", baseline.AddMinutes(i)));

        var index = await manager.GetIndexAsync(Scope);
        CollectionAssert.AreEqual(
            new[] { "m4", "m3", "m2" },
            index.Entries.Select(e => e.Title).ToArray());
    }

    [TestMethod]
    public async Task AddIndexEntry_OverTokenCapEvictsFromTail()
    {
        var manager = CreateManager(maxEntries: 100, maxTokens: 40);
        var baseline = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
            await manager.AddIndexEntryAsync(
                Scope, Entry(Guid.NewGuid(), $"memory number {i}", baseline.AddMinutes(i)));

        var index = await manager.GetIndexAsync(Scope);
        Assert.IsNotEmpty(index.Entries);
        Assert.IsLessThan(5, index.Entries.Count);
        Assert.IsLessThanOrEqualTo(40, index.TotalEstimatedTokens);
    }

    [TestMethod]
    public async Task RemoveIndexEntry_RemovesOnlyRequestedMemory()
    {
        var manager = CreateManager();
        var keep = Entry(Guid.NewGuid(), "keep");
        var remove = Entry(Guid.NewGuid(), "remove");
        await manager.AddIndexEntryAsync(Scope, keep);
        await manager.AddIndexEntryAsync(Scope, remove);

        await manager.RemoveIndexEntryAsync(Scope, remove.MemoryId);

        var index = await manager.GetIndexAsync(Scope);
        Assert.HasCount(1, index.Entries);
        Assert.AreEqual(keep.MemoryId, index.Entries[0].MemoryId);
    }

    [TestMethod]
    public async Task GetIndex_CorruptJsonReturnsEmptyIndex()
    {
        _indexJson = "{ invalid json";

        var index = await CreateManager().GetIndexAsync(Scope);

        Assert.IsEmpty(index.Entries);
    }

    [TestMethod]
    public async Task TruncateIndex_UnderCapsDoesNotWrite()
    {
        var manager = CreateManager(maxEntries: 10);
        await manager.AddIndexEntryAsync(Scope, Entry(Guid.NewGuid(), "only"));
        _store.ClearReceivedCalls();

        var evicted = await manager.TruncateIndexAsync(Scope);

        Assert.IsEmpty(evicted);
        await _store.DidNotReceive().UpsertIndexContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private MemoryIndexManager CreateManager(int maxEntries = 20, int maxTokens = 3000)
    {
        var options = new AgentMemoryOptions();
        options.HotIndex.MaxEntries = maxEntries;
        options.HotIndex.MaxTokens = maxTokens;
        return new MemoryIndexManager(_store, options, NullLoggerFactory.Instance);
    }

    private static MemoryIndexEntry Entry(
        Guid id, string title, DateTime? updatedAt = null) => new()
    {
        MemoryId = id,
        Title = title,
        Type = MemoryType.Fact,
        DescriptionHook = $"hook for {title}",
        UpdatedAt = updatedAt ?? DateTime.UtcNow
    };
}

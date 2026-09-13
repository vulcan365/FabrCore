using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryCompactorTests
{
    [TestMethod]
    public async Task Deduplication_ArchivesOriginalWithoutDeletingWhenModelUnavailable()
    {
        var store = Substitute.For<IMemoryStore>();
        var index = Substitute.For<IMemoryIndexManager>();
        var older = new MemoryEntry { Id = Guid.NewGuid(), Title = "Old", Type = MemoryType.Fact, UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        var newer = new MemoryEntry { Id = Guid.NewGuid(), Title = "New", Type = MemoryType.Fact, UpdatedAt = DateTime.UtcNow };
        store.FindDuplicatePairsAsync("scope", Arg.Any<double>(), null, Arg.Any<CancellationToken>()).Returns(new[] { (older.Id, newer.Id, 0.01d) });
        store.GetEntityByIdAsync("scope", older.Id, Arg.Any<CancellationToken>()).Returns(older);
        store.GetEntityByIdAsync("scope", newer.Id, Arg.Any<CancellationToken>()).Returns(newer);
        using var services = new ServiceCollection().BuildServiceProvider();
        var compactor = new MemoryCompactor(store, index, Substitute.For<IMemorySummaryTree>(), new AgentMemoryOptions(), services, NullLoggerFactory.Instance);
        Assert.AreEqual(1, await compactor.DeduplicateAsync("scope"));
        Assert.AreEqual(MemoryTemperature.Cold, older.Temperature);
        await store.Received(1).UpdateEntityAsync("scope", older, Arg.Any<CancellationToken>());
        await store.DidNotReceive().DeleteEntityAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await index.Received(1).RemoveIndexEntryAsync("scope", older.Id, Arg.Any<CancellationToken>());
        Assert.AreEqual(0, await compactor.DeduplicateAsync("scope"), "Already archived sources must not be consolidated again.");
    }

    [TestMethod]
    public async Task AgePruning_PreservesStandingInstructionsWithoutModel()
    {
        var store = Substitute.For<IMemoryStore>();
        var index = Substitute.For<IMemoryIndexManager>();
        store.GetHeadersAsync("scope", Arg.Any<int>(), null, Arg.Any<CancellationToken>()).Returns(new[] {
            new MemoryHeader { MemoryId = Guid.NewGuid(), Type = MemoryType.Instruction, UpdatedAt = DateTime.UtcNow.AddYears(-1) } });
        index.GetIndexAsync("scope", Arg.Any<CancellationToken>()).Returns(new MemoryIndex());
        using var services = new ServiceCollection().BuildServiceProvider();
        var compactor = new MemoryCompactor(store, index, Substitute.For<IMemorySummaryTree>(), new AgentMemoryOptions(), services, NullLoggerFactory.Instance);
        Assert.AreEqual(0, await compactor.PruneStaleAsync("scope"));
        await store.DidNotReceive().UpdateEntityAsync(Arg.Any<string>(), Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>());
    }
}

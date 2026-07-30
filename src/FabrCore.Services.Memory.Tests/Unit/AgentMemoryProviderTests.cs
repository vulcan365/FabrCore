using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class AgentMemoryProviderTests
{
    [TestMethod]
    public void GetMemoryService_CachesByTrimmedScope()
    {
        var provider = CreateProvider();

        var first = provider.GetMemoryService(" shared-scope ");
        var second = provider.GetMemoryService("shared-scope");

        Assert.AreSame(first, second);
        Assert.AreEqual("shared-scope", first.ScopeKey);
    }

    [TestMethod]
    public void EvictMemoryService_RecreatesServiceOnNextAccess()
    {
        var provider = CreateProvider();
        var first = provider.GetMemoryService("scope");

        Assert.IsTrue(provider.EvictMemoryService("scope"));
        Assert.IsFalse(provider.EvictMemoryService("scope"));

        Assert.AreNotSame(first, provider.GetMemoryService("scope"));
    }

    [TestMethod]
    public void GetMemoryService_RejectsBlankScope()
    {
        var provider = CreateProvider();

        Assert.ThrowsExactly<ArgumentException>(() => provider.GetMemoryService(" "));
        Assert.ThrowsExactly<ArgumentException>(() => provider.EvictMemoryService(" "));
    }

    private static AgentMemoryProvider CreateProvider() => new(
        Substitute.For<IMemoryStore>(),
        Substitute.For<IMemoryIndexManager>(),
        Substitute.For<IMemoryRetriever>(),
        Substitute.For<IMemoryCompactor>(),
        Substitute.For<IRetrievalPlanner>(),
        Substitute.For<IMemorySummaryTree>(),
        Substitute.For<IMemoryScopeService>(),
        Substitute.For<IMemoryAuditLog>(),
        new AgentMemoryOptions(),
        Substitute.For<IServiceProvider>(),
        NullLoggerFactory.Instance);
}

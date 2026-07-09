using FabrCore.Core.Auditing;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class AuditProviderTests
{
    private static InMemoryAuditProvider CreateProvider(AuditOptions? options = null)
        => new(NullLogger<InMemoryAuditProvider>.Instance, Options.Create(options ?? new AuditOptions()));

    [TestMethod]
    public void ShouldRecord_DefaultLevels()
    {
        var options = new AuditOptions();

        // AclDecision uses DefaultLevel = Failures.
        Assert.IsFalse(options.ShouldRecord(AuditCategory.AclDecision, AuditOutcome.Success));
        Assert.IsTrue(options.ShouldRecord(AuditCategory.AclDecision, AuditOutcome.Denied));
        Assert.IsTrue(options.ShouldRecord(AuditCategory.AclDecision, AuditOutcome.Error));

        // Management, boundary crossings, and bootstrap default to All.
        Assert.IsTrue(options.ShouldRecord(AuditCategory.AclManagement, AuditOutcome.Success));
        Assert.IsTrue(options.ShouldRecord(AuditCategory.BoundaryCrossing, AuditOutcome.Success));
        Assert.IsTrue(options.ShouldRecord(AuditCategory.Bootstrap, AuditOutcome.Success));
    }

    [TestMethod]
    public void ShouldRecord_NoneLevel_SuppressesEverything()
    {
        var options = new AuditOptions
        {
            DefaultLevel = AuditLevel.None,
            Categories = new Dictionary<AuditCategory, AuditLevel>()
        };

        Assert.IsFalse(options.ShouldRecord(AuditCategory.AclDecision, AuditOutcome.Denied));
        Assert.IsFalse(options.ShouldRecord(AuditCategory.Bootstrap, AuditOutcome.Error));
    }

    [TestMethod]
    public async Task Record_FiltersByLevel()
    {
        var provider = CreateProvider();

        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Success });
        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Denied });

        var events = await provider.GetEventsAsync();
        Assert.HasCount(1, events);
        Assert.AreEqual(AuditOutcome.Denied, events[0].Outcome);
    }

    [TestMethod]
    public async Task Record_EvictsFifoAtCapacity()
    {
        var provider = CreateProvider(new AuditOptions { MaxBufferedEvents = 3 });

        for (var i = 0; i < 5; i++)
        {
            await provider.RecordAsync(new AuditEvent
            {
                Category = AuditCategory.AclDecision,
                Outcome = AuditOutcome.Denied,
                Reason = $"event-{i}"
            });
        }

        var events = await provider.GetEventsAsync();
        Assert.HasCount(3, events);
        // Most recent first; oldest two evicted.
        Assert.AreEqual("event-4", events[0].Reason);
        Assert.AreEqual("event-2", events[2].Reason);
    }

    [TestMethod]
    public async Task GetEvents_AppliesQueryFilters()
    {
        var provider = CreateProvider(new AuditOptions { DefaultLevel = AuditLevel.All });

        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Denied, SubjectPrincipal = "alice" });
        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.BoundaryCrossing, Outcome = AuditOutcome.Success, SubjectPrincipal = "alice" });
        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Denied, SubjectPrincipal = "bob" });

        var aliceDecisions = await provider.GetEventsAsync(new AuditQuery
        {
            Category = AuditCategory.AclDecision,
            SubjectPrincipal = "ALICE" // case-insensitive
        });

        Assert.HasCount(1, aliceDecisions);
        Assert.AreEqual("alice", aliceDecisions[0].SubjectPrincipal);
    }

    [TestMethod]
    public async Task OnAuditEventRecorded_SubscriberExceptionsDoNotPropagate()
    {
        var provider = CreateProvider();
        var received = 0;
        provider.OnAuditEventRecorded += _ => throw new InvalidOperationException("boom");
        provider.OnAuditEventRecorded += _ => received++;

        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Denied });

        Assert.AreEqual(1, received);
    }

    [TestMethod]
    public async Task Clear_RemovesAllEvents()
    {
        var provider = CreateProvider();
        await provider.RecordAsync(new AuditEvent { Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Denied });

        await provider.ClearAsync();

        Assert.IsEmpty(await provider.GetEventsAsync());
    }
}

using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class ExtractionResultCacheTests
{
    [TestMethod]
    public void Cache_ExpiresEvictsAndRejectsOversizedResponses()
    {
        var clock = new Clock();
        var cache = new ExtractionResultCache(2, TimeSpan.FromMinutes(1), clock);
        cache.Put("a", "first");
        clock.Now += TimeSpan.FromSeconds(1);
        cache.Put("b", "second");
        cache.Put("c", "third");
        Assert.IsNull(cache.Get("a"));
        Assert.AreEqual("second", cache.Get("b"));
        cache.Put("huge", new string('x', 262_145));
        Assert.IsNull(cache.Get("huge"));
        Assert.AreEqual("third", cache.Get("c"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.IsNull(cache.Get("b"));
        Assert.IsNull(cache.Get("c"));
    }

    [TestMethod]
    public void Key_PreservesComponentBoundariesAndExactContent()
    {
        Assert.AreNotEqual(ExtractionResultCache.Key("ab", "c"), ExtractionResultCache.Key("a", "bc"));
        Assert.AreNotEqual(ExtractionResultCache.Key("model", "text"), ExtractionResultCache.Key("model", "text "));
        Assert.AreNotEqual(ExtractionResultCache.Key("mini", "text"), ExtractionResultCache.Key("nano", "text"));
        Assert.AreEqual(ExtractionResultCache.Key("mini", "text"), ExtractionResultCache.Key("mini", "text"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

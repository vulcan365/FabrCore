using FabrCore.Core;
using FabrCore.Services.Memory.Configuration;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryScopeResolverTests
{
    private static AgentConfiguration Config(
        string? handle = null, Dictionary<string, string>? args = null) =>
        new() { Handle = handle!, Args = args! };

    [TestMethod]
    public void Resolve_UsesDocumentedPrecedence()
    {
        var config = Config("agent:isolated", new Dictionary<string, string>
        {
            ["agent-memory:MemoryScope"] = "plugin-scope",
            ["MemoryScope"] = "argument-scope",
            ["AgentHandle"] = "legacy-scope"
        });

        Assert.AreEqual("explicit-scope", MemoryScopeResolver.Resolve(config, "explicit-scope"));
        Assert.AreEqual("plugin-scope", MemoryScopeResolver.Resolve(config));

        config.Args!.Remove("agent-memory:MemoryScope");
        Assert.AreEqual("argument-scope", MemoryScopeResolver.Resolve(config));

        config.Args.Remove("MemoryScope");
        Assert.AreEqual("legacy-scope", MemoryScopeResolver.Resolve(config));

        config.Args.Remove("AgentHandle");
        Assert.AreEqual("agent:isolated", MemoryScopeResolver.Resolve(config));
    }

    [TestMethod]
    public void Resolve_TrimsScope()
    {
        var config = Config("agent:isolated", new Dictionary<string, string>
        {
            ["MemoryScope"] = "  bank-reconciliation  "
        });

        Assert.AreEqual("bank-reconciliation", MemoryScopeResolver.Resolve(config));
    }

    [TestMethod]
    public void Resolve_ThrowsWhenNoScopeCanBeResolved()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MemoryScopeResolver.Resolve(Config()));
    }
}

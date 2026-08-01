using FabrCore.Services.Memory.Configuration;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryModelOptionsTests
{
    [TestMethod]
    public void ResolveModelForCall_ExplicitOperationNameWins()
    {
        var options = new MemoryModelOptions
        {
            SmallModelName = "small-tier",
            LargeModelName = "large-tier"
        };

        Assert.AreEqual("operation-specific",
            options.ResolveModelForCall(LlmModelTier.Small, "operation-specific"));
    }

    [TestMethod]
    public void ResolveModelForCall_UsesTierOverridesForDefaultNames()
    {
        var options = new MemoryModelOptions
        {
            SmallModelName = "small-tier",
            LargeModelName = "large-tier"
        };

        Assert.AreEqual("small-tier", options.ResolveModelForCall(LlmModelTier.Small, "default"));
        Assert.AreEqual("large-tier", options.ResolveModelForCall(LlmModelTier.Large, "default"));
    }

    [TestMethod]
    public void ResolveModelForCall_BlankNameFallsBackToDefault()
    {
        var options = new MemoryModelOptions();

        Assert.AreEqual("default", options.ResolveModelForCall(LlmModelTier.Default, " "));
    }
}

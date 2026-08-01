using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryTaxonomyRulesTests
{
    private static readonly HashSet<MemoryType> AllTypes = [.. Enum.GetValues<MemoryType>()];

    [TestMethod]
    public void Validate_AllowsEveryConfiguredMemoryType()
    {
        foreach (var type in AllTypes)
        {
            var (isValid, reason) = MemoryTaxonomyRules.Validate(type, "durable content", AllTypes);
            Assert.IsTrue(isValid, reason);
            Assert.IsNull(reason);
        }
    }

    [TestMethod]
    public void Validate_RejectsDisallowedTypeAndListsAllowedTypes()
    {
        var allowed = new HashSet<MemoryType> { MemoryType.Fact, MemoryType.Rule };

        var (isValid, reason) = MemoryTaxonomyRules.Validate(
            MemoryType.Observation, "content", allowed);

        Assert.IsFalse(isValid);
        StringAssert.Contains(reason, nameof(MemoryType.Observation));
        StringAssert.Contains(reason, nameof(MemoryType.Fact));
        StringAssert.Contains(reason, nameof(MemoryType.Rule));
    }
}

using System.Text.Json;
using FabrCore.Core.Acl;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class PrincipalDescriptionTests
{
    [TestMethod]
    public void PrincipalMetadata_RoundTripsThroughOrleansAndJson()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var principal = new AclPrincipal { Handle = "support", DisplayName = "Support", Description = "Customer support workflows", Roles = ["reader"] };
        var copy = serializer.Deserialize<AclPrincipal>(serializer.SerializeToArray(principal));
        Assert.IsNotNull(copy);
        Assert.AreEqual(principal.Description, copy.Description);
        Assert.AreEqual(principal.Handle, copy.Handle);
        var jsonCopy = JsonSerializer.Deserialize<AclPrincipal>(JsonSerializer.Serialize(copy, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        Assert.AreEqual(principal.Description, jsonCopy.Description);
        CollectionAssert.AreEqual(principal.Roles, jsonCopy.Roles);
        Assert.IsNull(JsonSerializer.Deserialize<AclPrincipal>("{\"handle\":\"legacy\"}", JsonSerializerOptions.Web)!.Description);
    }
}

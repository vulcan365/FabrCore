using FabrCore.Services.Memory.Administration;
using FabrCore.Services.Memory.Administration.Models;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryContractAssemblyTests
{
    [TestMethod]
    public void SharedAdministrationTypesAreOwnedByContractsAssembly()
    {
        var serviceAssembly = typeof(MemoryServiceExtensions).Assembly;
        var contractAssembly = typeof(IMemoryAdminService).Assembly;

        Assert.AreEqual("FabrCore.Services.Contracts", contractAssembly.GetName().Name);
        Assert.AreNotSame(serviceAssembly, contractAssembly);
        Assert.AreSame(contractAssembly, typeof(AdminMemoryDashboardStats).Assembly);
        Assert.AreSame(contractAssembly, typeof(MemoryAuditEntry).Assembly);
        Assert.AreSame(contractAssembly, typeof(MemoryEntry).Assembly);
        Assert.AreSame(contractAssembly, typeof(MemoryType).Assembly);
        Assert.IsEmpty(serviceAssembly.GetForwardedTypes());
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class DataProtectionTests
{
    internal static ServiceProvider Services(Dictionary<string, string?>? values = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build());
        services.AddFabrCoreDataProtection();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("Localhost", "Auto")]
    [DataRow("Localhost", "Ephemeral")]
    public void LocalhostDefaultsAreSingletonEphemeralAndDoNotConfigureAppCookies(string? clustering, string? mode)
    {
        Dictionary<string, string?> config = new()
        {
            ["FabrCore:Orleans:ClusteringMode"] = clustering,
            ["FabrCore:DataProtection:Mode"] = mode
        };
        using var first = Services(config); using var restarted = Services(config);
        var provider = first.GetRequiredService<IFabrCoreDataProtectionProvider>();
        Assert.AreSame(provider, first.GetRequiredService<IFabrCoreDataProtectionProvider>());
        Assert.IsNull(first.GetService<IDataProtectionProvider>());
        var protectedValue = provider.CreateProtector("test").Protect("secret");
        Assert.AreEqual("secret", provider.CreateProtector("test").Unprotect(protectedValue));
        Assert.ThrowsExactly<CryptographicException>(() => restarted.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
    }

    [TestMethod]
    public void DefaultApplicationCookieProtectionDoesNotReplaceLocalhostDefaults()
    {
        using var services = Services(configure: s => s.AddDataProtection());
        var application = services.GetRequiredService<IDataProtectionProvider>();
        var credentials = services.GetRequiredService<IFabrCoreDataProtectionProvider>();
        var ciphertext = credentials.CreateProtector("test").Protect("secret");
        Assert.AreEqual("secret", credentials.CreateProtector("test").Unprotect(ciphertext));
        Assert.ThrowsExactly<CryptographicException>(() => application.CreateProtector("test").Unprotect(ciphertext));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("SqlServer")]
    [DataRow("Localhost")]
    public void IntegratedSqlAutoModeRequiresEncryptionRegardlessOfClustering(string? clustering)
    {
        using var services = Services(new()
        {
            ["ConnectionStrings:FabrCore"] = "Server=unused;Database=unused",
            ["FabrCore:Orleans:ClusteringMode"] = clustering
        });
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => services.GetRequiredService<IFabrCoreDataProtectionProvider>());
        StringAssert.Contains(error.Message, "CertificatePath");
    }

    [TestMethod]
    public void OrleansOnlySqlAutoModeRequiresEncryption()
    {
        using var services = Services(new()
        {
            ["FabrCore:Orleans:ClusteringMode"] = "SqlServer",
            ["FabrCore:Orleans:ConnectionString"] = "Server=unused;Database=unused"
        });
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => services.GetRequiredService<IFabrCoreDataProtectionProvider>());
        StringAssert.Contains(error.Message, "CertificatePath");
    }

    [TestMethod]
    public void IntegratedSqlRejectsEphemeralEvenWithLocalhostClustering()
    {
        using var services = Services(new()
        {
            ["ConnectionStrings:FabrCore"] = "Server=unused;Database=unused",
            ["FabrCore:Orleans:ClusteringMode"] = "Localhost",
            ["FabrCore:DataProtection:Mode"] = "Ephemeral"
        });
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => services.GetRequiredService<IFabrCoreDataProtectionProvider>());
        StringAssert.Contains(error.Message, "Ephemeral protection cannot be used with durable storage");
    }

    [TestMethod]
    [DataRow("AzureStorage")]
    [DataRow("SqlServer")]
    public void DurableModesNeverSilentlySelectEphemeralKeys(string mode)
    {
        using var services = Services(new() { ["FabrCore:Orleans:ClusteringMode"] = mode, ["FabrCore:DataProtection:Mode"] = "Ephemeral" });
        Assert.ThrowsExactly<InvalidOperationException>(() => services.GetRequiredService<IFabrCoreDataProtectionProvider>());
    }

    [TestMethod]
    public void ExplicitApplicationProviderIsPreservedInCustomMode()
    {
        var external = new EphemeralDataProtectionProvider();
        using var services = Services(new() { ["FabrCore:DataProtection:Mode"] = "Custom" }, s => s.AddSingleton<IDataProtectionProvider>(external));
        var ciphertext = external.CreateProtector("test").Protect("external");
        Assert.AreEqual("external", services.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("test").Unprotect(ciphertext));
        Assert.AreSame(external, services.GetRequiredService<IDataProtectionProvider>());
    }

    [TestMethod]
    public void AnUnencryptedExplicitRepositoryIsRejectedByAutoMode()
    {
        using var services = Services(configure: s => s.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.GetTempPath())));
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => services.GetRequiredService<IFabrCoreDataProtectionProvider>());
        StringAssert.Contains(error.Message, "encryption at rest");
    }

    [TestMethod]
    public void AutoPreservesAnExistingEncryptedRepositoryAndApplicationIdentity()
    {
        using var certificate = Certificate();
        var directory = Directory.CreateTempSubdirectory("fabrcore-custom-protection-");
        try
        {
            using var services = Services(configure: s => s.AddDataProtection().SetApplicationName("existing-application")
                .PersistKeysToFileSystem(directory).ProtectKeysWithCertificate(certificate));
            var external = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("existing-purpose");
            var ciphertext = external.Protect("existing-data");
            Assert.AreEqual("existing-data", services.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("existing-purpose").Unprotect(ciphertext));
        }
        finally { foreach (var file in directory.GetFiles()) file.Delete(); directory.Delete(); }
    }

    [TestMethod]
    public void RegistrationDoesNotActivateProtectionForDisabledConsumers()
    {
        using var services = Services(new() { ["ConnectionStrings:FabrCore"] = "Server=unused;Database=unused" });
        // No certificate or SQL connection is required until a feature resolves protection.
        Assert.IsNull(services.GetService<IDataProtectionProvider>());
        Assert.AreEqual(0, services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Count());
    }

    internal static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FabrCore protection test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }
}

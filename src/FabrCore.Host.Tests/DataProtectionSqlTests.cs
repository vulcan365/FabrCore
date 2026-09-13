using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize, TestCategory("SqlMode")]
public sealed class DataProtectionSqlTests
{
    [TestMethod]
    public async Task AutoSqlKeysAreEncryptedSharedRestartSafeAndScoped()
    {
        var password = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(password)) Assert.Inconclusive("An isolated SQL test server is required.");
        var settings = new SqlConnectionStringBuilder { DataSource = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_SERVER") ?? "localhost", InitialCatalog = "master", UserID = "sa", Password = password, TrustServerCertificate = true };
        await using var admin = new SqlConnection(settings.ConnectionString); await admin.OpenAsync();
        var name = "FabrCoreProtection_" + Guid.NewGuid().ToString("N");
        var directory = Directory.CreateTempSubdirectory("fabrcore-protection-");
        var path = Path.Combine(directory.FullName, "key.pfx");
        var nextPath = Path.Combine(directory.FullName, "next.pfx");
        using var certificate = DataProtectionTests.Certificate();
        using var nextCertificate = DataProtectionTests.Certificate();
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx));
        await File.WriteAllBytesAsync(nextPath, nextCertificate.Export(X509ContentType.Pfx));
        await using (var create = new SqlCommand($"CREATE DATABASE [{name}]", admin)) await create.ExecuteNonQueryAsync();
        settings.InitialCatalog = name;
        try
        {
            Dictionary<string, string?> config = new() {
                ["ConnectionStrings:FabrCore"] = settings.ConnectionString,
                ["FabrCore:Orleans:ClusteringMode"] = "Localhost",
                ["FabrCore:DataProtection:CertificatePath"] = path,
                ["FabrCore:DataProtection:ApplicationName"] = "cluster-one"
            };
            string ciphertext;
            using (var first = DataProtectionTests.Services(config))
                ciphertext = first.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("connections").Protect("persisted-token");
            config["FabrCore:Database:AutoInitialize"] = "false";
            using (var restarted = DataProtectionTests.Services(config))
                Assert.AreEqual("persisted-token", restarted.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("connections").Unprotect(ciphertext));
            // A different application has its own keys even in the same database.
            config["FabrCore:DataProtection:ApplicationName"] = "cluster-two";
            using (var other = DataProtectionTests.Services(config))
                Assert.ThrowsExactly<CryptographicException>(() => other.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("connections").Unprotect(ciphertext));
            config["FabrCore:DataProtection:ApplicationName"] = "cluster-one";
            config["FabrCore:DataProtection:CertificatePath"] = nextPath;
            config["FabrCore:DataProtection:PreviousCertificates:0:Path"] = path;
            using (var rotated = DataProtectionTests.Services(config))
                Assert.AreEqual("persisted-token", rotated.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("connections").Unprotect(ciphertext));
            await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
            await using var read = new SqlCommand("SELECT Xml FROM fabrOps.DataProtectionKey", connection);
            await using (var reader = await read.ExecuteReaderAsync())
            {
                Assert.IsTrue(await reader.ReadAsync());
                do { var xml = reader.GetString(0); StringAssert.Contains(xml, "encryptedSecret"); Assert.IsFalse(xml.Contains("persisted-token")); Assert.IsFalse(xml.Contains("<masterKey")); } while (await reader.ReadAsync());
            }
            await using (var dropTable = new SqlCommand("DROP TABLE fabrOps.DataProtectionKey", connection)) await dropTable.ExecuteNonQueryAsync();
            using var missingSchema = DataProtectionTests.Services(config);
            Assert.ThrowsExactly<SqlException>(() => missingSchema.GetRequiredService<IFabrCoreDataProtectionProvider>());
            await using (var migration = new SqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "data-protection.sql")), connection))
                await migration.ExecuteNonQueryAsync();
            using var manuallyProvisioned = DataProtectionTests.Services(config);
            var manual = manuallyProvisioned.GetRequiredService<IFabrCoreDataProtectionProvider>().CreateProtector("manual-schema");
            Assert.AreEqual("works", manual.Unprotect(manual.Protect("works")));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var drop = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", admin); await drop.ExecuteNonQueryAsync();
            File.Delete(path); File.Delete(nextPath); directory.Delete();
        }
    }
}

using System.Security.Cryptography.X509Certificates;
using FabrCore.Host.Configuration;
using FabrCore.Host.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Security;

/// <summary>Host-owned protection for FabrCore credentials, independent of application cookie protection.</summary>
public interface IFabrCoreDataProtectionProvider : IDataProtectionProvider { }

public static class FabrCoreDataProtectionExtensions
{
    /// <summary>Registers lazy host defaults. Consumers activate validation only when protection is needed.</summary>
    public static IServiceCollection AddFabrCoreDataProtection(this IServiceCollection services)
    {
        services.TryAddSingleton<IFabrCoreDataProtectionProvider>(Create);
        return services;
    }

    private static IFabrCoreDataProtectionProvider Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var section = configuration.GetSection("FabrCore:DataProtection");
        var mode = section["Mode"] ?? "Auto";
        if (mode is not ("Auto" or "Ephemeral" or "SqlServer" or "Custom"))
            throw new InvalidOperationException("FabrCore:DataProtection:Mode must be Auto, Ephemeral, SqlServer, or Custom.");

        var supplied = services.GetService<IOptions<KeyManagementOptions>>()?.Value;
        if (mode == "Custom" || (mode == "Auto" && supplied?.XmlRepository is not null))
        {
            if (mode == "Auto" && supplied?.XmlEncryptor is null)
                throw new InvalidOperationException("The supplied Data Protection repository must also configure key encryption at rest.");
            // Preserve a deliberately configured external repository and its application discriminator.
            // Custom providers own their durability and key-encryption configuration.
            var provider = services.GetService<IDataProtectionProvider>()
                ?? throw new InvalidOperationException("Custom protection requires an IDataProtectionProvider registration.");
            return new Provider(provider);
        }

        var database = services.GetService<FabrCoreDatabaseOptions>() ?? FabrCoreDatabaseOptions.Resolve(configuration);
        var orleans = configuration.GetSection(OrleansClusterOptions.SectionName).Get<OrleansClusterOptions>() ?? new();
        var useSql = mode == "SqlServer" || database.Enabled || orleans.ClusteringMode == ClusteringMode.SqlServer;
        if (mode == "Ephemeral" && (useSql || orleans.ClusteringMode != ClusteringMode.Localhost))
            throw new InvalidOperationException("Ephemeral protection cannot be used with durable storage. Configure shared protection instead.");
        if (!useSql)
        {
            if (orleans.ClusteringMode != ClusteringMode.Localhost)
                throw new InvalidOperationException("This persistence provider requires FabrCore:DataProtection:Mode=Custom and shared protection.");
            var storage = services.GetKeyedService<Orleans.Storage.IGrainStorage>(FabrCoreOrleansConstants.StorageProviderName);
            var entities = services.GetService<FabrCore.Host.Services.IUserScopedFabrCoreStorageProvider>();
            if ((storage is not null && storage is not Orleans.Storage.MemoryGrainStorage)
                || (entities is not null && entities is not FabrCore.Host.Services.OrleansEntityStorageProvider))
                throw new InvalidOperationException("Custom persistence requires explicitly configured shared credential protection.");
            return new Provider(new EphemeralDataProtectionProvider());
        }

        var certificatePath = section["CertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath))
            throw new InvalidOperationException("SQL credential protection requires FabrCore:DataProtection:CertificatePath, or a custom shared encrypted provider.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, section["CertificatePassword"]);
        ServiceProvider? ownedServices = null;
        var certificates = new List<X509Certificate2> { certificate };
        try
        {
            if (!certificate.HasPrivateKey) throw new InvalidOperationException("The protection certificate must include its private key.");
            foreach (var previous in section.GetSection("PreviousCertificates").GetChildren())
                certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(previous["Path"] ?? throw new InvalidOperationException("Previous certificate path is required."), previous["Password"]));
            var application = section["ApplicationName"] ?? $"FabrCore:{orleans.ServiceId}:{orleans.ClusterId}:{services.GetService<IHostEnvironment>()?.EnvironmentName ?? "Production"}";
            if (string.IsNullOrWhiteSpace(application) || application.Length > 256)
                throw new InvalidOperationException("The protection application name must contain 1–256 characters.");
            var connection = database.Enabled
                ? configuration.GetConnectionString(database.OperationsConnectionStringName ?? database.ConnectionStringName)
                : orleans.EffectiveStorageConnectionString;
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("SQL protection requires a database connection.");
            var repository = new SqlDataProtectionRepository(connection, application);
            repository.Initialize(database.Enabled ? database.AutoInitialize : orleans.AutoInitDatabase);
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddDataProtection().SetApplicationName(application)
                .ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificates.ToArray());
            registrations.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
            ownedServices = registrations.BuildServiceProvider();
            return new Provider(ownedServices.GetRequiredService<IDataProtectionProvider>(), ownedServices, certificates);
        }
        catch
        {
            ownedServices?.Dispose();
            foreach (var item in certificates) item.Dispose();
            throw;
        }
    }

    private sealed class Provider(IDataProtectionProvider provider, ServiceProvider? owned = null, List<X509Certificate2>? certificates = null)
        : IFabrCoreDataProtectionProvider, IDisposable
    {
        public IDataProtector CreateProtector(string purpose) => provider.CreateProtector(purpose);
        public void Dispose()
        {
            owned?.Dispose();
            if (certificates is not null) foreach (var certificate in certificates) certificate.Dispose();
        }
    }
}

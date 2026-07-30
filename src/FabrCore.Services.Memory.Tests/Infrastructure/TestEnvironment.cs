using System.Text.Json;
using FabrCore.Core;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.Memory.Tests.Infrastructure;

internal static class TestEnvironment
{
    public const string ConnectionStringName = "MemoryTestDb";
    public const int EmbeddingDimensions = 1536;

    public static string RequireDatabaseConnectionString()
    {
        var complete = Environment.GetEnvironmentVariable("FABRCORE_MEMORY_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(complete))
            return complete;

        var password = Environment.GetEnvironmentVariable("FABRCORE_MEMORY_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            Assert.Inconclusive(
                "Set FABRCORE_MEMORY_TEST_CONNECTION_STRING or FABRCORE_MEMORY_TEST_PASSWORD to run SQL integration tests.");
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("FABRCORE_MEMORY_TEST_SERVER") ?? "localhost",
            InitialCatalog = Environment.GetEnvironmentVariable("FABRCORE_MEMORY_TEST_DATABASE") ?? "fabrcore-testing",
            UserID = Environment.GetEnvironmentVariable("FABRCORE_MEMORY_TEST_USER") ?? "fabrcore365",
            Password = password,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;
    }

    public static FabrCoreConfiguration RequireLiveModelConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fabrcore.json");
        if (!File.Exists(path))
            Assert.Inconclusive("fabrcore.json was not copied to the test output directory.");

        var config = JsonSerializer.Deserialize<FabrCoreConfiguration>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config is null || config.ModelConfigurations.Count == 0)
            Assert.Inconclusive("fabrcore.json has no model configurations.");

        if (config.ApiKeys.Any(k => string.IsNullOrWhiteSpace(k.Value) ||
                                    k.Value.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase) ||
                                    k.Value.Contains("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Inconclusive("fabrcore.json contains an unconfigured API key.");
        }

        return config;
    }

    public static string NewScope(string prefix) => $"tests:{prefix}:{Guid.NewGuid():N}";
}

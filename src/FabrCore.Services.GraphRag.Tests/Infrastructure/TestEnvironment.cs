using System.Text.Json;
using FabrCore.Core;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.GraphRag.Tests.Infrastructure;

internal static class TestEnvironment
{
    public const string ConnectionStringName = "GraphRagTestDb";
    public const int EmbeddingDimensions = 1536;

    public static string RequireDatabaseConnectionString()
    {
        var complete = Environment.GetEnvironmentVariable("FABRCORE_GRAPHRAG_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(complete))
            return complete;

        var password = Environment.GetEnvironmentVariable("FABRCORE_GRAPHRAG_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            Assert.Inconclusive("Set FABRCORE_GRAPHRAG_TEST_CONNECTION_STRING or FABRCORE_GRAPHRAG_TEST_PASSWORD to run SQL tests.");

        return new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("FABRCORE_GRAPHRAG_TEST_SERVER") ?? "localhost",
            InitialCatalog = Environment.GetEnvironmentVariable("FABRCORE_GRAPHRAG_TEST_DATABASE") ?? "fabrcore-testing",
            UserID = Environment.GetEnvironmentVariable("FABRCORE_GRAPHRAG_TEST_USER") ?? "fabrcore365",
            Password = password,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;
    }

    public static FabrCoreConfiguration RequireLiveModelConfiguration()
    {
        var path = Environment.GetEnvironmentVariable("FABRCORE_GRAPHRAG_TEST_CONFIG")
                   ?? Path.Combine(AppContext.BaseDirectory, "fabrcore.json");
        if (!File.Exists(path))
            Assert.Inconclusive("Add fabrcore.json to the GraphRAG test project to run live evaluations.");

        var configuration = JsonSerializer.Deserialize<FabrCoreConfiguration>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (configuration is null || configuration.ModelConfigurations.Count == 0)
            Assert.Inconclusive("fabrcore.json has no model configurations.");
        if (configuration.ApiKeys.Any(k => string.IsNullOrWhiteSpace(k.Value)
                                           || k.Value.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase)))
            Assert.Inconclusive("fabrcore.json contains an unconfigured API key.");

        return configuration;
    }

    public static string NewScope(string prefix) => $"tests:grag:{prefix}:{Guid.NewGuid():N}";
}

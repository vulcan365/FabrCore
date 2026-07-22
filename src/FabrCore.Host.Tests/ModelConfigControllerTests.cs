using FabrCore.Host.Api.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class ModelConfigControllerTests
{
    [TestMethod]
    public async Task GetModelConfig_RoundTripsReasoningEffort()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"fabrcore-model-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(contentRoot, "fabrcore.json"),
                """
                {
                  "ModelConfigurations": [
                    {
                      "Name": "graphrag",
                      "Provider": "Azure",
                      "Uri": "https://azure.test",
                      "Model": "gpt-test",
                      "ApiKeyAlias": "test-key",
                      "MaxOutputTokens": 1000,
                      "ReasoningEffort": "none"
                    }
                  ]
                }
                """);

            var environment = new TestWebHostEnvironment(contentRoot);
            var controller = new ModelConfigController(
                NullLogger<ModelConfigController>.Instance,
                environment);

            var result = await controller.GetModelConfig("graphrag");

            var ok = result as OkObjectResult;
            Assert.IsNotNull(ok);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            Assert.AreEqual("none", document.RootElement.GetProperty("ReasoningEffort").GetString());
            Assert.AreEqual(1000, document.RootElement.GetProperty("MaxOutputTokens").GetInt32());
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "FabrCore.Host.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}

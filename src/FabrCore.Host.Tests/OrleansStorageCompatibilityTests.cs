using FabrCore.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Storage;

namespace FabrCore.Host.Tests;

/// <summary>Legacy-shaped JSON fixtures exercise the production storage serializer without starting a silo.</summary>
[TestClass]
public class OrleansStorageCompatibilityTests
{
    private static IHost CreateHost(Action<IServiceCollection>? configure = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseOrleans(silo =>
        {
            silo.UseLocalhostClustering();
            silo.AddFabrCore([]);
        });
        builder.ConfigureServices(services => configure?.Invoke(services));
        return builder.Build();
    }

    [TestMethod]
    public void LegacyAgentState_WithTypeMetadata_RestoresConversationAndConfiguration()
    {
        using var host = CreateHost();
        Assert.IsFalse(host.Services.GetRequiredService<IOptions<OrleansJsonSerializerOptions>>().Value.AllowAllTypes);
        var serializer = host.Services.GetRequiredService<IGrainStorageSerializer>();
        var json = """
            {
              "$type": "FabrCore.Core.AgentGrainState, FabrCore.Core",
              "Configuration": {
                "$type": "FabrCore.Core.AgentConfiguration, FabrCore.Core",
                "Handle": "assistant", "AgentType": "support", "SystemPrompt": "Help the user",
                "Args": { "tenant": "example" }, "Plugins": ["memory"], "Tools": ["search"]
              },
              "MessageThreads": { "thread-1": [
                { "$type": "FabrCore.Core.StoredChatMessage, FabrCore.Core",
                  "Id": "message-1", "Role": "assistant", "ContentsJson": "[{\"text\":\"hello\"}]",
                  "Timestamp": "2026-06-01T12:00:00Z" }
              ] },
              "LastModified": "2026-06-01T12:00:00Z"
            }
            """;
        var state = serializer.Deserialize<AgentGrainState>(new BinaryData(json))!;
        Assert.AreEqual("example", state.Configuration!.Args["tenant"]);
        Assert.AreEqual("[{\"text\":\"hello\"}]", state.MessageThreads["thread-1"][0].ContentsJson);
        state.CustomState["progress"] = System.Text.Json.JsonSerializer.SerializeToElement(new { step = 3 });
        var restored = serializer.Deserialize<AgentGrainState>(serializer.Serialize(state))!;
        Assert.AreEqual("message-1", restored.MessageThreads["thread-1"][0].Id);
        Assert.AreEqual("support", restored.Configuration!.AgentType);
        Assert.AreEqual(3, restored.CustomState["progress"].GetProperty("step").GetInt32());
    }

    [TestMethod]
    public void LegacyPrincipalState_RestoresTrackedAgentsAndPendingMessages()
    {
        using var host = CreateHost();
        var serializer = host.Services.GetRequiredService<IGrainStorageSerializer>();
        var state = serializer.Deserialize<PrincipalGrainState>(new BinaryData("""
            {
              "$type": "FabrCore.Core.PrincipalGrainState, FabrCore.Core",
              "TrackedAgents": { "assistant": {
                "$type": "FabrCore.Core.TrackedAgentInfo, FabrCore.Core",
                "Handle": "assistant", "AgentType": "support"
              } },
              "PendingMessages": [ {
                "$type": "FabrCore.Core.AgentMessage, FabrCore.Core", "Id": "pending-1", "ToHandle": "user"
              } ],
              "PendingMessagesPersisted": "2026-06-01T12:00:00Z"
            }
            """))!;
        Assert.AreEqual("support", state.TrackedAgents["assistant"].AgentType);
        Assert.AreEqual("pending-1", state.PendingMessages[0].Id);
        var restored = serializer.Deserialize<PrincipalGrainState>(serializer.Serialize(state))!;
        Assert.AreEqual(state.PendingMessagesPersisted, restored.PendingMessagesPersisted);
        Assert.AreEqual("pending-1", restored.PendingMessages[0].Id);
    }

    [TestMethod]
    public void ApplicationDefinedState_RequiresExplicitTypeRegistration()
    {
        var payload = new BinaryData("""
            { "$type": "FabrCore.Host.Tests.OrleansStorageCompatibilityTests+ApplicationState, FabrCore.Host.Tests", "Value": "saved" }
            """);
        using (var host = CreateHost())
        {
            Assert.Throws<JsonSerializationException>(() =>
                host.Services.GetRequiredService<IGrainStorageSerializer>().Deserialize<object>(payload));
        }

        using var configuredHost = CreateHost(services => services.Configure<TypeManifestOptions>(options =>
            options.AddAllowedType(typeof(ApplicationState))));
        var result = configuredHost.Services.GetRequiredService<IGrainStorageSerializer>().Deserialize<object>(payload);
        Assert.AreEqual("saved", ((ApplicationState)result!).Value);
    }

    public sealed class ApplicationState
    {
        public string Value { get; set; } = "";
    }

    [TestMethod]
    [DataRow("{\"step\":3,\"nested\":[true,null,\"value\"]}")]
    [DataRow("[1,2,3]")]
    [DataRow("\"2026-06-01T12:00:00Z\"")]
    [DataRow("{\"date\":\"2026-06-01T12:00:00.1234567+05:30\"}")]
    [DataRow("3.125")]
    [DataRow("true")]
    [DataRow("null")]
    public void CustomJsonState_PreservesValues(string json)
    {
        using var host = CreateHost();
        var serializer = host.Services.GetRequiredService<IGrainStorageSerializer>();
        var value = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        var state = new AgentGrainState { CustomState = new() { ["value"] = value } };
        var restored = serializer.Deserialize<AgentGrainState>(serializer.Serialize(state))!;
        Assert.IsTrue(System.Text.Json.JsonElement.DeepEquals(value, restored.CustomState["value"]));
    }
}

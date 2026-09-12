using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Host.Configuration;
using FabrCore.Host.Database;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.GraphRag;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize]
public class DatabaseModeTests
{
    [TestMethod]
    public void DatabaseSelection_IsExplicitAndNeverFallsBackOnInvalidConfiguration()
    {
        Assert.IsFalse(FabrCoreDatabaseOptions.Resolve(new ConfigurationBuilder().Build()).Enabled);
        foreach (var values in new[] {
            new Dictionary<string,string?> { ["ConnectionStrings:FabrCore"]="" },
            new Dictionary<string,string?> { ["FabrCore:Database:ConnectionStringName"]="missing" },
            new Dictionary<string,string?> { ["FabrCore:Acl:Seed:Principals:0:Handle"]="alice" } })
            Assert.ThrowsExactly<InvalidOperationException>(()=>FabrCoreDatabaseOptions.Resolve(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:FabrCore"]="Server=example;Database=fabr" }).Build();
        var database=FabrCoreDatabaseOptions.Resolve(config);
        var orleans=new OrleansClusterOptions();
        database.ApplyOrleansDefaults(orleans,config,null);
        Assert.IsTrue(database.Enabled);
        Assert.AreEqual(ClusteringMode.SqlServer,orleans.ClusteringMode);
        Assert.AreEqual(config.GetConnectionString("FabrCore"),orleans.ConnectionString);
        config["FabrCore:Orleans:ClusteringMode"]="Localhost";
        var explicitLocal=new OrleansClusterOptions();
        database.ApplyOrleansDefaults(explicitLocal,config,null);
        Assert.AreEqual(ClusteringMode.Localhost,explicitLocal.ClusteringMode);
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public async Task Standalone_StartsAndChatsWithoutAclOrDatabase(string environment)
    {
        await using var app = BuildHost(environment);
        await app.StartAsync();
        try
        {
            Assert.IsFalse(app.Services.GetRequiredService<FabrCoreFeatureState>().DatabaseEnabled);
            Assert.IsNull(app.Services.GetService<IAclEntityStore>());
            Assert.IsNull(app.Services.GetService<SqlAclRepository>());
            Assert.IsNull(app.Services.GetService<IAgentMemoryProvider>());
            Assert.IsFalse(app.Services.GetServices<IHostedService>().Any(s=>s is DatabaseSchemaHostedService or GrainBackedAclEntityStore));
            Assert.AreEqual(AclOutcome.DisabledBypass,app.Services.GetRequiredService<IAclEvaluator>().CanSendMessage("alice",null,"bob:echo").Outcome);
            var registry=app.Services.GetRequiredService<IFabrCoreRegistry>();
            Assert.IsFalse(registry.GetAgentTypes().Any(r=>r.TypeName.Contains("GraphRag")));
            Assert.IsFalse(registry.GetPlugins().Any(r=>r.Aliases.Contains("agent-memory")));
            Assert.ThrowsExactly<InvalidOperationException>(()=>registry.FindAgentType("graph-rag-search-agent"));
            var agents=app.Services.GetRequiredService<IFabrCoreAgentService>();
            var health=await agents.ConfigureAgentAsync("bob",new() {Handle="echo",AgentType="database-mode-echo"});
            Assert.AreEqual(HealthState.Healthy,health.State,JsonSerializer.Serialize(health));
            var response=await agents.SendAndReceiveMessageAsync("alice","bob:echo","hello");
            Assert.AreEqual("hello",response.Message);
            using var client=app.GetTestClient();
            Assert.AreEqual(HttpStatusCode.Unauthorized,(await client.GetAsync("/fabrcoreapi/admin/v1/status")).StatusCode);
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","test-admin");
            Assert.AreEqual(HttpStatusCode.OK,(await client.GetAsync("/fabrcoreapi/admin/v1/status")).StatusCode);
            foreach (var route in new[] {"/fabrcoreapi/acl/principals","/fabrcoreapi/admin/v1/access","/fabrcoreapi/memory/admin/v1/dashboard","/fabrcoreapi/graphrag/admin/v1/dashboard"})
                Assert.AreEqual(HttpStatusCode.NotFound,(await client.GetAsync(route)).StatusCode,route);
            var first=await client.GetStringAsync("/fabrcoreapi/capabilities");
            Assert.AreEqual(first,await client.GetStringAsync("/fabrcoreapi/admin/v1/capabilities"));
            Assert.IsFalse(first.Contains("\"acl\""));
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public void AclImport_ValidatesReferencesAndPreservesGrantIds()
    {
        using var json=JsonDocument.Parse("""{"Seed":{"Principals":[{"Handle":"alice","Description":"Support identity","Roles":["reader"]}],"Roles":[{"Name":"reader"}],"Grants":[{"Subject":"principal:alice","Permission":"agent.read.allow","Resource":"bob:*"}]}}""");
        var incoming=AclMigration.Read(json.RootElement);
        var id=incoming.Grants[0].Id;
        var merged=AclMigration.MergeIntoEmpty(new(),incoming,new());
        Assert.AreEqual(id,merged.Grants[0].Id);
        Assert.AreEqual("Support identity",merged.Principals[0].Description);
        incoming.Principals[0].Roles.Add("missing");
        Assert.ThrowsExactly<ArgumentException>(()=>AclMigration.MergeIntoEmpty(new(),incoming,new()));
        Assert.ThrowsExactly<InvalidOperationException>(()=>AclMigration.MergeIntoEmpty(merged,new(),new()));
    }

    [TestMethod, TestCategory("SqlMode")]
    public async Task SqlMode_InitializesAndPersistsAcrossRestart()
    {
        var password=Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_PASSWORD");
        if (string.IsNullOrEmpty(password)) Assert.Inconclusive("Set FABRCORE_SQL_TEST_PASSWORD for isolated SQL mode integration tests.");
        var master=new SqlConnectionStringBuilder {DataSource=Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_SERVER")??"localhost",InitialCatalog="master",UserID="sa",Password=password,TrustServerCertificate=true};
        var database="FabrCoreModeTest_"+Guid.NewGuid().ToString("N");
        await using var admin=new SqlConnection(master.ConnectionString);
        await admin.OpenAsync();
        await using(var create=new SqlCommand($"CREATE DATABASE [{database}]",admin)) await create.ExecuteNonQueryAsync();
        master.InitialCatalog=database;
        try
        {
            for(var run=0;run<2;run++)
            {
                await using var app=BuildHost("Development",master.ConnectionString);
                await app.StartAsync();
                try
                {
                    Assert.IsTrue(app.Services.GetRequiredService<FabrCoreFeatureState>().DatabaseEnabled);
                    Assert.IsTrue(app.Services.GetRequiredService<GrainBackedAclEntityStore>().IsReady);
                    Assert.IsNotNull(app.Services.GetRequiredService<IAgentMemoryProvider>());
                    Assert.IsInstanceOfType<SqlAuditProvider>(app.Services.GetRequiredService<FabrCore.Core.Auditing.IAuditProvider>());
                    Assert.IsInstanceOfType<SqlVerifiableExecutionStore>(app.Services.GetRequiredService<FabrCore.Core.VerifiableExecution.IVerifiableExecutionStore>());
                    Assert.IsInstanceOfType<SqlA2ATaskStore>(app.Services.GetRequiredService<FabrCore.Host.A2A.IA2ATaskStore>());
                    var acl=app.Services.GetRequiredService<IAclEntityStore>();
                    var storage=app.Services.GetRequiredService<IUserScopedFabrCoreStorageProvider>();
                    if(run==0)
                    {
                        await acl.UpsertPrincipalAsync(new() {Handle="alice", DisplayName="Alice", Description="Support identity"});
                        await acl.UpsertGrantAsync(new() {Id="test-grant",Subject=new(SubjectKind.Principal,"alice"),Permission="agent.read.allow",Resource="bob:*"});
                        await storage.UpsertAsync("alice","mode-test","key","persisted");
                    }
                    else
                    {
                        Assert.AreEqual("Support identity",(await acl.GetPrincipalAsync("alice"))!.Description);
                        Assert.AreEqual("test-grant",(await acl.GetGrantAsync("test-grant"))!.Id);
                        Assert.AreEqual("persisted",await storage.GetAsync<string>("alice","mode-test","key"));
                    }
                    Assert.IsTrue(app.Services.GetRequiredService<IAclEvaluator>().CanRead("alice","bob:echo").IsAllowed);
                    Assert.IsFalse(app.Services.GetRequiredService<IAclEvaluator>().CanSendMessage("alice",null,"bob:echo").IsAllowed);
                    var repository=app.Services.GetRequiredService<SqlAclRepository>();
                    var snapshot=await repository.LoadAsync();
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>repository.SaveAsync(snapshot,snapshot.Version-1));
                    Assert.AreEqual(snapshot.Version,(await repository.LoadAsync()).Version);
                }
                finally {await app.StopAsync();}
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var drop=new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];",admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static WebApplication BuildHost(string environment,string? connection=null)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {EnvironmentName=environment,ApplicationName=typeof(FabrCoreHostExtensions).Assembly.GetName().Name});
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
            ["FabrCore:AdminAuthentication:ApiKey"]="test-admin",
            ["FabrCore:Host:AllowedWebSocketOrigins:0"]="http://localhost"
        });
        if(connection is not null) builder.Configuration["ConnectionStrings:FabrCore"]=connection;
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies=[typeof(DatabaseModeEchoAgent).Assembly] }.UseConfigurationStore<TestConfigurationStore>());
        var app=builder.Build();
        app.UseFabrCoreServer();
        return app;
    }

    public sealed class TestConfigurationStore : IFabrCoreConfigurationStore
    {
        public bool SupportsWrites=>false;
        public Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new FabrCoreConfiguration {ModelConfigurations=[new() {Name="default",Provider="OpenAI",Uri="https://example.invalid",ApiKeyAlias="",Model="test"},new() {Name="embeddings",Provider="OpenAI",Uri="https://example.invalid",ApiKeyAlias="",Model="test-embeddings"}]});
        public Task SaveConfigurationAsync(FabrCoreConfiguration configuration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}

[AgentAlias("database-mode-echo")]
public sealed class DatabaseModeEchoAgent(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host) : FabrCoreAgentProxy(config, services, host)
{
    public override Task OnInitialize() => Task.CompletedTask;
    public override Task<AgentMessage> OnMessage(AgentMessage message) { var reply = message.Response(); reply.Message = message.Message; return Task.FromResult(reply); }
}


using System.Reflection;
using System.Net;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Administration.Models;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Models;
using FabrCore.Services.GraphRag.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class GraphRagAdminControllerTests
{
    [TestMethod]
    public void AddGraphRagAdministrationRegistersControllerApplicationPart()
    {
        var services = new ServiceCollection();
        services.AddGraphRagAdministration();
        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<ApplicationPartManager>();

        Assert.IsTrue(manager.ApplicationParts.Any(part => part.Name == typeof(GraphRagAdminController).Assembly.GetName().Name));
    }

    [TestMethod]
    public void SharedAdministrationTypesAreOwnedByContractsAssembly()
    {
        var serviceAssembly = typeof(GraphRagServiceExtensions).Assembly;
        var contractAssembly = typeof(IGraphRagAdminService).Assembly;

        Assert.AreEqual("FabrCore.Services.Contracts", contractAssembly.GetName().Name);
        Assert.AreNotSame(serviceAssembly, contractAssembly);
        Assert.AreSame(contractAssembly, typeof(AdminDashboardStats).Assembly);
        Assert.AreSame(contractAssembly, typeof(SourceDocumentDto).Assembly);
        Assert.IsEmpty(serviceAssembly.GetForwardedTypes());
    }

    [TestMethod]
    public async Task CapabilityRequiresResolvedPrincipal()
    {
        using var provider = BuildProvider(AclOutcome.Allow);
        var controller = CreateController(provider);

        var result = await controller.GetCapabilities(null, CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status401Unauthorized, ((ObjectResult)result).StatusCode);
    }

    [TestMethod]
    public async Task CapabilityRejectsPrincipalWithoutReadPermission()
    {
        using var provider = BuildProvider(AclOutcome.NoMatchDeny);
        var controller = CreateController(provider);

        var result = await controller.GetCapabilities("alice", CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
    }

    [TestMethod]
    public async Task CapabilityReturnsVersionedFeaturesForAuthorizedPrincipal()
    {
        using var provider = BuildProvider(AclOutcome.Allow);
        var controller = CreateController(provider);

        var result = await controller.GetCapabilities("alice", CancellationToken.None);

        var ok = (OkObjectResult)result;
        var capability = (GraphRagAdminCapability)ok.Value!;
        Assert.AreEqual(GraphRagAdminAvailability.Available, capability.Availability);
        Assert.AreEqual(GraphRagAdminCapability.CurrentApiVersion, capability.ApiVersion);
        CollectionAssert.Contains(capability.Features.ToList(), "maintenance");
        CollectionAssert.Contains(capability.Features.ToList(), "upload");
    }

    [TestMethod]
    public async Task SearchRejectsUnregisteredScopeBeforeCallingService()
    {
        using var provider = BuildProvider(AclOutcome.Allow);
        var controller = CreateController(provider, new StubScopeService(false));

        var result = await controller.Search(
            "alice",
            new GraphRagSearchRequest("query", ["unknown-scope"], "entities"),
            CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((ObjectResult)result).StatusCode);
    }

    [TestMethod]
    public async Task SuccessfulMutationRecordsPrincipalAndResourceAudit()
    {
        using var provider = BuildProvider(AclOutcome.Allow);
        var audit = new StubGraphRagAuditLog();
        var controller = CreateController(provider, new StubScopeService(true), audit);

        var result = await controller.CreateScope(
            "alice",
            "scope-a",
            new GraphRagScopeWriteRequest("A scope"),
            CancellationToken.None);

        Assert.IsInstanceOfType<OkObjectResult>(result);
        Assert.IsNotNull(audit.LastEntry);
        Assert.AreEqual("AdminScopeCreated", audit.LastEntry.ActionType);
        Assert.AreEqual("alice", audit.LastEntry.ActorId);
        Assert.AreEqual("scope-a", audit.LastEntry.ScopeKey);
    }

    [DataRow(AclOutcome.Allow, false)]
    [DataRow(AclOutcome.NoMatchDeny, true)]
    [TestMethod]
    public async Task LocalAdapterUsesAclWhenPrincipalAccessorIsAvailable(AclOutcome outcome, bool denied)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AclEnforcer(
            new StubAclEvaluator(outcome),
            new StubSecurityAuditProvider(),
            NullLogger<AclEnforcer>.Instance));
        services.AddSingleton<IGraphRagAdminPrincipalAccessor>(new StubPrincipalAccessor("alice"));
        using var provider = services.BuildServiceProvider();
        var client = new AclLocalGraphRagAdminClient(CreateClientProxy(), provider);

        if (denied)
        {
            var exception = await Assert.ThrowsExactlyAsync<GraphRagAdminClientException>(() => client.GetCapabilityAsync());
            Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
        }
        else
        {
            Assert.AreEqual(GraphRagAdminAvailability.Available, (await client.GetCapabilityAsync()).Availability);
        }
    }

    private static ServiceProvider BuildProvider(AclOutcome outcome)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGraphRagAdminClient>(GraphRagAdminClientKeys.Local, CreateClientProxy());
        var evaluator = new StubAclEvaluator(outcome);
        var audit = new StubSecurityAuditProvider();
        services.AddSingleton(new AclEnforcer(evaluator, audit, NullLogger<AclEnforcer>.Instance));
        return services.BuildServiceProvider();
    }

    private static GraphRagAdminController CreateController(
        IServiceProvider provider,
        IKnowledgeScopeService? scopes = null,
        StubGraphRagAuditLog? audit = null)
        => new(provider, scopes ?? new StubScopeService(true), audit ?? new StubGraphRagAuditLog(), NullLogger<GraphRagAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static IGraphRagAdminClient CreateClientProxy()
    {
        var proxy = DispatchProxy.Create<IGraphRagAdminClient, AdminClientProxy>();
        return proxy;
    }

    public class AdminClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                nameof(IGraphRagAdminClient.GetCapabilityAsync) => Task.FromResult(new GraphRagAdminCapability { Availability = GraphRagAdminAvailability.Available }),
                nameof(IGraphRagAdminClient.GetDashboardStatsAsync) => Task.FromResult(new AdminDashboardStats()),
                nameof(IGraphRagAdminClient.CreateScopeAsync) => Task.FromResult(new AdminScopeDto { ScopeKey = (string)args![0]! }),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    private sealed class StubAclEvaluator(AclOutcome outcome) : IAclEvaluator
    {
        public AclEnforcementMode Mode => AclEnforcementMode.Enforce;
        public AclDecision Evaluate(in AclSubjectContext subject, AclAction action, string resourceHandle)
            => new(outcome, Mode, "test");
    }

    private sealed class StubSecurityAuditProvider : IAuditProvider
    {
        public AuditOptions Options { get; } = new();
        public event Action<AuditEvent>? OnAuditEventRecorded;
        public Task RecordAsync(AuditEvent auditEvent) { OnAuditEventRecorded?.Invoke(auditEvent); return Task.CompletedTask; }
        public Task<List<AuditEvent>> GetEventsAsync(AuditQuery? query = null) => Task.FromResult(new List<AuditEvent>());
        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class StubPrincipalAccessor(string? principal) : IGraphRagAdminPrincipalAccessor
    {
        public ValueTask<string?> GetPrincipalIdAsync(CancellationToken ct = default) => ValueTask.FromResult(principal);
    }

    private sealed class StubGraphRagAuditLog : IGraphRagAuditLog
    {
        public GraphRagAuditEntry? LastEntry { get; private set; }
        public Task RecordAsync(GraphRagAuditEntry entry, CancellationToken ct = default) { LastEntry = entry; return Task.CompletedTask; }
        public Task RecordSearchAsync(string query, IReadOnlyList<string> scopes, int limit, int resultCount, long durationMs, string searchKind, string? actorId = null, string? actorName = null, Guid? correlationId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordScopeCreatedAsync(string scopeKey, string? description, double defaultPriority, string? actorId = null, string? actorName = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordDocumentIngestedAsync(Guid documentId, string fileName, string scopeKey, int versionNumber, int chunkCount, int extractedEntityCount, int extractedRelationshipCount, long durationMs, string? actorId = null, string? actorName = null, Guid? correlationId = null, CancellationToken ct = default, IngestionAuditMetrics? performance = null) => Task.CompletedTask;
        public Task RecordDocumentDeletedAsync(Guid documentId, string fileName, string scopeKey, int contributionsProcessed, string? actorId = null, string? actorName = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubScopeService(bool exists) : IKnowledgeScopeService
    {
        public Task<KnowledgeScope> CreateScopeAsync(string scopeKey, string description, double defaultPriority = 1, string? metadata = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnowledgeScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeScope>> ListScopesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default) => Task.FromResult(exists);
        public Task<int> CountEntitiesInScopeAsync(string scopeKey, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using FabrCore.Host.WebSocket;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class WebSocketAuthenticationTests
{
    [TestMethod]
    public async Task TicketAndV2ProtocolAuthenticateAndTicketIsNotAudited()
    {
        var audit = new FakeAuditProvider();
        var authenticator = CreateAuthenticator(new FakeTicketService("alice"), audit: audit);
        var context = Context("https://app.example", "fabrcore.v2, fabrcore.ticket.secret-value");

        var result = await authenticator.AuthenticateAsync(context);

        Assert.IsTrue(result.Allowed);
        Assert.AreEqual("alice", result.UserHandle);
        Assert.IsFalse(audit.Events.SelectMany(x => x.Details.Values.Append(x.Reason ?? ""))
            .Any(x => x.Contains("secret-value", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MissingProtocolAndDisallowedOriginAreRejected()
    {
        var authenticator = CreateAuthenticator(new FakeTicketService("alice"));
        Assert.IsFalse((await authenticator.AuthenticateAsync(Context("https://app.example", "fabrcore.ticket.token"))).Allowed);
        Assert.IsFalse((await authenticator.AuthenticateAsync(Context("https://evil.example", "fabrcore.v2, fabrcore.ticket.token"))).Allowed);
    }

    [TestMethod]
    public async Task HeadlessRequestWithoutOriginIsAllowed()
    {
        var authenticator = CreateAuthenticator(new FakeTicketService("alice"));
        var context = Context(null, "fabrcore.v2, fabrcore.ticket.token");
        Assert.IsTrue((await authenticator.AuthenticateAsync(context)).Allowed);
    }

    [TestMethod]
    public async Task SystemPrincipalIsRejected()
    {
        var audit = new FakeAuditProvider();
        var authenticator = CreateAuthenticator(new FakeTicketService("system"), audit: audit);
        var result = await authenticator.AuthenticateAsync(Context(null, "fabrcore.v2, fabrcore.ticket.token"));
        Assert.IsFalse(result.Allowed);
        Assert.AreEqual(AuditOutcome.Denied, audit.Events.Last().Outcome);
    }

    [TestMethod]
    public async Task DevelopmentSelectionRequiresExplicitOption()
    {
        var context = Context(null, "fabrcore.v2");
        context.Request.Headers["x-fabrcore-userhandle"] = "Dev User";
        Assert.IsFalse((await CreateAuthenticator(new FakeTicketService(null), development: true)
            .AuthenticateAsync(context)).Allowed);

        var enabled = CreateAuthenticator(new FakeTicketService(null), development: true, developmentSelection: true);
        var result = await enabled.AuthenticateAsync(context);
        Assert.AreEqual("dev-user", result.UserHandle);
    }

    private static DefaultWebSocketAuthenticator CreateAuthenticator(
        IWebSocketTicketService tickets,
        bool development = false,
        bool developmentSelection = false,
        FakeAuditProvider? audit = null) => new(
            Options.Create(new FabrCoreHostOptions { AllowedWebSocketOrigins = ["https://app.example"] }),
            Options.Create(new FabrCoreWebSocketOptions { AllowDevelopmentPrincipalSelection = developmentSelection }),
            Options.Create(new FabrCoreAclOptions()),
            new FakeEnvironment(development ? "Development" : "Production"),
            tickets,
            audit ?? new FakeAuditProvider());

    private static DefaultHttpContext Context(string? origin, string protocols)
    {
        var context = new DefaultHttpContext();
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        context.Request.Headers.SecWebSocketProtocol = protocols;
        return context;
    }

    private sealed class FakeTicketService(string? principal) : IWebSocketTicketService
    {
        public Task<FabrCoreWebSocketTicketResponse> IssueAsync(string principalHandle) => throw new NotSupportedException();
        public Task<string?> RedeemAsync(string token) => Task.FromResult(principal);
    }

    private sealed class FakeAuditProvider : IAuditProvider
    {
        public List<AuditEvent> Events { get; } = [];
        public AuditOptions Options { get; } = new();
        public event Action<AuditEvent>? OnAuditEventRecorded;
        public Task RecordAsync(AuditEvent auditEvent) { Events.Add(auditEvent); OnAuditEventRecorded?.Invoke(auditEvent); return Task.CompletedTask; }
        public Task<List<AuditEvent>> GetEventsAsync(AuditQuery? query = null) => Task.FromResult(Events);
        public Task ClearAsync() { Events.Clear(); return Task.CompletedTask; }
    }

    private sealed class FakeEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

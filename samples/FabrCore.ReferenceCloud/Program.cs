using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FabrCore.Core.CloudServer;

// Single-cluster, single-process conformance fixture. Pending commands are intentionally
// not replayed after a process restart. Production servers need durable leases/receipts.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
var clusterKey = Required("REFERENCE_CLUSTER_KEY");
var operatorKey = Required("REFERENCE_OPERATOR_KEY");
var clusterId = Required("REFERENCE_CLUSTER_ID");
var environment = Required("REFERENCE_ENVIRONMENT");
var configurationFile = Path.GetFullPath(Required("REFERENCE_CONFIGURATION_FILE"));
var queue = Channel.CreateBounded<CloudAdminCommand>(100);
var commands = new ConcurrentDictionary<string, (CloudAdminCommand Command, CloudAdminCommandResponse? Response)>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    var admin = context.Request.Path.StartsWithSegments("/operator");
    var expected = admin ? operatorKey : clusterKey;
    var supplied = context.Request.Headers.Authorization.ToString();
    if (!Equal(supplied, "Bearer " + expected) || (!admin &&
        (context.Request.Headers[CloudServerProtocol.ClusterIdHeader] != clusterId ||
         context.Request.Headers[CloudServerProtocol.EnvironmentHeader] != environment)))
    { context.Response.StatusCode = 401; return; }
    await next(context);
});
app.MapGet(CloudServerProtocol.ConfigurationPath, async (HttpContext context) =>
{
    var contents = await File.ReadAllBytesAsync(configurationFile, context.RequestAborted);
    var etag = "\"" + Convert.ToHexString(SHA256.HashData(contents)) + "\"";
    context.Response.Headers.ETag = etag;
    return context.Request.Headers.IfNoneMatch.Contains(etag) ? Results.StatusCode(304) : Results.Bytes(contents, "application/json");
});
app.MapPost(CloudServerProtocol.HeartbeatPath, () => Results.Ok(new CloudHeartbeatResponse()));
app.MapPost("/operator/commands", (CloudAdminCommand input) =>
{
    if (!input.PathAndQuery.StartsWith("/fabrcoreapi/admin/v1/", StringComparison.Ordinal) ||
        input.Method is not ("GET" or "POST" or "PUT" or "DELETE")) return Results.BadRequest();
    if (commands.Count >= 1000) return Results.StatusCode(429);
    var command = new CloudAdminCommand { CommandId = Guid.NewGuid().ToString("N"),
        LeaseToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        Method = input.Method, PathAndQuery = input.PathAndQuery, Body = input.Body,
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60), Headers = new(StringComparer.OrdinalIgnoreCase)
        { ["X-FabrCore-Admin-Actor"] = ["reference-operator"] } };
    if (input.Headers.TryGetValue("If-Match", out var revision)) command.Headers["If-Match"] = revision;
    if (input.Body is not null) command.Headers["Content-Type"] = ["application/json"];
    commands[command.CommandId] = (command, null);
    if (!queue.Writer.TryWrite(command)) { commands.TryRemove(command.CommandId, out _); return Results.StatusCode(429); }
    return Results.Accepted("/operator/commands/" + command.CommandId, new { command.CommandId });
});
app.MapGet(CloudServerProtocol.ConnectPath, async (HttpContext context) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    try
    {
        while (await queue.Reader.WaitToReadAsync(timeout.Token))
            if (queue.Reader.TryRead(out var command) && command.ExpiresAt > DateTimeOffset.UtcNow)
                return Results.Ok(command);
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { }
    return Results.NoContent();
});
app.MapPost("/fabrcore-cloud/v2/connect/{id}/response", (string id, CloudAdminCommandResponse response) =>
{
    if (!commands.TryGetValue(id, out var prior) || response.CommandId != id ||
        !Equal(response.LeaseToken, prior.Command.LeaseToken)) return Results.NotFound();
    if (prior.Response is not null) return Results.Conflict();
    if (prior.Command.ExpiresAt <= DateTimeOffset.UtcNow) return Results.StatusCode(410);
    return commands.TryUpdate(id, (prior.Command, response), prior) ? Results.NoContent() : Results.Conflict();
});
app.MapGet("/operator/commands/{id}", (string id) =>
{
    if (!commands.TryGetValue(id, out var entry)) return Results.NotFound();
    if (entry.Response is not null) return Results.Ok(entry.Response);
    return Results.Json(new { status = entry.Command.ExpiresAt <= DateTimeOffset.UtcNow ? "incomplete" : "pending" }, statusCode: 202);
});
app.MapDelete("/operator/commands/{id}", (string id) => commands.TryRemove(id, out _) ? Results.NoContent() : Results.NotFound());
app.Run();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required.");
static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));

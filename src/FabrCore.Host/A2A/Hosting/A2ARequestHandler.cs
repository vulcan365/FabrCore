using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FabrCore.Host.A2A.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Executes A2A requests for one exposed agent across both protocol bindings.
/// </summary>
/// <remarks>
/// The JSON-RPC and HTTP+JSON bindings differ only in how a call is addressed and how the result
/// is framed, so both funnel into the same dispatch here. The one place they are not symmetric is
/// deliberate: a JSON-RPC envelope posted to an HTTP+JSON route is accepted and answered in kind,
/// because that is what Microsoft Copilot Studio sends.
/// </remarks>
internal sealed class A2ARequestHandler
{
    private readonly IA2ATaskExecutor _executor;
    private readonly IA2APrincipalResolver _principalResolver;
    private readonly IA2AAgentCardFactory _cardFactory;
    private readonly IA2AAgentCatalog _catalog;
    private readonly A2AOptions _options;
    private readonly ILogger<A2ARequestHandler> _logger;

    public A2ARequestHandler(
        IA2ATaskExecutor executor,
        IA2APrincipalResolver principalResolver,
        IA2AAgentCardFactory cardFactory,
        IA2AAgentCatalog catalog,
        IOptions<A2AOptions> options,
        ILogger<A2ARequestHandler> logger)
    {
        _executor = executor;
        _principalResolver = principalResolver;
        _cardFactory = cardFactory;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Handles the JSON-RPC binding at <c>POST {base}</c>.</summary>
    public async Task HandleJsonRpcAsync(HttpContext context, string agentName)
    {
        if (await ResolveAsync(context, agentName, envelope: true) is not { } agent)
        {
            return;
        }

        var parsed = await ParseAsync(context, impliedMethod: null, requireEnvelope: true);
        if (parsed is null)
        {
            return;
        }

        await DispatchAsync(context, agent, parsed.Value, streaming: IsStreamingMethod(parsed.Value.Method));
    }

    /// <summary>Handles <c>POST {base}/message:send</c> and <c>{base}/message:stream</c>.</summary>
    public async Task HandleHttpMessageAsync(HttpContext context, string agentName, bool streamingRoute)
    {
        if (await ResolveAsync(context, agentName, envelope: false) is not { } agent)
        {
            return;
        }

        var impliedMethod = streamingRoute ? A2AProtocol.MethodMessageStream : A2AProtocol.MethodMessageSend;
        var parsed = await ParseAsync(context, impliedMethod, requireEnvelope: false);
        if (parsed is null)
        {
            return;
        }

        var request = parsed.Value;

        // A JSON-RPC envelope on a REST route means a Copilot-Studio-shaped client: it posts to
        // the streaming URL but reads a single JSON body, so streaming the answer would lose it.
        var streaming = IsStreamingMethod(request.Method);

        await DispatchAsync(context, agent, request, streaming);
    }

    /// <summary>Handles <c>GET {base}/tasks/{id}</c>.</summary>
    public async Task HandleHttpGetTaskAsync(HttpContext context, string agentName, string taskId)
    {
        if (await ResolveAsync(context, agentName, envelope: false) is not { } agent)
        {
            return;
        }

        if (!Authorize(context, agent, out var denial))
        {
            await WriteErrorAsync(context, envelope: false, id: null, denial!);
            return;
        }

        var historyLength = int.TryParse(context.Request.Query["historyLength"], out var parsedLength)
            ? parsedLength
            : (int?)null;
        if (historyLength < 0 || (context.Request.Query.ContainsKey("historyLength") && historyLength is null))
        {
            await WriteErrorAsync(context, false, null, A2AErrors.Params("historyLength must be a nonnegative integer."));
            return;
        }

        var task = await _executor.GetTaskAsync(taskId, historyLength, context.RequestAborted);
        if (!await OwnsTaskAsync(context, agent, task))
        {
            await WriteErrorAsync(context, envelope: false, id: null, A2AErrors.NoSuchTask(taskId));
            return;
        }

        await WriteResultAsync(context, envelope: false, id: null, task!);
    }

    /// <summary>Handles <c>POST {base}/tasks/{id}:cancel</c>.</summary>
    public async Task HandleHttpCancelTaskAsync(HttpContext context, string agentName, string taskId)
    {
        if (await ResolveAsync(context, agentName, envelope: false) is not { } agent)
        {
            return;
        }

        if (!Authorize(context, agent, out var denial))
        {
            await WriteErrorAsync(context, envelope: false, id: null, denial!);
            return;
        }

        if (!await OwnsTaskAsync(context, agent, await _executor.GetTaskAsync(taskId, 0, context.RequestAborted)))
        {
            await WriteErrorAsync(context, false, null, A2AErrors.NoSuchTask(taskId));
            return;
        }
        var result = await _executor.CancelAsync(taskId, context.RequestAborted);
        switch (result.Outcome)
        {
            case A2ACancelOutcome.NotFound:
                await WriteErrorAsync(context, envelope: false, id: null, A2AErrors.NoSuchTask(taskId));
                return;
            case A2ACancelOutcome.NotCancelable:
                await WriteErrorAsync(context, envelope: false, id: null, A2AErrors.NotCancelable(taskId));
                return;
            default:
                await WriteResultAsync(context, envelope: false, id: null, result.Task!);
                return;
        }
    }

    /// <summary>Handles <c>POST {base}/tasks/{id}:subscribe</c>.</summary>
    public async Task HandleHttpSubscribeAsync(HttpContext context, string agentName, string taskId)
    {
        if (await ResolveAsync(context, agentName, envelope: false) is not { } agent)
        {
            return;
        }

        if (!Authorize(context, agent, out var denial))
        {
            await WriteErrorAsync(context, envelope: false, id: null, denial!);
            return;
        }

        await ResubscribeAsync(context, agent, taskId, envelope: false, id: null);
    }

    /// <summary>Serves one agent's card as JSON.</summary>
    public async Task WriteAgentCardAsync(HttpContext context, string agentName)
    {
        ApplyAgentCardCors(context);

        if (await ResolveAsync(context, agentName, envelope: false, discovery: true) is not { } agent)
        {
            return;
        }

        await WriteCardAsync(context, agent);
    }

    /// <summary>
    /// Serves the server-root card. With several agents published and no <c>PrimaryAgent</c>
    /// designated the root card is ambiguous, so answer with a 404 that names each agent's card
    /// URL — an unexplained 404 is the worst possible answer for someone who pointed a client at
    /// the bare host name.
    /// </summary>
    public async Task WritePrimaryAgentCardAsync(HttpContext context)
    {
        ApplyAgentCardCors(context);

        var primary = await _catalog.GetPrimaryAsync(context.RequestAborted);
        if (primary is not null)
        {
            await WriteCardAsync(context, primary);
            return;
        }

        var agents = await _catalog.ListAsync(context.RequestAborted);
        var baseUrl = _cardFactory.ResolveBaseUrl(context.Request);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            A2AJson.Serialize(new
            {
                message = agents.Count == 0
                    ? "This server publishes no A2A agents."
                    : "This server publishes several A2A agents, so there is no single agent card at the "
                      + "server root. Use one of the agent cards below, or set A2A:PrimaryAgent to serve "
                      + "one of them here.",
                agentCards = agents.ToDictionary(
                    a => a.Name,
                    a => baseUrl + a.BasePath + A2ADefaults.WellKnownAgentCardPath),
            }),
            context.RequestAborted);
    }

    /// <summary>Serves the catalog of published agents and their endpoints.</summary>
    public async Task WriteCatalogAsync(HttpContext context)
    {
        var agents = await _catalog.ListAsync(context.RequestAborted);
        var baseUrl = _cardFactory.ResolveBaseUrl(context.Request);

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            A2AJson.Serialize(new
            {
                protocolVersion = A2AProtocol.Version,
                authentication = _options.Authentication.Mode.ToString(),
                agents = agents.Select(agent => new
                {
                    name = agent.Name,
                    displayName = agent.DisplayName,
                    description = agent.Description,
                    source = agent.Source.ToString(),
                    agentCard = baseUrl + agent.BasePath + A2ADefaults.WellKnownAgentCardPath,
                    jsonRpc = baseUrl + agent.BasePath,
                    httpJson = new
                    {
                        send = baseUrl + agent.BasePath + "/message:send",
                        stream = baseUrl + agent.BasePath + "/message:stream",
                        getTask = baseUrl + agent.BasePath + "/tasks/{taskId}",
                        cancelTask = baseUrl + agent.BasePath + "/tasks/{taskId}:cancel",
                    },
                }),
            }),
            context.RequestAborted);
    }

    /// <summary>
    /// Lets a browser-based client actually read the card. Copilot Studio fetches it with a
    /// cross-origin fetch() from its own page, so without this header the browser throws away a
    /// 200 the server log shows succeeding, and the operator is told no card could be found.
    /// Card routes only - the call endpoints stay same-origin.
    /// </summary>
    private void ApplyAgentCardCors(HttpContext context)
    {
        var allowed = _options.AgentCardCorsOrigins;
        if (allowed.Count == 0)
        {
            return;
        }

        if (allowed.Contains("*"))
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            return;
        }

        // Echoing one origin makes the response origin-dependent, so it must not be cached and
        // replayed to a different one.
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)
            && allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.Headers.AccessControlAllowOrigin = origin;
        }

        context.Response.Headers.Vary = "Origin";
    }

    private async Task WriteCardAsync(HttpContext context, A2AExposedAgent agent)
    {
        var card = await _cardFactory.BuildAsync(agent, context.Request, context.RequestAborted);
        context.Response.ContentType = "application/json";
        context.Response.Headers.Vary = "Origin, A2A-Version";
        await context.Response.WriteAsync(A2AJson.Serialize(A2AV1.Card(card)), context.RequestAborted);
    }

    /// <summary>
    /// Resolves the route's agent name against the catalog, writing a not-found response and
    /// returning null when it names nothing this server publishes.
    /// </summary>
    private async Task<A2AExposedAgent?> ResolveAsync(HttpContext context, string agentName, bool envelope, bool discovery = false)
    {
        var version = context.Request.Headers["A2A-Version"].ToString();
        if (version != "1.0" && !(discovery && version.Length == 0))
        {
            await WriteErrorAsync(context, envelope, null, new A2AJsonRpcError
                { Code = -32009, Message = "VersionNotSupportedError", Data = new { supportedVersions = new[] { "1.0" } } });
            return null;
        }
        var agent = await _catalog.FindAsync(agentName, context.RequestAborted);
        if (agent is not null)
        {
            return agent;
        }

        _logger.LogDebug("A2A request for unknown agent {Agent}.", agentName);
        await WriteErrorAsync(
            context,
            envelope,
            id: null,
            new A2AJsonRpcError
            {
                Code = A2AErrors.MethodNotFound,
                Message = "No A2A agent is published under that name on this server.",
                Data = agentName,
            });
        return null;
    }

    // ── Dispatch ───────────────────────────────────────────────────────────────────────────

    private async Task DispatchAsync(
        HttpContext context, A2AExposedAgent agent, ParsedRequest request, bool streaming)
    {
        if (!Authorize(context, agent, out var denial))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, denial!);
            return;
        }

        switch (request.Method)
        {
            case "ListTasks":
                await ListTasksAsync(context, agent, request);
                return;
            case A2AProtocol.MethodMessageSend:
            case A2AProtocol.MethodMessageStream:
                await SendMessageAsync(context, agent, request, streaming);
                return;

            case A2AProtocol.MethodTasksGet:
                await GetTaskAsync(context, agent, request);
                return;

            case A2AProtocol.MethodTasksCancel:
                await CancelTaskAsync(context, agent, request);
                return;

            case A2AProtocol.MethodTasksResubscribe:
                if (!TryReadParams<A2ATaskIdParams>(request, out var resubscribeParams, out var paramsError))
                {
                    await WriteErrorAsync(context, request.Envelope, request.Id, paramsError!);
                    return;
                }

                await ResubscribeAsync(context, agent, resubscribeParams!.Id, request.Envelope, request.Id);
                return;

            case A2AProtocol.MethodPushNotificationSet:
            case A2AProtocol.MethodPushNotificationGet:
            case A2AProtocol.MethodPushNotificationList:
            case A2AProtocol.MethodPushNotificationDelete:
                await WriteErrorAsync(
                    context, request.Envelope, request.Id, A2AErrors.PushNotificationsUnsupported());
                return;

            case A2AProtocol.MethodAgentAuthenticatedExtendedCard:
                await WriteErrorAsync(
                    context,
                    request.Envelope,
                    request.Id,
                    new A2AJsonRpcError
                    {
                        Code = A2AErrors.AuthenticatedExtendedCardNotConfigured,
                        Message = "Authenticated Extended Card is not configured",
                    });
                return;

            default:
                await WriteErrorAsync(
                    context, request.Envelope, request.Id, A2AErrors.MethodNotFoundFor(request.Method));
                return;
        }
    }

    private async Task SendMessageAsync(
        HttpContext context, A2AExposedAgent agent, ParsedRequest request, bool streaming)
    {
        if (!TryReadParams<A2AMessageSendParams>(request, out var sendParams, out var error))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, error!);
            return;
        }

        var message = sendParams!.Message;
        if (message?.Parts is null || message.Parts.Count == 0 || message.Parts.Any(p => p is null))
        {
            await WriteErrorAsync(
                context, request.Envelope, request.Id, A2AErrors.Params("message.parts must not be empty."));
            return;
        }

        var contextId = FirstNonEmpty(message.ContextId, Guid.NewGuid().ToString());
        if (sendParams.Configuration?.PushNotificationConfig is not null)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.PushNotificationsUnsupported());
            return;
        }

        if (string.IsNullOrWhiteSpace(message.MessageId) || sendParams.Configuration?.HistoryLength < 0)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.Params("A messageId and nonnegative historyLength are required."));
            return;
        }
        if (streaming && !agent.Streaming)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.Unsupported("This agent does not support streaming."));
            return;
        }
        var principalHandle = await _principalResolver.ResolvePrincipalHandleAsync(
            context, agent, contextId, context.RequestAborted);
        if (principalHandle is null)
        {
            await WriteErrorAsync(
                context,
                request.Envelope,
                request.Id,
                A2AErrors.Invalid("The caller could not be mapped to a FabrCore principal."));
            return;
        }

        // This executor completes a single turn; it does not resume interrupted tasks.
        // Never accept client-selected IDs or replace a running/terminal task.
        if (!string.IsNullOrWhiteSpace(message.TaskId))
        {
            var existing = await _executor.GetTaskAsync(message.TaskId, 0, context.RequestAborted);
            var taskError = await OwnsTaskAsync(context, agent, existing)
                ? A2AErrors.Unsupported("Task continuation is not supported; send a new message with the same contextId.")
                : A2AErrors.NoSuchTask(message.TaskId);
            await WriteErrorAsync(context, request.Envelope, request.Id, taskError);
            return;
        }
        var taskId = Guid.NewGuid().ToString();
        message.Role = A2ARoles.User;

        A2ATaskExecution execution;
        try
        {
            execution = _executor.Start(new A2AExecutionRequest(
                agent, principalHandle, message, taskId, contextId, _principalResolver.DescribeCaller(context)));
        }
        catch (A2ATaskCapacityException)
        {
            context.Response.Headers.RetryAfter = "1";
            await WriteErrorAsync(context, request.Envelope, request.Id,
                new A2AJsonRpcError { Code = A2AErrors.CapacityExceeded, Message = "The server is at its concurrent task limit." });
            return;
        }

        _logger.LogInformation(
            "A2A {Method} on agent {Agent} started task {TaskId} (context {ContextId}) for principal {Principal}.",
            request.Method, agent.Name, taskId, contextId, principalHandle);

        if (streaming)
        {
            await StreamAsync(context, execution, request.Envelope, request.Id);
            return;
        }

        if (sendParams.Configuration?.Blocking == false)
        {
            // Non-blocking: hand back the submitted task now and let the client poll tasks/get.
            await WriteResultAsync(context, request.Envelope, request.Id, execution.Snapshot(), wrap: true);
            return;
        }

        await execution.Completion.WaitAsync(context.RequestAborted);
        var final = execution.Snapshot();
        if (sendParams.Configuration?.HistoryLength is { } historyLength)
            final.History = final.History?.TakeLast(historyLength).ToList();

        var shape = _options.Interop.ResultShape;

        object result = shape == A2AResultShape.Message
            ? BuildMessageResult(final)
            : final;

        await WriteResultAsync(context, request.Envelope, request.Id, result, wrap: true);
    }

    private async Task GetTaskAsync(HttpContext context, A2AExposedAgent agent, ParsedRequest request)
    {
        if (!TryReadParams<A2ATaskQueryParams>(request, out var queryParams, out var error))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, error!);
            return;
        }

        var task = await _executor.GetTaskAsync(
            queryParams!.Id, queryParams.HistoryLength, context.RequestAborted);

        if (queryParams.HistoryLength < 0)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.Params("historyLength must be nonnegative."));
            return;
        }

        if (!await OwnsTaskAsync(context, agent, task))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.NoSuchTask(queryParams.Id));
            return;
        }

        await WriteResultAsync(context, request.Envelope, request.Id, task!);
    }

    private async Task CancelTaskAsync(HttpContext context, A2AExposedAgent agent, ParsedRequest request)
    {
        if (!TryReadParams<A2ATaskIdParams>(request, out var idParams, out var error))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, error!);
            return;
        }

        if (!await OwnsTaskAsync(context, agent, await _executor.GetTaskAsync(idParams!.Id, 0, context.RequestAborted)))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.NoSuchTask(idParams.Id));
            return;
        }
        var result = await _executor.CancelAsync(idParams.Id, context.RequestAborted);
        switch (result.Outcome)
        {
            case A2ACancelOutcome.NotFound:
                await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.NoSuchTask(idParams.Id));
                return;
            case A2ACancelOutcome.NotCancelable:
                await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.NotCancelable(idParams.Id));
                return;
            default:
                await WriteResultAsync(context, request.Envelope, request.Id, result.Task!);
                return;
        }
    }

    private async Task ResubscribeAsync(HttpContext context, A2AExposedAgent agent, string taskId, bool envelope, JsonNode? id)
    {
        if (!await OwnsTaskAsync(context, agent, await _executor.GetTaskAsync(taskId, 0, context.RequestAborted)))
        {
            await WriteErrorAsync(context, envelope, id, A2AErrors.NoSuchTask(taskId));
            return;
        }
        var execution = _executor.Find(taskId);
        if (execution is not null && A2ATaskStates.IsTerminal(execution.Snapshot().Status.State))
        {
            await WriteErrorAsync(context, envelope, id, A2AErrors.Unsupported("Cannot subscribe to a terminal task."));
            return;
        }
        if (execution is null)
        {
            var stored = await _executor.GetTaskAsync(taskId, null, context.RequestAborted);
            await WriteErrorAsync(
                context,
                envelope,
                id,
                stored is null
                    ? A2AErrors.NoSuchTask(taskId)
                    : A2AErrors.Unsupported("The task has finished and its event stream is no longer available."));
            return;
        }

        await StreamAsync(context, execution, envelope, id);
    }

    private async Task StreamAsync(
        HttpContext context, A2ATaskExecution execution, bool envelope, JsonNode? id)
    {
        await using var writer = new A2ASseWriter(
            context, _options.Tasks.StreamHeartbeatInterval, _logger);

        await foreach (var evt in execution.SubscribeAsync(context.RequestAborted))
        {
            var result = A2AV1.Result(evt, wrap: true);
            var payload = envelope ? A2AJsonRpcResponse.Success(id, result) : result;
            await writer.WriteAsync(payload);
        }
    }

    // ── Parsing and framing ────────────────────────────────────────────────────────────────

    private readonly record struct ParsedRequest(
        bool Envelope, JsonNode? Id, string Method, JsonElement? Params, bool NativeJsonRpcRoute);

    private async Task<ParsedRequest?> ParseAsync(
        HttpContext context, string? impliedMethod, bool requireEnvelope)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, requireEnvelope, null, A2AErrors.Parse(ex.Message));
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(
                    context, requireEnvelope, null, A2AErrors.Invalid("The request body must be a JSON object."));
                return null;
            }

            var isEnvelope = root.TryGetProperty("jsonrpc", out _) && root.TryGetProperty("method", out _);

            if (requireEnvelope && !isEnvelope)
            {
                await WriteErrorAsync(
                    context,
                    envelope: true,
                    id: null,
                    A2AErrors.Invalid("A JSON-RPC 2.0 request object with 'jsonrpc' and 'method' is required."));
                return null;
            }

            if (isEnvelope && !requireEnvelope && !_options.Interop.AcceptJsonRpcOnHttpRoutes)
            {
                await WriteErrorAsync(
                    context,
                    envelope: false,
                    id: null,
                    A2AErrors.Invalid(
                        "This route takes the HTTP+JSON binding body. Post JSON-RPC requests to the agent's base URL, " +
                        "or enable A2A:Interop:AcceptJsonRpcOnHttpRoutes."));
                return null;
            }

            if (!isEnvelope)
            {
                return new ParsedRequest(false, null, impliedMethod!, A2AV1.Parameters(root), false);
            }

            var id = root.TryGetProperty("id", out var idElement)
                ? JsonNode.Parse(idElement.GetRawText())
                : null;

            if (root.GetProperty("jsonrpc").ValueKind != JsonValueKind.String
                || root.GetProperty("jsonrpc").GetString() != "2.0"
                || root.GetProperty("method").ValueKind != JsonValueKind.String)
            {
                await WriteErrorAsync(context, true, id, A2AErrors.Invalid("A valid JSON-RPC 2.0 method is required."));
                return null;
            }
            var method = root.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : (JsonElement?)null;

            if (parameters is { } p) parameters = A2AV1.Parameters(p);
            return new ParsedRequest(true, id, method, parameters, requireEnvelope);
        }
    }

    private static bool TryReadParams<T>(ParsedRequest request, out T? value, out A2AJsonRpcError? error)
        where T : class
    {
        value = null;
        error = null;

        if (request.Params is null)
        {
            error = A2AErrors.Params("'params' is required.");
            return false;
        }

        try
        {
            value = request.Params.Value.Deserialize<T>(A2AJson.Options);
        }
        catch (JsonException ex)
        {
            error = A2AErrors.Params(ex.Message);
            return false;
        }

        if (value is null)
        {
            error = A2AErrors.Params("'params' could not be read.");
            return false;
        }

        return true;
    }

    private static bool IsStreamingMethod(string method)
        => method is A2AProtocol.MethodMessageStream or A2AProtocol.MethodTasksResubscribe;

    private static A2AMessage BuildMessageResult(A2ATask task)
    {
        // A Message result carries the answer without the task wrapper. Prefer the terminal
        // status message; fall back to the artifact parts so nothing is dropped.
        if (task.Artifacts?.Any(a => a.Parts.Count > 0) != true
            && task.Status.Message is { } statusMessage && statusMessage.Parts.Count > 0)
        {
            return statusMessage;
        }

        var parts = task.Artifacts?.SelectMany(a => a.Parts).ToList() ?? new List<A2APart>();
        if (parts.Count == 0)
        {
            parts.Add(A2APart.FromText(string.Empty));
        }

        return new A2AMessage
        {
            Role = A2ARoles.Agent,
            MessageId = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            ContextId = task.ContextId,
            Parts = parts,
        };
    }

    private bool Authorize(HttpContext context, A2AExposedAgent agent, out A2AJsonRpcError? error)
    {
        error = null;

        if (_options.Authentication.Mode == A2AAuthenticationMode.JwtBearer)
        {
            var jwt = _options.Authentication.JwtBearer;
            var scopes = (context.User.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (jwt.RequiredScopes.Any(scope => !scopes.Contains(scope, StringComparer.Ordinal))
                || (jwt.RequiredRoles.Count > 0 && !context.User.FindAll("roles").Any(c => jwt.RequiredRoles.Contains(c.Value, StringComparer.Ordinal))))
            {
                error = A2AErrors.Invalid("The token does not have the required scopes or roles.");
                return false;
            }
        }

        var allowed = context.User.FindFirstValue(A2AClaimTypes.AllowedAgents);
        if (string.IsNullOrEmpty(allowed))
        {
            return true;
        }

        var names = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Contains(agent.Name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        _logger.LogWarning(
            "An A2A credential scoped to [{Allowed}] was used against agent {Agent}.", allowed, agent.Name);
        error = new A2AJsonRpcError
        {
            Code = A2AErrors.InvalidRequest,
            Message = "This credential is not authorized for the requested agent.",
            Data = agent.Name,
        };
        return false;
    }

    public async Task HandleHttpListTasksAsync(HttpContext context, string agentName)
    {
        if (await ResolveAsync(context, agentName, false) is not { } agent) return;
        var query = new JsonObject();
        foreach (var key in new[] { "contextId", "status", "pageToken", "statusTimestampAfter" })
            if (context.Request.Query.TryGetValue(key, out var value)) query[key] = value.ToString();
        foreach (var key in new[] { "pageSize", "historyLength" })
            if (context.Request.Query.TryGetValue(key, out var value))
            {
                if (!int.TryParse(value, out var number))
                {
                    await WriteErrorAsync(context, false, null, A2AErrors.Params($"{key} must be an integer."));
                    return;
                }
                query[key] = number;
            }
        if (context.Request.Query.TryGetValue("includeArtifacts", out var include))
        {
            if (!bool.TryParse(include, out var flag))
            {
                await WriteErrorAsync(context, false, null, A2AErrors.Params("includeArtifacts must be boolean."));
                return;
            }
            query["includeArtifacts"] = flag;
        }
        await DispatchAsync(context, agent, new ParsedRequest(false, null, "ListTasks",
            JsonSerializer.SerializeToElement(query), false), false);
    }

    private async Task ListTasksAsync(HttpContext context, A2AExposedAgent agent, ParsedRequest request)
    {
        if (!TryReadParams<A2AListTasksParams>(request, out var query, out var error))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, error!);
            return;
        }
        var pageSize = query!.PageSize ?? 50;
        if (pageSize is < 1 or > 100 || query.HistoryLength < 0)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.Params("Invalid pageSize or historyLength."));
            return;
        }
        var offset = 0;
        if (!string.IsNullOrEmpty(query.PageToken) && (!int.TryParse(query.PageToken, out offset) || offset < 0))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.Params("Invalid pageToken."));
            return;
        }
        IReadOnlyList<A2ATask> all;
        try { all = await _executor.ListAsync(context.RequestAborted); }
        catch (NotSupportedException)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.Unsupported("The configured task store does not support ListTasks."));
            return;
        }
        var owned = new List<A2ATask>();
        foreach (var task in all.OrderByDescending(t => t.Status.Timestamp).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            if (!await OwnsTaskAsync(context, agent, task)) continue;
            if (!string.IsNullOrEmpty(query.ContextId) && query.ContextId != task.ContextId) continue;
            if (!string.IsNullOrEmpty(query.Status) && query.Status != "TASK_STATE_" + task.Status.State.Replace('-', '_').ToUpperInvariant()) continue;
            if (query.StatusTimestampAfter is { } after && (!DateTimeOffset.TryParse(task.Status.Timestamp, out var timestamp) || timestamp < after)) continue;
            owned.Add(task);
        }
        var page = owned.Skip(offset).Take(pageSize).Select(task => new A2ATask
        {
            Id = task.Id, ContextId = task.ContextId, Status = task.Status, Metadata = task.Metadata,
            Artifacts = query.IncludeArtifacts ? task.Artifacts : null,
            History = task.History?.TakeLast(query.HistoryLength ?? _options.Tasks.DefaultHistoryLength).ToList(),
        }).ToList();
        await WriteResultAsync(context, request.Envelope, request.Id, new
        {
            tasks = page, totalSize = owned.Count, pageSize,
            nextPageToken = offset + page.Count < owned.Count ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
        });
    }

    private async ValueTask<bool> OwnsTaskAsync(HttpContext context, A2AExposedAgent agent, A2ATask? task)
    {
        if (task?.Metadata is null || !task.Metadata.TryGetValue(A2ATaskOwnership.Key, out var owner)
            || owner.ValueKind != JsonValueKind.String)
            return false;
        var principal = await _principalResolver.ResolvePrincipalHandleAsync(
            context, agent, task.ContextId, context.RequestAborted);
        return principal is not null && owner.GetString() == A2ATaskOwnership.Fingerprint(
            agent.Name, principal, _principalResolver.DescribeCaller(context));
    }

    private static string FirstNonEmpty(string? candidate, string fallback)
        => string.IsNullOrWhiteSpace(candidate) ? fallback : candidate!;

    private static Task WriteResultAsync(HttpContext context, bool envelope, JsonNode? id, object result, bool wrap = false)
    {
        context.Response.ContentType = !envelope ? "application/a2a+json" : "application/json";
        result = A2AV1.Result(result, wrap);
        var payload = envelope ? A2AJsonRpcResponse.Success(id, result) : result;
        return context.Response.WriteAsync(A2AJson.Serialize(payload), context.RequestAborted);
    }

    private static Task WriteErrorAsync(
        HttpContext context, bool envelope, JsonNode? id, A2AJsonRpcError error)
    {
        if (context.Response.HasStarted)
        {
            // Already streaming: the terminal event is the only signal left to give.
            return Task.CompletedTask;
        }

        context.Response.ContentType = "application/json";

        if (envelope)
        {
            // JSON-RPC reports application errors in the envelope with HTTP 200.
            context.Response.StatusCode = StatusCodes.Status200OK;
            return context.Response.WriteAsync(
                A2AJson.Serialize(A2AJsonRpcResponse.Failure(id, error)), context.RequestAborted);
        }

        context.Response.StatusCode = A2AErrors.ToHttpStatus(error.Code);
        context.Response.ContentType = "application/problem+json";
        var errorType = error.Code switch
        {
            A2AErrors.TaskNotFound => "task-not-found", A2AErrors.TaskNotCancelable => "task-not-cancelable",
            A2AErrors.PushNotificationNotSupported => "push-notification-not-supported",
            A2AErrors.UnsupportedOperation => "unsupported-operation", A2AErrors.ContentTypeNotSupported => "content-type-not-supported",
            A2AErrors.AuthenticatedExtendedCardNotConfigured => "extended-agent-card-not-configured",
            A2AErrors.VersionNotSupported => "version-not-supported", A2AErrors.CapacityExceeded => "capacity-exceeded", _ => "invalid-request",
        };
        return context.Response.WriteAsync(A2AJson.Serialize(new
        {
            type = "https://a2a-protocol.org/errors/" + errorType,
            title = error.Message, status = context.Response.StatusCode,
            detail = error.Data is string text ? text : error.Data is null ? null : A2AJson.Serialize(error.Data),
            supportedVersions = error.Code == A2AErrors.VersionNotSupported ? new[] { "1.0" } : null,
        }), context.RequestAborted);
    }
}

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

    /// <summary>Handles <c>POST {base}/v1/message:send</c> and <c>{base}/v1/message:stream</c>.</summary>
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
        var streaming = request.Envelope && _options.Interop.CollapseStreamForJsonRpcOnHttpRoutes
            ? false
            : IsStreamingMethod(request.Method);

        await DispatchAsync(context, agent, request, streaming);
    }

    /// <summary>Handles <c>GET {base}/v1/tasks/{id}</c>.</summary>
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

        var task = await _executor.GetTaskAsync(taskId, historyLength, context.RequestAborted);
        if (task is null)
        {
            await WriteErrorAsync(context, envelope: false, id: null, A2AErrors.NoSuchTask(taskId));
            return;
        }

        await WriteResultAsync(context, envelope: false, id: null, task);
    }

    /// <summary>Handles <c>POST {base}/v1/tasks/{id}:cancel</c>.</summary>
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

    /// <summary>Handles <c>POST {base}/v1/tasks/{id}:subscribe</c>.</summary>
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

        await ResubscribeAsync(context, taskId, envelope: false, id: null);
    }

    /// <summary>Serves one agent's card as JSON.</summary>
    public async Task WriteAgentCardAsync(HttpContext context, string agentName)
    {
        ApplyAgentCardCors(context);

        if (await ResolveAsync(context, agentName, envelope: false) is not { } agent)
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
                        send = baseUrl + agent.BasePath + "/v1/message:send",
                        stream = baseUrl + agent.BasePath + "/v1/message:stream",
                        getTask = baseUrl + agent.BasePath + "/v1/tasks/{taskId}",
                        cancelTask = baseUrl + agent.BasePath + "/v1/tasks/{taskId}:cancel",
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
        await context.Response.WriteAsync(A2AJson.Serialize(card), context.RequestAborted);
    }

    /// <summary>
    /// Resolves the route's agent name against the catalog, writing a not-found response and
    /// returning null when it names nothing this server publishes.
    /// </summary>
    private async Task<A2AExposedAgent?> ResolveAsync(HttpContext context, string agentName, bool envelope)
    {
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
            case A2AProtocol.MethodMessageSend:
            case A2AProtocol.MethodMessageStream:
                await SendMessageAsync(context, agent, request, streaming);
                return;

            case A2AProtocol.MethodTasksGet:
                await GetTaskAsync(context, request);
                return;

            case A2AProtocol.MethodTasksCancel:
                await CancelTaskAsync(context, request);
                return;

            case A2AProtocol.MethodTasksResubscribe:
                if (!TryReadParams<A2ATaskIdParams>(request, out var resubscribeParams, out var paramsError))
                {
                    await WriteErrorAsync(context, request.Envelope, request.Id, paramsError!);
                    return;
                }

                await ResubscribeAsync(context, resubscribeParams!.Id, request.Envelope, request.Id);
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
        if (message.Parts.Count == 0)
        {
            await WriteErrorAsync(
                context, request.Envelope, request.Id, A2AErrors.Params("message.parts must not be empty."));
            return;
        }

        var contextId = FirstNonEmpty(message.ContextId, Guid.NewGuid().ToString());
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

        // A client continuing a task supplies its id; a new turn gets a fresh one.
        var taskId = FirstNonEmpty(message.TaskId, Guid.NewGuid().ToString());
        message.Role = A2ARoles.User;

        var execution = _executor.Start(new A2AExecutionRequest(
            agent,
            principalHandle,
            message,
            taskId,
            contextId,
            _principalResolver.DescribeCaller(context)));

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
            await WriteResultAsync(context, request.Envelope, request.Id, execution.Snapshot());
            return;
        }

        await execution.Completion.WaitAsync(context.RequestAborted);
        var final = execution.Snapshot();

        var shape = request.Envelope && !request.NativeJsonRpcRoute
            ? _options.Interop.CompatibilityResultShape
            : _options.Interop.ResultShape;

        object result = shape == A2AResultShape.Message
            ? BuildMessageResult(final)
            : final;

        await WriteResultAsync(context, request.Envelope, request.Id, result);
    }

    private async Task GetTaskAsync(HttpContext context, ParsedRequest request)
    {
        if (!TryReadParams<A2ATaskQueryParams>(request, out var queryParams, out var error))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, error!);
            return;
        }

        var task = await _executor.GetTaskAsync(
            queryParams!.Id, queryParams.HistoryLength, context.RequestAborted);

        if (task is null)
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, A2AErrors.NoSuchTask(queryParams.Id));
            return;
        }

        await WriteResultAsync(context, request.Envelope, request.Id, task);
    }

    private async Task CancelTaskAsync(HttpContext context, ParsedRequest request)
    {
        if (!TryReadParams<A2ATaskIdParams>(request, out var idParams, out var error))
        {
            await WriteErrorAsync(context, request.Envelope, request.Id, error!);
            return;
        }

        var result = await _executor.CancelAsync(idParams!.Id, context.RequestAborted);
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

    private async Task ResubscribeAsync(HttpContext context, string taskId, bool envelope, JsonNode? id)
    {
        var execution = _executor.Find(taskId);
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
            var payload = envelope ? A2AJsonRpcResponse.Success(id, evt) : evt;
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
                return new ParsedRequest(false, null, impliedMethod!, root.Clone(), false);
            }

            var id = root.TryGetProperty("id", out var idElement)
                ? JsonNode.Parse(idElement.GetRawText())
                : null;

            var method = root.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : (JsonElement?)null;

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
        if (task.Status.Message is { } statusMessage && statusMessage.Parts.Count > 0)
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

    private static string FirstNonEmpty(string? candidate, string fallback)
        => string.IsNullOrWhiteSpace(candidate) ? fallback : candidate!;

    private static Task WriteResultAsync(HttpContext context, bool envelope, JsonNode? id, object result)
    {
        context.Response.ContentType = "application/json";
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
        return context.Response.WriteAsync(A2AJson.Serialize(new { error }), context.RequestAborted);
    }
}

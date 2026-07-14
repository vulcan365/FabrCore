using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// The Microsoft 365 Agents SDK application that bridges Copilot/Teams conversations to FabrCore
/// agents: each inbound message activity is mapped to a FabrCore principal, routed to that
/// principal's configured agent through <see cref="IFabrCoreAgentService"/>, and the agent's reply
/// is returned to the channel (streamed on streaming-capable channels). Adaptive Card submits —
/// message activities carrying the payload in <c>Value</c> with empty text — are routed the same
/// way as surface <c>ui.action</c> messages.
/// </summary>
public class FabrCoreCopilotAgent : AgentApplication
{
    // Surface renders are one-way sends racing the agent's direct reply; wait briefly after the
    // reply so cards dispatched at the very end of the agent's turn are still captured.
    private static readonly TimeSpan TrailingRenderWait = TimeSpan.FromMilliseconds(250);

    private readonly IFabrCoreAgentService _agentService;
    private readonly ICopilotPrincipalResolver _principalResolver;
    private readonly ICopilotAgentProvisioner _provisioner;
    private readonly IGrainFactory _grainFactory;
    private readonly Microsoft365CopilotOptions _copilotOptions;
    private readonly ILogger<FabrCoreCopilotAgent> _logger;
    private readonly ICopilotConversationContextWriter? _conversationContextWriter;

    public FabrCoreCopilotAgent(
        AgentApplicationOptions options,
        IFabrCoreAgentService agentService,
        ICopilotPrincipalResolver principalResolver,
        ICopilotAgentProvisioner provisioner,
        IGrainFactory grainFactory,
        IOptions<Microsoft365CopilotOptions> copilotOptions,
        ILogger<FabrCoreCopilotAgent> logger,
        ICopilotConversationContextWriter? conversationContextWriter = null)
        : base(options)
    {
        _agentService = agentService;
        _principalResolver = principalResolver;
        _provisioner = provisioner;
        _grainFactory = grainFactory;
        _copilotOptions = copilotOptions.Value;
        _logger = logger;
        _conversationContextWriter = conversationContextWriter;

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);
        OnActivity(ActivityTypes.Message, OnMessageActivityAsync, rank: RouteRank.Last);
        OnTurnError(HandleTurnErrorAsync);
    }

    private async Task WelcomeAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var welcome = _copilotOptions.WelcomeMessage;
        if (string.IsNullOrWhiteSpace(welcome) || turnContext.Activity.MembersAdded is null)
        {
            return;
        }

        foreach (var member in turnContext.Activity.MembersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient?.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(welcome), cancellationToken);
                break;
            }
        }
    }

    private async Task OnMessageActivityAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var text = turnContext.Activity.Text?.Trim();
        AgentMessage? agentMessage = null;
        if (string.IsNullOrEmpty(text))
        {
            // An Adaptive Card Action.Submit arrives as a message activity with empty text and
            // the submit payload in Value; anything else without text is not routable.
            if (!CopilotActivityMapper.TryCreateUiActionMessage(turnContext.Activity.Value, out agentMessage))
            {
                return;
            }

            _logger.LogDebug(
                "Routing Adaptive Card submit on activity {ActivityId} as a ui.action message",
                turnContext.Activity.Id);
        }

        var userToken = await TryGetUserTokenAsync(turnContext, cancellationToken);

        var principalHandle = await _principalResolver.ResolvePrincipalHandleAsync(turnContext, userToken, cancellationToken);
        if (principalHandle is null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("I couldn't verify your identity, so I can't process this request."),
                cancellationToken);
            return;
        }

        string? deliveryEndpointId = null;
        if (_conversationContextWriter is not null)
        {
            try
            {
                // Refresh before the turn observer subscribes. This also wakes Core so
                // durable messages that arrived between turns can enter the outbox.
                deliveryEndpointId = await _conversationContextWriter.TrackAsync(
                    principalHandle,
                    turnContext,
                    cancellationToken);
                _logger.LogInformation(
                    "Microsoft 365 conversation endpoint capture completed - Principal: {Principal}, ActivityId: {ActivityId}, EndpointCaptured: {EndpointCaptured}, Endpoint: {Endpoint}, ChannelId: {ChannelId}, ConversationType: {ConversationType}",
                    principalHandle,
                    turnContext.Activity.Id,
                    !string.IsNullOrWhiteSpace(deliveryEndpointId),
                    CopilotConversationContextWriter.ShortEndpointId(deliveryEndpointId),
                    turnContext.Activity.ChannelId?.ToString(),
                    turnContext.Activity.Conversation?.ConversationType);
            }
            catch (Exception ex)
            {
                // Endpoint capture must never break the existing in-turn bridge.
                _logger.LogWarning(
                    ex,
                    "Could not update proactive conversation context for principal {Principal}",
                    principalHandle);
            }
        }

        var targetHandle = await _provisioner.EnsureAgentAsync(
            principalHandle, turnContext.Activity.Conversation?.Id, cancellationToken);

        agentMessage ??= new AgentMessage { Message = text, Kind = MessageKind.Request };
        PopulateChannelContext(agentMessage, turnContext, userToken, deliveryEndpointId);

        // Observe the principal for the duration of the turn: agents deliver ui.render surface
        // messages there (not as the OnMessage reply), and with no observer subscribed they
        // would queue on the grain and never reach this channel.
        PrincipalMessageCapture? capture = null;
        try
        {
            capture = await PrincipalMessageCapture.SubscribeAsync(_grainFactory, principalHandle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not subscribe to principal {Principal} for surface message capture; adaptive-card renders will not be relayed this turn.",
                principalHandle);
        }

        await using var _ = capture;

        var useStreaming = _copilotOptions.Streaming.Enabled
            && turnContext.StreamingResponse.IsStreamingChannel;

        if (useStreaming)
        {
            await RespondStreamingAsync(turnContext, principalHandle, targetHandle, agentMessage, capture, cancellationToken);
        }
        else
        {
            await RespondBufferedAsync(turnContext, principalHandle, targetHandle, agentMessage, capture, cancellationToken);
        }
    }

    private async Task RespondStreamingAsync(
        ITurnContext turnContext,
        string principalHandle,
        string targetHandle,
        AgentMessage agentMessage,
        PrincipalMessageCapture? capture,
        CancellationToken cancellationToken)
    {
        var stream = turnContext.StreamingResponse;
        stream.EnableGeneratedByAILabel = _copilotOptions.Streaming.EnableGeneratedByAILabel;
        if (_copilotOptions.Streaming.EnableFeedbackLoop)
        {
            stream.FeedbackLoopEnabled = true;
        }

        if (!string.IsNullOrEmpty(_copilotOptions.Streaming.InformativeUpdate))
        {
            await stream.QueueInformativeUpdateAsync(_copilotOptions.Streaming.InformativeUpdate, cancellationToken);
        }

        try
        {
            var reply = await InvokeFabrCoreAgentAsync(principalHandle, targetHandle, agentMessage);
            var replyActivity = await BuildReplyActivityAsync(reply, capture, cancellationToken);

            if (replyActivity.Attachments is { Count: > 0 })
            {
                // Cards can't be streamed as text chunks; deliver the whole activity as the
                // final streamed message (SDK 1.6.x has no StreamingResponse.AddAttachment,
                // and FinalMessage keeps its own text and attachments untouched).
                stream.FinalMessage = replyActivity;
            }
            else
            {
                stream.QueueTextChunk(replyActivity.Text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FabrCore agent {Principal}:{Handle} failed to answer Copilot message {ActivityId}",
                principalHandle, targetHandle, turnContext.Activity.Id);
            _provisioner.Invalidate(principalHandle, targetHandle);
            stream.QueueTextChunk(_copilotOptions.ErrorMessage);
        }
        finally
        {
            await stream.EndStreamAsync(cancellationToken);
        }
    }

    private async Task RespondBufferedAsync(
        ITurnContext turnContext,
        string principalHandle,
        string targetHandle,
        AgentMessage agentMessage,
        PrincipalMessageCapture? capture,
        CancellationToken cancellationToken)
    {
        IActivity replyActivity;
        try
        {
            var reply = await InvokeFabrCoreAgentAsync(principalHandle, targetHandle, agentMessage);
            replyActivity = await BuildReplyActivityAsync(reply, capture, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FabrCore agent {Principal}:{Handle} failed to answer Copilot message {ActivityId}",
                principalHandle, targetHandle, turnContext.Activity.Id);
            _provisioner.Invalidate(principalHandle, targetHandle);
            replyActivity = MessageFactory.Text(_copilotOptions.ErrorMessage);
        }

        await turnContext.SendActivityAsync(replyActivity, cancellationToken);
    }

    private async Task<AgentMessage?> InvokeFabrCoreAgentAsync(
        string principalHandle, string targetHandle, AgentMessage agentMessage)
    {
        var reply = await _agentService.SendAndReceiveMessageAsync(principalHandle, targetHandle, agentMessage);

        if (reply?.MessageType == SystemMessageTypes.Error)
        {
            throw new InvalidOperationException($"Agent returned an error: {reply.Message}");
        }

        return reply;
    }

    /// <summary>
    /// Maps a FabrCore reply plus any surface renders captured from the principal during the
    /// turn to the outgoing channel activity: adaptive-card renders become card attachments,
    /// plain replies are sent as text.
    /// </summary>
    private async Task<IActivity> BuildReplyActivityAsync(
        AgentMessage? reply, PrincipalMessageCapture? capture, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentMessage> surfaceRenders = [];
        if (capture is not null)
        {
            await Task.Delay(TrailingRenderWait, cancellationToken);
            surfaceRenders = capture.DrainAdaptiveCardRenders();
        }

        var activity = CopilotActivityMapper.BuildReplyActivity(
            reply, surfaceRenders, "The agent returned an empty response.", out var unmappedRenderIds);

        foreach (var messageId in unmappedRenderIds)
        {
            _logger.LogWarning(
                "Agent message {MessageId} is an adaptive-card render but no Adaptive Card could be parsed from its data payload; it was skipped.",
                messageId);
        }

        return activity;
    }

    /// <summary>
    /// Stamps the channel identity, conversation args, optional user token, and trace context
    /// this addon adds to every bridged message.
    /// </summary>
    private void PopulateChannelContext(
        AgentMessage message,
        ITurnContext turnContext,
        string? userToken,
        string? deliveryEndpointId)
    {
        var activity = turnContext.Activity;
        message.Channel = Microsoft365CopilotDefaults.ChannelName;
        message.Args ??= new Dictionary<string, string>();

        AddArg(message, Microsoft365CopilotDefaults.ArgChannelId, activity.ChannelId?.ToString());
        AddArg(message, Microsoft365CopilotDefaults.ArgConversationId, activity.Conversation?.Id);
        AddArg(message, Microsoft365CopilotDefaults.ArgActivityId, activity.Id);
        AddArg(message, Microsoft365CopilotDefaults.ArgAadObjectId, activity.From?.AadObjectId);
        AddArg(message, Microsoft365CopilotDefaults.ArgTenantId,
            activity.From?.TenantId ?? activity.Conversation?.TenantId);
        AddArg(message, Microsoft365CopilotDefaults.ArgUserName, activity.From?.Name);
        AddArg(message, Microsoft365CopilotDefaults.ArgLocale, activity.Locale);
        AddArg(message, Microsoft365CopilotDefaults.ArgDeliveryEndpointId, deliveryEndpointId);

        if (_copilotOptions.UserAuthorization.PassUserTokenToAgent && userToken is not null)
        {
            AddArg(message, _copilotOptions.UserAuthorization.UserTokenArgName, userToken);
        }

        message.StampFromActivity(System.Diagnostics.Activity.Current);
    }

    private static void AddArg(AgentMessage message, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            message.Args![key] = value;
        }
    }

    private async Task<string?> TryGetUserTokenAsync(ITurnContext turnContext, CancellationToken cancellationToken)
    {
        if (!_copilotOptions.UserAuthorizationConfigured || Options.UserAuthorization is null)
        {
            return null;
        }

        try
        {
            return await UserAuthorization.GetTurnTokenAsync(turnContext, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No user token available for activity {ActivityId}", turnContext.Activity.Id);
            return null;
        }
    }

    private async Task HandleTurnErrorAsync(ITurnContext turnContext, ITurnState turnState, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled error processing Copilot activity {ActivityId}", turnContext.Activity?.Id);
        try
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(_copilotOptions.ErrorMessage), cancellationToken);
        }
        catch
        {
            // The channel may already be gone; nothing further to do.
        }
    }
}

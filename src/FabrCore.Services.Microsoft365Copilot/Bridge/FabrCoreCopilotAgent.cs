using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// The Microsoft 365 Agents SDK application that bridges Copilot/Teams conversations to FabrCore
/// agents: each inbound message activity is mapped to a FabrCore principal, routed to that
/// principal's configured agent through <see cref="IFabrCoreAgentService"/>, and the agent's reply
/// is returned to the channel (streamed on streaming-capable channels).
/// </summary>
public class FabrCoreCopilotAgent : AgentApplication
{
    private readonly IFabrCoreAgentService _agentService;
    private readonly ICopilotPrincipalResolver _principalResolver;
    private readonly ICopilotAgentProvisioner _provisioner;
    private readonly Microsoft365CopilotOptions _copilotOptions;
    private readonly ILogger<FabrCoreCopilotAgent> _logger;

    public FabrCoreCopilotAgent(
        AgentApplicationOptions options,
        IFabrCoreAgentService agentService,
        ICopilotPrincipalResolver principalResolver,
        ICopilotAgentProvisioner provisioner,
        IOptions<Microsoft365CopilotOptions> copilotOptions,
        ILogger<FabrCoreCopilotAgent> logger)
        : base(options)
    {
        _agentService = agentService;
        _principalResolver = principalResolver;
        _provisioner = provisioner;
        _copilotOptions = copilotOptions.Value;
        _logger = logger;

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
        if (string.IsNullOrEmpty(text))
        {
            return;
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

        var targetHandle = await _provisioner.EnsureAgentAsync(
            principalHandle, turnContext.Activity.Conversation?.Id, cancellationToken);

        var agentMessage = BuildAgentMessage(turnContext, text, userToken);

        var useStreaming = _copilotOptions.Streaming.Enabled
            && turnContext.StreamingResponse.IsStreamingChannel;

        if (useStreaming)
        {
            await RespondStreamingAsync(turnContext, principalHandle, targetHandle, agentMessage, cancellationToken);
        }
        else
        {
            await RespondBufferedAsync(turnContext, principalHandle, targetHandle, agentMessage, cancellationToken);
        }
    }

    private async Task RespondStreamingAsync(
        ITurnContext turnContext,
        string principalHandle,
        string targetHandle,
        AgentMessage agentMessage,
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
            var replyText = await InvokeFabrCoreAgentAsync(principalHandle, targetHandle, agentMessage);
            stream.QueueTextChunk(string.IsNullOrWhiteSpace(replyText)
                ? "The agent returned an empty response."
                : replyText);
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
        CancellationToken cancellationToken)
    {
        string replyText;
        try
        {
            replyText = await InvokeFabrCoreAgentAsync(principalHandle, targetHandle, agentMessage)
                ?? "The agent returned an empty response.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FabrCore agent {Principal}:{Handle} failed to answer Copilot message {ActivityId}",
                principalHandle, targetHandle, turnContext.Activity.Id);
            _provisioner.Invalidate(principalHandle, targetHandle);
            replyText = _copilotOptions.ErrorMessage;
        }

        await turnContext.SendActivityAsync(MessageFactory.Text(replyText), cancellationToken);
    }

    private async Task<string?> InvokeFabrCoreAgentAsync(
        string principalHandle, string targetHandle, AgentMessage agentMessage)
    {
        var reply = await _agentService.SendAndReceiveMessageAsync(principalHandle, targetHandle, agentMessage);

        if (reply is null)
        {
            return null;
        }

        if (reply.MessageType == SystemMessageTypes.Error)
        {
            throw new InvalidOperationException($"Agent returned an error: {reply.Message}");
        }

        return reply.Message;
    }

    private AgentMessage BuildAgentMessage(ITurnContext turnContext, string text, string? userToken)
    {
        var activity = turnContext.Activity;
        var message = new AgentMessage
        {
            Message = text,
            Kind = MessageKind.Request,
            Channel = Microsoft365CopilotDefaults.ChannelName,
            Args = new Dictionary<string, string>(),
        };

        AddArg(message, Microsoft365CopilotDefaults.ArgChannelId, activity.ChannelId?.ToString());
        AddArg(message, Microsoft365CopilotDefaults.ArgConversationId, activity.Conversation?.Id);
        AddArg(message, Microsoft365CopilotDefaults.ArgActivityId, activity.Id);
        AddArg(message, Microsoft365CopilotDefaults.ArgAadObjectId, activity.From?.AadObjectId);
        AddArg(message, Microsoft365CopilotDefaults.ArgTenantId,
            activity.From?.TenantId ?? activity.Conversation?.TenantId);
        AddArg(message, Microsoft365CopilotDefaults.ArgUserName, activity.From?.Name);
        AddArg(message, Microsoft365CopilotDefaults.ArgLocale, activity.Locale);

        if (_copilotOptions.UserAuthorization.PassUserTokenToAgent && userToken is not null)
        {
            AddArg(message, _copilotOptions.UserAuthorization.UserTokenArgName, userToken);
        }

        message.StampFromActivity(System.Diagnostics.Activity.Current);
        return message;
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

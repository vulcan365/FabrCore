using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

internal sealed class CopilotEndpointRegistry
{
    public int Version { get; set; } = 1;

    public List<CopilotConversationEndpoint> Endpoints { get; set; } = [];
}

internal sealed class CopilotConversationEndpoint
{
    public string EndpointId { get; set; } = string.Empty;

    public Dictionary<string, string> Claims { get; set; } = new(StringComparer.Ordinal);

    public string ConversationReferenceJson { get; set; } = string.Empty;

    public string ConversationType { get; set; } = string.Empty;

    public DateTimeOffset LastActiveUtc { get; set; }

    public bool Eligible { get; set; } = true;
}

public interface ICopilotConversationContextWriter
{
    Task<string?> TrackAsync(
        string principalHandle,
        ITurnContext turnContext,
        CancellationToken cancellationToken = default);

    Task MarkUnavailableAsync(
        string principalHandle,
        string endpointId,
        CancellationToken cancellationToken = default);
}

internal sealed class CopilotConversationContextWriter : ICopilotConversationContextWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly IPrincipalContextStore _contextStore;
    private readonly Microsoft365CopilotOptions _options;
    private readonly ILogger<CopilotConversationContextWriter> _logger;

    public CopilotConversationContextWriter(
        IPrincipalContextStore contextStore,
        IOptions<Microsoft365CopilotOptions> options,
        ILogger<CopilotConversationContextWriter> logger)
    {
        _contextStore = contextStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> TrackAsync(
        string principalHandle,
        ITurnContext turnContext,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Proactive.Enabled)
        {
            _logger.LogDebug(
                "Microsoft 365 proactive endpoint capture skipped because proactive delivery is disabled - Principal: {Principal}",
                principalHandle);
            return null;
        }

        var activity = turnContext.Activity;
        var conversationId = activity.Conversation?.Id;
        var conversationType = activity.Conversation?.ConversationType;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            _logger.LogWarning(
                "Microsoft 365 proactive endpoint capture rejected because the activity has no conversation id - Principal: {Principal}, ChannelId: {ChannelId}, ConversationType: {ConversationType}",
                principalHandle,
                activity.ChannelId?.ToString(),
                conversationType);
            return null;
        }

        if (string.IsNullOrWhiteSpace(conversationType))
        {
            _logger.LogWarning(
                "Microsoft 365 proactive endpoint capture rejected because the activity has no conversation type - Principal: {Principal}, ChannelId: {ChannelId}",
                principalHandle,
                activity.ChannelId?.ToString());
            return null;
        }

        if (!_options.Proactive.AllowedConversationTypes.Contains(
                conversationType,
                StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Microsoft 365 proactive endpoint capture skipped for disallowed conversation type - Principal: {Principal}, ChannelId: {ChannelId}, ConversationType: {ConversationType}, AllowedConversationTypes: {AllowedConversationTypes}",
                principalHandle,
                activity.ChannelId?.ToString(),
                conversationType,
                string.Join(',', _options.Proactive.AllowedConversationTypes));
            return null;
        }

        var tenantId = activity.From?.TenantId ?? activity.Conversation?.TenantId ?? string.Empty;
        var endpointId = CreateEndpointId(activity.ChannelId?.ToString(), tenantId, conversationId);
        var conversation = new Conversation(turnContext);
        var endpoint = new CopilotConversationEndpoint
        {
            EndpointId = endpointId,
            Claims = new Dictionary<string, string>(
                Conversation.ClaimsFromIdentity(conversation.Identity),
                StringComparer.Ordinal),
            ConversationReferenceJson = JsonSerializer.Serialize(
                conversation.Reference,
                ProtocolJsonSerializer.SerializationOptions),
            ConversationType = conversationType,
            LastActiveUtc = DateTimeOffset.UtcNow,
            Eligible = true
        };

        var gate = GetStripe(principalHandle);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await ReadRegistryAsync(principalHandle, cancellationToken);
            registry.Endpoints.RemoveAll(item =>
                string.Equals(item.EndpointId, endpointId, StringComparison.Ordinal));
            registry.Endpoints.Add(endpoint);
            registry.Endpoints = registry.Endpoints
                .OrderByDescending(item => item.LastActiveUtc)
                .Take(_options.Proactive.MaxStoredEndpoints)
                .ToList();

            await WriteRegistryAsync(principalHandle, registry, cancellationToken);

            _logger.LogInformation(
                "Microsoft 365 proactive endpoint captured - Principal: {Principal}, Endpoint: {Endpoint}, ChannelId: {ChannelId}, ConversationType: {ConversationType}, StoredEndpoints: {StoredEndpoints}",
                principalHandle,
                ShortEndpointId(endpointId),
                activity.ChannelId?.ToString(),
                conversationType,
                registry.Endpoints.Count);
        }
        finally
        {
            gate.Release();
        }

        return endpointId;
    }

    public async Task MarkUnavailableAsync(
        string principalHandle,
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetStripe(principalHandle);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await ReadRegistryAsync(principalHandle, cancellationToken);
            var endpoint = registry.Endpoints.FirstOrDefault(item =>
                string.Equals(item.EndpointId, endpointId, StringComparison.Ordinal));
            if (endpoint is null)
            {
                _logger.LogWarning(
                    "Microsoft 365 proactive endpoint could not be marked unavailable because it was not found - Principal: {Principal}, Endpoint: {Endpoint}, StoredEndpoints: {StoredEndpoints}",
                    principalHandle,
                    ShortEndpointId(endpointId),
                    registry.Endpoints.Count);
                return;
            }

            if (!endpoint.Eligible)
            {
                _logger.LogDebug(
                    "Microsoft 365 proactive endpoint is already unavailable - Principal: {Principal}, Endpoint: {Endpoint}",
                    principalHandle,
                    ShortEndpointId(endpointId));
                return;
            }

            endpoint.Eligible = false;
            await WriteRegistryAsync(principalHandle, registry, cancellationToken);
            _logger.LogWarning(
                "Microsoft 365 proactive endpoint marked unavailable - Principal: {Principal}, Endpoint: {Endpoint}",
                principalHandle,
                ShortEndpointId(endpointId));
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool TryParseRegistry(string? json, out CopilotEndpointRegistry registry)
    {
        registry = new CopilotEndpointRegistry();
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            registry = JsonSerializer.Deserialize<CopilotEndpointRegistry>(json, JsonOptions)
                ?? new CopilotEndpointRegistry();
            registry.Endpoints ??= [];
            return registry.Version == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string SerializeRegistry(CopilotEndpointRegistry registry) =>
        JsonSerializer.Serialize(registry, JsonOptions);

    internal static string CreateEndpointId(string? channelId, string tenantId, string conversationId)
    {
        var source = string.Join('|', channelId ?? string.Empty, tenantId, conversationId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
    }

    private async Task<CopilotEndpointRegistry> ReadRegistryAsync(
        string principalHandle,
        CancellationToken cancellationToken)
    {
        var json = await _contextStore.GetAsync(
            principalHandle,
            Microsoft365CopilotDefaults.ProactiveEndpointsContextKey,
            cancellationToken);
        if (TryParseRegistry(json, out var registry))
        {
            return registry;
        }

        _logger.LogWarning(
            "Ignoring malformed Microsoft 365 proactive endpoint registry for principal {Principal}",
            principalHandle);
        return new CopilotEndpointRegistry();
    }

    private Task WriteRegistryAsync(
        string principalHandle,
        CopilotEndpointRegistry registry,
        CancellationToken cancellationToken) =>
        _contextStore.SetAsync(
            principalHandle,
            Microsoft365CopilotDefaults.ProactiveEndpointsContextKey,
            SerializeRegistry(registry),
            cancellationToken);

    private SemaphoreSlim GetStripe(string principalHandle) =>
        _stripes[(principalHandle.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % _stripes.Length];

    internal static string ShortEndpointId(string? endpointId) =>
        string.IsNullOrWhiteSpace(endpointId)
            ? "(none)"
            : endpointId[..Math.Min(12, endpointId.Length)];
}

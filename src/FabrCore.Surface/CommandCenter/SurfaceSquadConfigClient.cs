using FabrCore.Surface.Ai.Swarm;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceSquadConfigClient : ISurfaceSquadConfigClient
{
    private const string Container = "surface";
    private const string EntityKey = "command-center/squads";

    private static readonly JsonSerializerOptions JsonOptions = new(SurfaceJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly SurfaceOptions options;
    private readonly ILogger<SurfaceSquadConfigClient> logger;

    public SurfaceSquadConfigClient(
        HttpClient httpClient,
        IOptions<SurfaceOptions> options,
        ILogger<SurfaceSquadConfigClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<SurfaceSquad>> GetAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url = BuildUrl();
        logger.LogDebug("Loading Surface swarm squads from {Url}.", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOwnerHeaders(request, principalId);

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<SurfaceSquadConfigState>(JsonOptions, cancellationToken);
        return NormalizeSquads(state?.Squads);
    }

    public async Task SaveAsync(
        string principalId,
        IReadOnlyList<SurfaceSquad> squads,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url = BuildUrl();
        logger.LogDebug("Saving Surface swarm squads to {Url}.", url);

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        AddOwnerHeaders(request, principalId);
        request.Content = JsonContent.Create(
            new SurfaceSquadConfigState { Squads = NormalizeSquads(squads) },
            options: JsonOptions);

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private string BuildUrl()
    {
        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(SurfaceOptions.FabrCoreHostUrl)} must be configured before loading Surface swarm squads.");
        }

        return $"{options.FabrCoreHostUrl.TrimEnd('/')}/fabrcoreapi/Storage/{Container}/{EntityKey}";
    }

    private static void AddOwnerHeaders(HttpRequestMessage request, string principalId)
    {
        request.Headers.TryAddWithoutValidation("x-user", principalId);
        request.Headers.TryAddWithoutValidation("x-user-handle", principalId);
    }

    private static List<SurfaceSquad> NormalizeSquads(IEnumerable<SurfaceSquad>? squads)
        => squads?
               .Where(squad => !string.IsNullOrWhiteSpace(squad.OrchestratorHandle))
               .Select(CloneSquad)
               .ToList()
           ?? [];

    private static SurfaceSquad CloneSquad(SurfaceSquad squad)
        => new()
        {
            SquadType = squad.SquadType,
            Name = squad.Name,
            Slug = squad.Slug,
            PrincipalHandle = squad.PrincipalHandle,
            OrchestratorHandle = squad.OrchestratorHandle,
            PlannerHandle = squad.PlannerHandle,
            Description = squad.Description,
            TaskOptions = CloneTaskOptions(squad.TaskOptions),
            Agents = squad.Agents.Select(agent => new SurfaceSquadAgent
            {
                Name = agent.Name,
                Handle = agent.Handle,
                AgentType = agent.AgentType,
                Role = agent.Role,
                Description = agent.Description
            }).ToList()
        };

    private static SurfaceTaskSquadOptions CloneTaskOptions(SurfaceTaskSquadOptions? options)
        => new()
        {
            FastModelName = string.IsNullOrWhiteSpace(options?.FastModelName) ? "default" : options.FastModelName.Trim(),
            WorkerModelName = string.IsNullOrWhiteSpace(options?.WorkerModelName) ? "default" : options.WorkerModelName.Trim(),
            PlannerModelName = string.IsNullOrWhiteSpace(options?.PlannerModelName) ? "default" : options.PlannerModelName.Trim(),
            PersonaPrompt = string.IsNullOrWhiteSpace(options?.PersonaPrompt) ? null : options.PersonaPrompt.Trim(),
            ClientAgentOverlay = string.IsNullOrWhiteSpace(options?.ClientAgentOverlay) ? null : options.ClientAgentOverlay.Trim(),
            DelegationTimeoutSeconds = options?.DelegationTimeoutSeconds > 0 ? options.DelegationTimeoutSeconds : 120,
            MaxTaskAttempts = options?.MaxTaskAttempts > 0 ? options.MaxTaskAttempts : 2,
            MaxValidationAttempts = options?.MaxValidationAttempts > 0 ? options.MaxValidationAttempts : 2
        };

    private sealed class SurfaceSquadConfigState
    {
        public int Version { get; set; } = 1;

        public List<SurfaceSquad> Squads { get; set; } = [];
    }
}

using FabrCore.Core;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Swarm;

public sealed record SurfaceSwarmSmeAnswer(string Answer, string SmeName);

/// <summary>
/// Stateless per-use helper that consults SubjectMatterExpert-role squad members
/// over the Swarm conversation bus. Dead or slow SMEs are logged and skipped so a
/// single unavailable agent never blocks planning or supervision.
/// </summary>
public sealed class SurfaceSwarmSmeConsultant
{
    private readonly SurfaceSwarmSquadConversationBus bus;
    private readonly SurfaceSwarmSquadRuntime runtime;
    private readonly string fromHandle;
    private readonly TimeSpan timeout;
    private readonly ILogger logger;

    public SurfaceSwarmSmeConsultant(
        SurfaceSwarmSquadConversationBus bus,
        SurfaceSwarmSquadRuntime runtime,
        string fromHandle,
        TimeSpan timeout,
        ILogger logger)
    {
        this.bus = bus;
        this.runtime = runtime;
        this.fromHandle = fromHandle;
        this.timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : timeout;
        this.logger = logger;
    }

    public IReadOnlyList<SurfaceSwarmSquadAgent> Smes
        => runtime.Squad.Agents
            .Where(agent => agent.Role == SurfaceSwarmSquadMemberRole.SubjectMatterExpert)
            .ToList();

    public async Task<SurfaceSwarmSmeAnswer?> ConsultAsync(
        SurfaceSwarmSquadAgent sme,
        string question,
        string? context = null)
    {
        try
        {
            var request = new AgentMessage
            {
                FromHandle = fromHandle,
                ToHandle = sme.Handle,
                MessageType = SurfaceSwarmMessageTypes.SmeConsultation,
                Kind = MessageKind.Request,
                Message = question,
                State = new Dictionary<string, string>(),
                Args = new Dictionary<string, string>
                {
                    [SurfaceSwarmArgs.AgentName] = sme.Name
                }
            };

            if (!string.IsNullOrEmpty(context))
            {
                request.State["context"] = context;
            }

            var response = await bus.SendAndReceiveAsync(request, timeout);
            var status = response.State?.GetValueOrDefault("sme-status", string.Empty);
            var text = response.Message?.Trim();

            if (!string.IsNullOrWhiteSpace(text)
                && !string.Equals(status, "unknown", StringComparison.OrdinalIgnoreCase)
                && !text.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Swarm SME '{SmeName}' answered consultation", sme.Name);
                return new SurfaceSwarmSmeAnswer(text, sme.Name);
            }

            logger.LogDebug("Swarm SME '{SmeName}' could not help (status={Status})", sme.Name, status);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Swarm SME consultation failed for '{SmeName}' — skipping", sme.Name);
            return null;
        }
    }

    /// <summary>Queries SMEs sequentially and returns the first usable answer.</summary>
    public async Task<SurfaceSwarmSmeAnswer?> ConsultAsync(string question, string? context = null)
    {
        foreach (var sme in Smes)
        {
            var answer = await ConsultAsync(sme, question, context);
            if (answer is not null)
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>Queries all SMEs in parallel and returns every usable answer.</summary>
    public async Task<List<SurfaceSwarmSmeAnswer>> ConsultAllAsync(string question, string? context = null)
    {
        var smes = Smes;
        if (smes.Count == 0)
        {
            return [];
        }

        var results = await Task.WhenAll(smes.Select(sme => ConsultAsync(sme, question, context)));
        return results.Where(answer => answer is not null).Cast<SurfaceSwarmSmeAnswer>().ToList();
    }
}

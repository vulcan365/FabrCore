using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Host.Grains;

internal sealed class BlueprintAdministrationGrain(IUserScopedFabrCoreStorageProvider storage, IFabrCoreAgentService agents,
    IEnumerable<IBlueprintExpander> expanders, IFabrCoreConfigurationStore models, ILogger<FabrCoreBlueprintService> logger) : Grain, IBlueprintAdministrationGrain
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IFabrCoreBlueprintService Local => new LocalFabrCoreBlueprintService(agents, expanders, storage, logger);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    public async Task<string> ExecuteAsync(string operation, string name, string body, string? revision)
    {
        var principal = this.GetPrimaryKeyString();
        if (operation == "apply")
            return JsonSerializer.Serialize(await Local.ApplyAsync(principal, JsonSerializer.Deserialize<FabrCoreBlueprint>(body, Json)!,
                revision is null ? HealthDetailLevel.Basic : (HealthDetailLevel)int.Parse(revision, System.Globalization.CultureInfo.InvariantCulture)), Json);
        var catalog = await ReadCatalogAsync(principal);
        if (operation == "cloud")
        {
            var delivery = JsonSerializer.Deserialize<CloudBlueprintDeployment>(body, Json)!;
            await SaveCatalogAsync(principal, catalog, delivery.Blueprint);
            if (!delivery.ApplyOnRefresh) return Result(200, new { status = "saved", revision = Hash(delivery.Blueprint) });
            var expanded = await Expand(principal, delivery.Blueprint);
            var deployment = new BlueprintDeploymentRequest { OperationId = Hash(new { delivery.DeploymentId, revision = Hash(delivery.Blueprint), delivery.ApplyMode }),
                Mode = delivery.ApplyMode, Revision = Hash(delivery.Blueprint), ExpansionDigest = Hash(expanded) };
            return await ExecuteAsync("deploy", delivery.Blueprint.Name!, JsonSerializer.Serialize(deployment, Json), null);
        }
        if (operation == "list") return JsonSerializer.Serialize(catalog.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList(), Json);
        if (operation is "summaries" or "summary-page")
        {
            var deployments = await storage.GetAsync<Dictionary<string, BlueprintSummary>>(principal, "fabrcore.deployment-summary", "index") ?? new();
            var summaries = new List<BlueprintSummary>();
            foreach (var n in catalog.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                var b = catalog[n];
                if (b is not null)
                {
                    deployments.TryGetValue(n, out var deployed);
                    summaries.Add(new() { Name = n, Description = b.Description, Version = b.Version, Revision = Hash(b),
                        DeployedRevision = deployed?.DeployedRevision, DeploymentStatus = deployed?.DeploymentStatus,
                        LastDeploymentId = deployed?.LastDeploymentId, DefinitionDrift = deployed?.DeployedRevision is null ? null : deployed.DeployedRevision != Hash(b) });
                }
            }
            if (operation == "summaries") return JsonSerializer.Serialize(summaries, Json);
            var pageRequest = JsonSerializer.Deserialize<AgentManagementRequest>(body, Json)!;
            var catalogRevision = Hash(summaries);
            if (pageRequest.Revision is not null && pageRequest.Revision != catalogRevision)
                return Result(412, new { error = "Blueprint catalog changed; restart pagination." });
            var offset = Math.Max(0, pageRequest.Offset); var limit = Math.Clamp(pageRequest.Limit, 1, 1000);
            return Result(200, new AdministrationPage<BlueprintSummary> { Items = summaries.Skip(offset).Take(limit).ToList(),
                Revision = catalogRevision, NextCursor = offset + limit < summaries.Count ? (offset + limit).ToString() : null });
        }
        var existing = catalog.TryGetValue(name, out var found) ? JsonSerializer.Deserialize<FabrCoreBlueprint>(JsonSerializer.Serialize(found, Json), Json) : null;
        if (operation == "get") return JsonSerializer.Serialize(existing, Json);
        if (revision is not null && revision != (existing is null ? "*" : Hash(existing)))
            return Result(412, new { error = "Blueprint changed; refresh before applying edits." });
        if (operation == "delete") { var removed = catalog.Remove(name); if (removed) await storage.UpsertAsync(principal, "fabrcore.blueprint-catalog", "definitions", catalog); return JsonSerializer.Serialize(removed, Json); }
        var blueprint = operation is "preview" or "deploy" ? existing : JsonSerializer.Deserialize<FabrCoreBlueprint>(body, Json);
        if (blueprint is null) return Result(404, new { error = "Blueprint not found." });
        if (operation == "save") { await SaveCatalogAsync(principal, catalog, blueprint); return Result(200, new { revision = Hash(blueprint) }); }
        var configurations = await Expand(principal, blueprint);
        var preview = new BlueprintPreview { Revision = Hash(blueprint), ExpansionDigest = Hash(configurations), Agents = configurations };
        if (operation is "preview" or "validate") return Result(200, preview);
        if (operation != "deploy") return Result(400, new { error = "Unknown blueprint operation." });
        var request = JsonSerializer.Deserialize<BlueprintDeploymentRequest>(body, Json)!;
        if (request.Mode is not ("ensure" or "update") || string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128)
            return Result(400, new { error = "Mode must be ensure/update and operationId is required (maximum 128 characters)." });
        if (request.Revision != preview.Revision || request.ExpansionDigest != preview.ExpansionDigest)
            return Result(412, new { error = "Definition or expansion changed; preview again." });
        if (request.AgentHandles.Count > 0)
        {
            var selected = request.AgentHandles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count != request.AgentHandles.Count || selected.Any(h => !configurations.Any(c => string.Equals(c.Handle, h, StringComparison.OrdinalIgnoreCase))))
                return Result(400, new { error = "Selected retry handles must be unique members of the reviewed expansion." });
            configurations = configurations.Where(c => selected.Contains(c.Handle!)).ToList();
        }
        var receiptKey = Hash(new { name, request.OperationId });
        var prior = await storage.GetAsync<JsonElement?>(principal, "fabrcore.deployments", receiptKey);
        if (prior is not null)
        {
            if (prior.Value.TryGetProperty("request", out var previousRequest) &&
                JsonSerializer.Serialize(previousRequest.Deserialize<BlueprintDeploymentRequest>(Json), Json) != JsonSerializer.Serialize(request, Json))
                return Result(409, new { error = "Deployment ID is already bound to another revision or mode." });
            if (prior.Value.TryGetProperty("status", out var status) && status.GetString() == "running")
                return Result(409, new { status = "incomplete", error = "Deployment interrupted; inspect agents before explicitly retrying." });
            return Result(200, prior);
        }
        await storage.UpsertAsync(principal, "fabrcore.deployments", receiptKey, new { status = "running", request, startedAt = DateTimeOffset.UtcNow });
        // The non-reentrant grain serializes deployments across all connected silos.
        foreach (var c in configurations) c.ForceReconfigure = request.Mode == "update";
        var results = request.Mode == "update" ? await agents.ConfigureAgentsAsync(principal, configurations) : await agents.EnsureAgentsAsync(principal, configurations);
        var receipt = new { status = results.All(r => r.State == HealthState.Healthy) ? "applied" : "partial-failure", request, results, completedAt = DateTimeOffset.UtcNow };
        await storage.UpsertAsync(principal, "fabrcore.deployments", receiptKey, receipt);
        var deploymentIndex = await storage.GetAsync<Dictionary<string, BlueprintSummary>>(principal, "fabrcore.deployment-summary", "index") ?? new();
        deploymentIndex[name] = new() { Name = name, DeployedRevision = request.Revision,
            DeploymentStatus = request.AgentHandles.Count > 0 ? "retry-completed" : receipt.status, LastDeploymentId = request.OperationId };
        await storage.UpsertAsync(principal, "fabrcore.deployment-summary", "index", deploymentIndex);
        return Result(200, receipt);
    }

    private async Task<Dictionary<string, FabrCoreBlueprint>> ReadCatalogAsync(string principal)
    {
        var catalog = await storage.GetAsync<Dictionary<string, FabrCoreBlueprint>>(principal, "fabrcore.blueprint-catalog", "definitions");
        if (catalog is not null) return new(catalog, StringComparer.OrdinalIgnoreCase);
        catalog = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in await Local.ListAsync(principal))
        {
            var blueprint = await Local.GetAsync(principal, name);
            if (blueprint is not null) catalog[name] = blueprint;
        }
        return catalog;
    }
    private async Task SaveCatalogAsync(string principal, Dictionary<string, FabrCoreBlueprint> catalog, FabrCoreBlueprint blueprint)
    {
        var name = blueprint.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Blueprint name must contain 1–128 letters, digits, '.', '-' or '_'.");
        blueprint.Name = name; catalog[name] = blueprint;
        // The catalog and definitions commit in one storage write, avoiding orphaned index entries.
        await storage.UpsertAsync(principal, "fabrcore.blueprint-catalog", "definitions", catalog);
    }
    private async Task<List<AgentConfiguration>> Expand(string principal, FabrCoreBlueprint blueprint)
    {
        var result = JsonSerializer.Deserialize<List<AgentConfiguration>>(JsonSerializer.Serialize(blueprint.Agents, Json), Json)!;
        foreach (var (key, value) in blueprint.Extensions)
        {
            var expander = expanders.SingleOrDefault(e => e.ExtensionKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Blueprint extension '{key}' is unavailable.");
            if (expander is not IBlueprintPreviewExpander) throw new ArgumentException($"Blueprint extension '{key}' does not advertise side-effect-free preview.");
            result.AddRange((await expander.ExpandAsync(new() { PrincipalId = principal, Blueprint = blueprint }, value)).Agents);
        }
        if (result.Count == 0) throw new ArgumentException("Blueprint contains no agents.");
        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelNames = (await models.GetConfigurationAsync()).ModelConfigurations.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        static bool Available(IEnumerable<FabrCore.Sdk.RegistryEntry> entries, string name) => entries.Any(e => e.TypeName == name || e.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));
        foreach (var c in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(c.Handle);
            var parsed = HandleUtilities.ParseHandle(c.Handle);
            if (!string.IsNullOrEmpty(parsed.UserHandle) && parsed.UserHandle != principal) throw new ArgumentException("Cross-principal blueprint handles are prohibited.");
            if (!handles.Add(parsed.AgentHandle)) throw new ArgumentException("Duplicate expanded agent handle: " + parsed.AgentHandle);
            c.Handle = parsed.AgentHandle; c.ForceReconfigure = false;
            ArgumentException.ThrowIfNullOrWhiteSpace(c.AgentType);
            if (!Available(agents.GetAgentTypes(), c.AgentType)) throw new ArgumentException("Agent type is not installed: " + c.AgentType);
            if (!string.IsNullOrWhiteSpace(c.Models) && !modelNames.Contains(c.Models)) throw new ArgumentException("Model alias is unavailable: " + c.Models);
            foreach (var plugin in c.Plugins) if (!Available(agents.GetPlugins(), plugin)) throw new ArgumentException("Plugin is not installed: " + plugin);
            foreach (var tool in c.Tools) if (!Available(agents.GetTools(), tool)) throw new ArgumentException("Tool is not installed: " + tool);
        }
        return result;
    }
    private static string Result(int status, object value) => JsonSerializer.Serialize(new AdminApiResult { StatusCode = status, Body = JsonSerializer.SerializeToElement(value, Json) }, Json);
}

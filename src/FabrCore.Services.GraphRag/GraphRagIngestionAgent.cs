using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

[AgentAlias("graph-rag-ingestion-agent")]
[Description("Downloads FabrCore message files, converts them to Markdown, and ingests them into authorized GraphRAG scopes.")]
[FabrCoreCapabilities("Ingests one or more FabrCore temporary file attachments into one or more authorized GraphRAG scopes with optional extraction guidance.")]
[FabrCoreNote("Configure Args[\"AllowedScopes\"] with a comma-separated trusted allow-list.")]
[FabrCoreNote("Every request must include Files and Args[\"Scope\"] or Args[\"Scopes\"]. The message text is optional extraction guidance.")]
public sealed class GraphRagIngestionAgent : FabrCoreAgentProxy
{
    public const string AllowedScopesArgKey = "AllowedScopes";
    public const string ScopeArgKey = "Scope";
    public const string ScopesArgKey = "Scopes";
    public const string StatusResponseArgKey = "GraphRagIngestion.Status";
    public const string SucceededResponseArgKey = "GraphRagIngestion.Succeeded";
    public const string FailedResponseArgKey = "GraphRagIngestion.Failed";
    public const string ScopesResponseArgKey = "GraphRagIngestion.Scopes";
    public const string ResultsResponseArgKey = "GraphRagIngestion.Results";

    private HashSet<string> _allowedScopes = new(StringComparer.OrdinalIgnoreCase);
    private IFabrCoreHostApiClient? _hostApiClient;
    private IMarkdownConversionService? _markdownConverter;
    private IKnowledgeIngestionService? _ingestion;
    private IKnowledgeScopeService? _scopeService;

    public GraphRagIngestionAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override Task OnInitialize()
    {
        var allowedRaw = GetArg(config.Args, AllowedScopesArgKey);
        _allowedScopes = ParseScopeList(allowedRaw);
        if (_allowedScopes.Count == 0)
        {
            throw new InvalidOperationException(
                $"GraphRagIngestionAgent requires Args[\"{AllowedScopesArgKey}\"] with at least one authorized scope key.");
        }

        _hostApiClient = serviceProvider.GetRequiredService<IFabrCoreHostApiClient>();
        _markdownConverter = serviceProvider.GetRequiredService<IMarkdownConversionService>();
        _ingestion = serviceProvider.GetRequiredService<IKnowledgeIngestionService>();
        _scopeService = serviceProvider.GetRequiredService<IKnowledgeScopeService>();
        return Task.CompletedTask;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        response.Args ??= new Dictionary<string, string>();

        try
        {
            if (!TryResolveRequestedScopes(message.Args, out var scopes, out var validationError))
                return Reject(response, validationError!);

            var unauthorized = scopes.Where(scope => !_allowedScopes.Contains(scope)).ToArray();
            if (unauthorized.Length > 0)
            {
                return Reject(response,
                    $"GraphRAG ingestion was not started. Unauthorized scope(s): {string.Join(", ", unauthorized)}.");
            }

            foreach (var scope in scopes)
            {
                if (!await _scopeService!.ScopeExistsAsync(scope))
                    return Reject(response, $"GraphRAG ingestion was not started. Scope '{scope}' is not registered.");
            }

            var fileIds = (message.Files ?? [])
                .Where(fileId => !string.IsNullOrWhiteSpace(fileId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (fileIds.Length == 0)
                return Reject(response, "GraphRAG ingestion was not started. Attach at least one FabrCore file ID in Files.");

            var results = new List<GraphRagIngestionItemResult>();
            var instructions = string.IsNullOrWhiteSpace(message.Message) ? null : message.Message.Trim();

            for (var fileIndex = 0; fileIndex < fileIds.Length; fileIndex++)
            {
                var fileId = fileIds[fileIndex];
                SetStatusMessage($"Converting file {fileIndex + 1} of {fileIds.Length}...");

                FileMetadataResponse? metadata;
                try
                {
                    metadata = await _hostApiClient!.GetFileInfoAsync(fileId);
                    if (metadata is null)
                    {
                        results.Add(GraphRagIngestionItemResult.Failure(fileId, null, null,
                            "The FabrCore file was not found or has expired."));
                        continue;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results.Add(GraphRagIngestionItemResult.Failure(fileId, null, null,
                        $"Could not read FabrCore file metadata: {ex.Message}"));
                    continue;
                }

                string markdown;
                try
                {
                    await using var stream = await _hostApiClient!.GetFileAsync(fileId);
                    if (stream is null)
                    {
                        results.Add(GraphRagIngestionItemResult.Failure(
                            fileId, metadata.OriginalFileName, null,
                            "The FabrCore file was not found or has expired."));
                        continue;
                    }

                    markdown = await _markdownConverter!.ConvertAsync(
                        stream, metadata.OriginalFileName, metadata.ContentType);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results.Add(GraphRagIngestionItemResult.Failure(
                        fileId, metadata.OriginalFileName, null,
                        $"Markdown conversion failed: {ex.Message}"));
                    continue;
                }

                foreach (var scope in scopes)
                {
                    SetStatusMessage($"Ingesting {metadata.OriginalFileName} into {scope}...");
                    try
                    {
                        var document = await _ingestion!.IngestDocumentAsync(
                            new KnowledgeIngestionRequest(
                                metadata.OriginalFileName,
                                scope,
                                markdown,
                                instructions));

                        results.Add(new GraphRagIngestionItemResult(
                            fileId,
                            metadata.OriginalFileName,
                            scope,
                            true,
                            document.Reused ? "Reused" : document.Status,
                            document.DocumentId,
                            null));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        results.Add(GraphRagIngestionItemResult.Failure(
                            fileId, metadata.OriginalFileName, scope, ex.Message));
                    }
                }
            }

            return Complete(response, scopes, results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GraphRAG ingestion request failed before batch completion");
            response.Message = $"GraphRAG ingestion failed: {ex.Message}";
            response.Args[StatusResponseArgKey] = "Failed";
            response.Args[SucceededResponseArgKey] = "0";
            response.Args[FailedResponseArgKey] = "0";
            response.Args[ResultsResponseArgKey] = "[]";
            return response;
        }
        finally
        {
            SetStatusMessage(null);
        }
    }

    public override Task OnEvent(EventMessage eventMessage) => Task.CompletedTask;

    private bool TryResolveRequestedScopes(
        IReadOnlyDictionary<string, string>? args,
        out string[] scopes,
        out string? error)
    {
        var singular = ParseScopeList(GetArg(args, ScopeArgKey));
        var plural = ParseScopeList(GetArg(args, ScopesArgKey));

        if (singular.Count > 0 && plural.Count > 0 && !singular.SetEquals(plural))
        {
            scopes = [];
            error = "GraphRAG ingestion was not started because Scope and Scopes contain conflicting values.";
            return false;
        }

        var resolved = plural.Count > 0 ? plural : singular;
        if (resolved.Count == 0)
        {
            scopes = [];
            error = "GraphRAG ingestion was not started. Add Args[\"Scope\"] or Args[\"Scopes\"] with at least one authorized scope key.";
            return false;
        }

        scopes = resolved.OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase).ToArray();
        error = null;
        return true;
    }

    private static string? GetArg(IReadOnlyDictionary<string, string>? args, string key)
        => args?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static HashSet<string> ParseScopeList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static AgentMessage Reject(AgentMessage response, string error)
    {
        response.Message = error;
        response.Args![StatusResponseArgKey] = "Rejected";
        response.Args[SucceededResponseArgKey] = "0";
        response.Args[FailedResponseArgKey] = "0";
        response.Args[ResultsResponseArgKey] = "[]";
        return response;
    }

    private static AgentMessage Complete(
        AgentMessage response,
        IReadOnlyList<string> scopes,
        IReadOnlyList<GraphRagIngestionItemResult> results)
    {
        var succeeded = results.Count(result => result.Succeeded);
        var failed = results.Count - succeeded;
        var status = succeeded > 0 && failed == 0
            ? "Succeeded"
            : succeeded > 0 ? "PartiallySucceeded" : "Failed";

        var summary = new StringBuilder()
            .Append("GraphRAG ingestion ").Append(status.ToLowerInvariant()).Append(": ")
            .Append(succeeded).Append(" succeeded, ").Append(failed).Append(" failed.");
        foreach (var result in results)
        {
            summary.AppendLine().Append("- ")
                .Append(result.FileName ?? result.FileId);
            if (result.Scope is not null) summary.Append(" [").Append(result.Scope).Append(']');
            summary.Append(": ").Append(result.Succeeded ? result.Status : result.Error);
        }

        response.Message = summary.ToString();
        response.Args![StatusResponseArgKey] = status;
        response.Args[SucceededResponseArgKey] = succeeded.ToString();
        response.Args[FailedResponseArgKey] = failed.ToString();
        response.Args[ScopesResponseArgKey] = string.Join(',', scopes);
        response.Args[ResultsResponseArgKey] = JsonSerializer.Serialize(results);
        return response;
    }
}

public sealed record GraphRagIngestionItemResult(
    string FileId,
    string? FileName,
    string? Scope,
    bool Succeeded,
    string Status,
    Guid? DocumentId,
    string? Error)
{
    internal static GraphRagIngestionItemResult Failure(
        string fileId, string? fileName, string? scope, string error)
        => new(fileId, fileName, scope, false, "Failed", null, error);
}

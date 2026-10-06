#pragma warning disable OPENAI001 // Responses error types are experimental in the OpenAI SDK.
using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace FabrCore.Sdk;

/// <summary>What a provider said when it refused a request for being larger than the model's context.</summary>
/// <param name="LimitTokens">The context limit the provider stated, when its message carried one.</param>
/// <param name="RequestTokens">The request size the provider measured, when its message carried one.</param>
/// <param name="Message">The provider's own wording, for diagnostics.</param>
internal readonly record struct ContextOverflowInfo(int? LimitTokens, long? RequestTokens, string Message);

/// <summary>
/// Recognizes a provider rejecting a request because the prompt does not fit the model's context.
/// </summary>
/// <remarks>
/// Deliberately conservative: an unrecognized rejection is left alone (it surfaces as the provider
/// error it always was), while a false match would trim and resend a request that failed for an
/// unrelated reason. The structured <c>context_length_exceeded</c> code covers OpenAI and Azure on
/// both chat APIs; the message phrases cover OpenAI-compatible providers that do not send it.
/// </remarks>
internal static partial class ContextOverflow
{
    internal const string ErrorCode = "context_length_exceeded";

    /// <summary>Classifies a failed provider call.</summary>
    public static bool TryClassify(Exception exception, out ContextOverflowInfo overflow)
    {
        overflow = default;
        if (exception is not ClientResultException failure || failure.Status is not (400 or 413 or 422))
            return false;

        // The SDK only understands OpenAI-shaped error bodies; for anything else its message is a
        // generic "Service request failed" and the provider's wording survives only in the body.
        var (code, param, message) = ReadErrorBody(failure);
        return TryClassify(code, param, message ?? failure.Message, out overflow);
    }

    /// <summary>Classifies an error reported in-band on a streaming response.</summary>
    public static bool TryClassify(ErrorContent error, out ContextOverflowInfo overflow)
        => TryClassify(error.ErrorCode, error.Details, error.Message, out overflow);

    /// <summary>Classifies a response that completed over HTTP but carries a failed status.</summary>
    public static bool TryClassify(ChatResponse response, out ContextOverflowInfo overflow)
    {
        overflow = default;
        return response.AdditionalProperties is { } properties
            && properties.TryGetValue("Error", out var value)
            && value is ResponseError error
            && TryClassify(error.Code.ToString(), null, error.Message, out overflow);
    }

    /// <summary>Classifies a streaming update that reports the response as failed.</summary>
    public static bool TryClassify(ChatResponseUpdate update, out ContextOverflowInfo overflow)
    {
        overflow = default;
        foreach (var content in update.Contents)
        {
            if (content is ErrorContent error && TryClassify(error, out overflow))
                return true;
        }

        return update.RawRepresentation is StreamingResponseFailedUpdate { Response.Error: { } failed }
            && TryClassify(failed.Code.ToString(), null, failed.Message, out overflow);
    }

    internal static bool TryClassify(string? code, string? param, string? message, out ContextOverflowInfo overflow)
    {
        overflow = default;

        // An oversized output request is a different problem: trimming the prompt would not fix it.
        if (param is not null && OutputLimitParameters.Contains(param))
            return false;

        message ??= string.Empty;
        var match = OverflowPhrases().Match(message);
        if (!match.Success && !string.Equals(code, ErrorCode, StringComparison.OrdinalIgnoreCase))
            return false;

        var limit = Number(match, "limit");
        var request = Number(match, "request") ?? Number(RequestSize().Match(message), "request");
        overflow = new ContextOverflowInfo(
            limit is > 0 and <= int.MaxValue ? (int)limit.Value : null,
            request is > 0 ? request : null,
            message);
        return true;
    }

    private static readonly HashSet<string> OutputLimitParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "max_tokens", "max_completion_tokens", "max_output_tokens"
    };

    // Each alternative is a phrase a provider uses only for an oversized prompt. Named groups pick up
    // the stated limit and request size where the phrase carries them.
    [GeneratedRegex(
        @"maximum context length is (?<limit>[\d,]+) tokens" +
        @"|exceeds? the context window" +
        @"|input tokens exceed the configured limit of (?<limit>[\d,]+) tokens" +
        @"|maximum prompt length is (?<limit>[\d,]+)" +
        @"|input token count \(?(?<request>[\d,]+)\)? exceeds the maximum number of tokens allowed \(?(?<limit>[\d,]+)\)?" +
        @"|prompt is too long: (?<request>[\d,]+) tokens > (?<limit>[\d,]+) maximum",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverflowPhrases();

    [GeneratedRegex(
        @"(?:resulted in|requested(?: about)?|request contains) (?<request>[\d,]+) tokens",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestSize();

    private static long? Number(Match match, string group)
        => match.Success
            && match.Groups[group] is { Success: true } captured
            && long.TryParse(captured.Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static (string? Code, string? Param, string? Message) ReadErrorBody(ClientResultException failure)
    {
        try
        {
            if (failure.GetRawResponse()?.Content is not { } content)
                return default;

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
                return default;

            return error.ValueKind switch
            {
                JsonValueKind.String => (null, null, error.GetString()),
                JsonValueKind.Object => (Text(error, "code"), Text(error, "param"), Text(error, "message")),
                _ => default
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON, or the body was not buffered: fall back to the exception message.
            return default;
        }
    }

    private static string? Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

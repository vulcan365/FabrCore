using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace FabrCore.Tests.Infrastructure;

/// <summary>
/// Example custom evaluator that counts words in the model response.
/// Demonstrates how to implement IEvaluator for domain-specific metrics.
/// Adapt this pattern for your own custom evaluations.
/// </summary>
public class WordCountEvaluator : IEvaluator
{
    public const string WordCountMetricName = "Words";

    public IReadOnlyCollection<string> EvaluationMetricNames => [WordCountMetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        int wordCount = CountWords(modelResponse.Text);

        string reason =
            $"The evaluated model response contained {wordCount} words.";

        var metric = new NumericMetric(WordCountMetricName, value: wordCount, reason);
        Interpret(metric);

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    private static int CountWords(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return 0;

        return Regex.Matches(input, @"\b\w+\b").Count;
    }

    private static void Interpret(NumericMetric metric)
    {
        if (metric.Value is null)
        {
            metric.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Unknown,
                failed: true,
                reason: "Failed to calculate word count.");
        }
        else if (metric.Value > 5 && metric.Value <= 100)
        {
            metric.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Good,
                reason: "Response was between 6 and 100 words.");
        }
        else
        {
            metric.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Unacceptable,
                failed: true,
                reason: "Response was either too short or greater than 100 words.");
        }
    }
}

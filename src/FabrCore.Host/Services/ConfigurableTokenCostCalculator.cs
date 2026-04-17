using FabrCore.Core.Monitoring;
using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Default <see cref="ITokenCostCalculator"/> backed by the
    /// <c>FabrCore:ModelPricing</c> configuration section. Expected shape:
    /// <code>
    /// "FabrCore": {
    ///   "ModelPricing": {
    ///     "gpt-4o":           { "InputPer1K": 0.0025, "OutputPer1K": 0.010, "CachedInputPer1K": 0.00125 },
    ///     "claude-opus-4-7":  { "InputPer1K": 0.015,  "OutputPer1K": 0.075 }
    ///   }
    /// }
    /// </code>
    /// Unknown models yield null (explicit "price unknown") rather than zero.
    /// </summary>
    internal sealed class ConfigurableTokenCostCalculator : ITokenCostCalculator
    {
        private readonly Dictionary<string, ModelPrice> _prices;

        public ConfigurableTokenCostCalculator(IConfiguration configuration)
        {
            _prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

            var section = configuration.GetSection("FabrCore:ModelPricing");
            foreach (var child in section.GetChildren())
            {
                if (string.IsNullOrWhiteSpace(child.Key)) continue;
                var price = new ModelPrice(
                    InputPer1K: ReadDecimal(child, "InputPer1K"),
                    OutputPer1K: ReadDecimal(child, "OutputPer1K"),
                    CachedInputPer1K: ReadNullableDecimal(child, "CachedInputPer1K"),
                    ReasoningPer1K: ReadNullableDecimal(child, "ReasoningPer1K"));
                _prices[child.Key] = price;
            }
        }

        public decimal? EstimateUsd(
            string? model,
            long inputTokens,
            long outputTokens,
            long cachedInputTokens = 0,
            long reasoningTokens = 0)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            if (!_prices.TryGetValue(model, out var p)) return null;

            // Cached-input is billed at a discounted rate when declared; otherwise counts as standard input.
            var billableInput = inputTokens - cachedInputTokens;
            if (billableInput < 0) billableInput = 0;

            var cost = billableInput / 1000m * p.InputPer1K
                       + outputTokens / 1000m * p.OutputPer1K;

            if (p.CachedInputPer1K.HasValue)
                cost += cachedInputTokens / 1000m * p.CachedInputPer1K.Value;
            else
                cost += cachedInputTokens / 1000m * p.InputPer1K;

            // Reasoning tokens default to the output rate when no dedicated price is configured.
            cost += reasoningTokens / 1000m * (p.ReasoningPer1K ?? p.OutputPer1K);

            return cost;
        }

        private static decimal ReadDecimal(IConfigurationSection section, string key)
            => decimal.TryParse(section[key], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

        private static decimal? ReadNullableDecimal(IConfigurationSection section, string key)
        {
            var raw = section[key];
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null;
        }

        private readonly record struct ModelPrice(
            decimal InputPer1K,
            decimal OutputPer1K,
            decimal? CachedInputPer1K,
            decimal? ReasoningPer1K);
    }
}

namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// Pluggable cost estimator. Given a model name and token counts, returns
    /// an estimated USD cost. Default FabrCore implementation reads a
    /// <c>FabrCore:ModelPricing</c> configuration section; hosts can replace
    /// with their own implementation for dynamic pricing or other currencies.
    /// </summary>
    public interface ITokenCostCalculator
    {
        /// <summary>
        /// Returns an estimated cost in USD for the given token counts. May
        /// return null if pricing for <paramref name="model"/> is unknown —
        /// callers should treat null as "don't display" rather than "free".
        /// </summary>
        decimal? EstimateUsd(
            string? model,
            long inputTokens,
            long outputTokens,
            long cachedInputTokens = 0,
            long reasoningTokens = 0);
    }
}

using RugPullShared;

namespace RugPullServer;

/// <summary>
/// Shared risk assessment logic used by both HTTP and MCP interfaces.
/// Risk level is derived from rug pull probability, not prediction confidence.
/// </summary>
public static class RiskResult
{
    public static object FromPrediction(RugPullPrediction prediction, RugPullData input)
    {
        string riskLevel = prediction.Probability switch
        {
            > 0.9f => "CRITICAL",
            > 0.75f => "HIGH",
            > 0.5f => "MEDIUM",
            _ => "LOW"
        };

        return new
        {
            prediction = prediction.IsRugPull ? "LIKELY RUG PULL" : "LIKELY LEGITIMATE",
            probability = prediction.Probability,
            riskLevel,
            details = new
            {
                rawScore = prediction.Score,
                addedLiquidity = input.TotalAddedLiquidity,
                removedLiquidity = input.TotalRemovedLiquidity,
                addRemoveRatio = input.AddToRemoveRatio,
                liquidityAdds = input.NumLiquidityAdds,
                liquidityRemoves = input.NumLiquidityRemoves
            }
        };
    }
}

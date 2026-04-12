namespace RugPullServer;

/// <summary>
/// Response records for the /api/analyze endpoint and the MCP analyze_token tool.
///
/// The top-level <see cref="AnalysisResponse"/> bundles three independent sections:
///   - <c>Token</c>: basic identity (name, symbol, mint address).
///   - <c>MlPrediction</c> / <c>MlError</c>: result of the ML inference path. Always
///     null in Story 4 — Story 5 wires in Helius feature extraction and the model.
///   - <c>MarketSignals</c> + <c>MarketData</c>: heuristic risk scoring and the raw
///     pool metrics it was computed from.
///   - <c>Agreement</c>: comparison between ML and heuristic verdicts. Always the
///     literal string "not_evaluated" until Story 5.
/// </summary>
public record TokenRef(string Name, string Symbol, string MintAddress);

public record MlPrediction(
    string Verdict,
    float Probability,
    float RawScore,
    string RiskLevel,
    MlFeatures Features,
    string FeaturesSource,
    string FeaturesCachedAt,
    ModelInfo Model);

public record MlFeatures(
    float TotalAddedLiquidity,
    float TotalRemovedLiquidity,
    float NumLiquidityAdds,
    float NumLiquidityRemoves,
    float AddToRemoveRatio);

public record ModelInfo(
    string Version,
    string TrainingDataset,
    float TrainingAccuracy,
    float TrainingAuc);

public record MarketSignalsBlock(
    string OverallRisk,
    int RiskScore,
    List<RiskFlag> Flags);

public record MarketData(
    string Dex,
    string PairAddress,
    double LiquidityUsd,
    double Volume24hUsd,
    double PriceUsd,
    double PriceChange24hPercent,
    int Buys24h,
    int Sells24h,
    string PoolCreatedAt,       // ISO 8601 string
    double PoolAgeHours,
    int TotalPools);

public record AnalysisResponse(
    TokenRef Token,
    MlPrediction? MlPrediction,
    string? MlError,
    MarketSignalsBlock MarketSignals,
    MarketData MarketData,
    string Agreement);

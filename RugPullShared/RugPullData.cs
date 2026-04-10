using Microsoft.ML.Data;

namespace RugPullShared;

/// <summary>
/// Maps to one row in the SolRPDS dataset CSV.
/// Column indices match the dataset schema:
/// https://github.com/DeFiLabX/SolRPDS
/// </summary>
public class RugPullData
{
    [LoadColumn(0)] public string LiquidityPoolAddress { get; set; } = "";
    [LoadColumn(1)] public string Mint { get; set; } = "";
    [LoadColumn(2)] public float TotalAddedLiquidity { get; set; }
    [LoadColumn(3)] public float TotalRemovedLiquidity { get; set; }
    [LoadColumn(4)] public float NumLiquidityAdds { get; set; }
    [LoadColumn(5)] public float NumLiquidityRemoves { get; set; }
    [LoadColumn(6)] public float AddToRemoveRatio { get; set; }
    [LoadColumn(7)] public string LastPoolActivityTimestamp { get; set; } = "";
    [LoadColumn(8)] public string FirstPoolActivityTimestamp { get; set; } = "";
    [LoadColumn(9)] public string LastSwapTimestamp { get; set; } = "";
    [LoadColumn(10)] public string LastSwapTxId { get; set; } = "";
    [LoadColumn(11)] public string InactivityStatus { get; set; } = "";
}

public class RugPullPrediction
{
    [ColumnName("PredictedLabel")] public bool IsRugPull { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
    [ColumnName("Score")] public float Score { get; set; }
}

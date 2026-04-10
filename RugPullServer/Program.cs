using Microsoft.ML;
using RugPullShared;
using RugPullServer;

var mlContext = new MLContext();
mlContext.ComponentCatalog.RegisterAssembly(typeof(RugPullData).Assembly);
var model = mlContext.Model.Load("rugpull_model.zip", out _);
var predictionEngine = mlContext.Model.CreatePredictionEngine<RugPullData, RugPullPrediction>(model);
var tokenAnalyzer = new TokenAnalyzer(new HttpClient());

if (args.Contains("--mcp"))
{
    await McpHandler.RunAsync(predictionEngine, tokenAnalyzer);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var predLock = new object();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/check", (ManualCheckRequest req) =>
{
    var input = new RugPullData
    {
        TotalAddedLiquidity = req.TotalAddedLiquidity,
        TotalRemovedLiquidity = req.TotalRemovedLiquidity,
        NumLiquidityAdds = req.NumLiquidityAdds,
        NumLiquidityRemoves = req.NumLiquidityRemoves,
        AddToRemoveRatio = req.AddToRemoveRatio
    };

    RugPullPrediction prediction;
    lock (predLock)
    {
        prediction = predictionEngine.Predict(input);
    }

    return Results.Ok(RiskResult.FromPrediction(prediction, input));
});

app.MapGet("/api/analyze/{mintAddress}", async (string mintAddress) =>
{
    var analysis = await tokenAnalyzer.AnalyzeAsync(mintAddress);
    if (analysis == null)
        return Results.NotFound(new { error = $"No pools found for {mintAddress}" });

    return Results.Ok(analysis);
});

app.Run();

record ManualCheckRequest(
    float TotalAddedLiquidity,
    float TotalRemovedLiquidity,
    float NumLiquidityAdds,
    float NumLiquidityRemoves,
    float AddToRemoveRatio);

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML;
using RugPullShared;

namespace RugPullServer;

/// <summary>
/// Handles MCP (Model Context Protocol) over stdin/stdout using JSON-RPC 2.0.
/// Activated when the server is launched with the --mcp flag.
/// </summary>
public static class McpHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task RunAsync(
        PredictionEngine<RugPullData, RugPullPrediction> predictionEngine,
        TokenAnalyzer tokenAnalyzer)
    {
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) break;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string method = root.GetProperty("method").GetString() ?? "";
                var id = root.TryGetProperty("id", out var idProp) ? idProp.Clone() : default;

                switch (method)
                {
                    case "initialize":
                        Respond(id, new
                        {
                            protocolVersion = "2024-11-05",
                            serverInfo = new { name = "solana-rugpull-detector", version = "1.0.0" },
                            capabilities = new { tools = new { } }
                        });
                        break;

                    case "notifications/initialized":
                        break;

                    case "tools/list":
                        Respond(id, new
                        {
                            tools = new object[]
                            {
                                new
                                {
                                    name = "check_rug_pull",
                                    description = "Predict if a Solana token is a rug pull using liquidity pool metrics from the SolRPDS-trained ML model.",
                                    inputSchema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["totalAddedLiquidity"] = new { type = "number", description = "Total liquidity ever added to the pool" },
                                            ["totalRemovedLiquidity"] = new { type = "number", description = "Total liquidity ever removed" },
                                            ["numLiquidityAdds"] = new { type = "number", description = "Number of liquidity add transactions" },
                                            ["numLiquidityRemoves"] = new { type = "number", description = "Number of liquidity remove transactions" },
                                            ["addToRemoveRatio"] = new { type = "number", description = "Ratio of add to remove transactions" }
                                        },
                                        required = new[] { "totalAddedLiquidity", "totalRemovedLiquidity", "numLiquidityAdds", "numLiquidityRemoves", "addToRemoveRatio" }
                                    }
                                },
                                new
                                {
                                    name = "analyze_token",
                                    description = "Look up a Solana token by mint address. Fetches live pool data from DexScreener and runs heuristic risk analysis on liquidity depth, buy/sell ratio, price action, and token age.",
                                    inputSchema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["mintAddress"] = new { type = "string", description = "Solana token mint address" }
                                        },
                                        required = new[] { "mintAddress" }
                                    }
                                }
                            }
                        });
                        break;

                    case "tools/call":
                        var toolName = root.GetProperty("params").GetProperty("name").GetString();
                        var toolArgs = root.GetProperty("params").GetProperty("arguments");

                        if (toolName == "check_rug_pull")
                            HandleManualCheck(id, toolArgs, predictionEngine);
                        else if (toolName == "analyze_token")
                            await HandleAnalyzeToken(id, toolArgs, tokenAnalyzer);
                        else
                            RespondError(id, -32601, $"Unknown tool: {toolName}");
                        break;

                    default:
                        if (id.ValueKind != JsonValueKind.Undefined)
                            RespondError(id, -32601, $"Unknown method: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    private static void HandleManualCheck(
        JsonElement id, JsonElement toolArgs,
        PredictionEngine<RugPullData, RugPullPrediction> predictionEngine)
    {
        var input = new RugPullData
        {
            TotalAddedLiquidity = toolArgs.GetProperty("totalAddedLiquidity").GetSingle(),
            TotalRemovedLiquidity = toolArgs.GetProperty("totalRemovedLiquidity").GetSingle(),
            NumLiquidityAdds = toolArgs.GetProperty("numLiquidityAdds").GetSingle(),
            NumLiquidityRemoves = toolArgs.GetProperty("numLiquidityRemoves").GetSingle(),
            AddToRemoveRatio = toolArgs.GetProperty("addToRemoveRatio").GetSingle()
        };

        var prediction = predictionEngine.Predict(input);
        var result = RiskResult.FromPrediction(prediction, input);
        var resultText = JsonSerializer.Serialize(result, JsonOpts);

        Respond(id, new { content = new[] { new { type = "text", text = resultText } } });
    }

    private static async Task HandleAnalyzeToken(
        JsonElement id, JsonElement toolArgs, TokenAnalyzer analyzer)
    {
        string mintAddress = toolArgs.GetProperty("mintAddress").GetString() ?? "";
        var outcome = await analyzer.AnalyzeAsync(mintAddress);

        object payload = outcome switch
        {
            AnalysisFound found => (object)found.Analysis,
            AnalysisNotFound nf => new
            {
                error = "Token not found",
                mintAddress,
                reason = nf.Reason,
                suggestion = nf.Suggestion
            },
            AnalysisInvalidInput invalid => new
            {
                error = "Invalid mint address",
                mintAddress,
                reason = invalid.Reason
            },
            AnalysisUpstreamError err => new
            {
                error = "Upstream API error",
                mintAddress,
                reason = err.Reason
            },
            _ => new { error = "Unknown outcome", mintAddress }
        };

        var resultText = JsonSerializer.Serialize(payload, JsonOpts);
        Respond(id, new { content = new[] { new { type = "text", text = resultText } } });
    }

    private static void Respond(JsonElement id, object result)
    {
        var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOpts);
        Console.WriteLine(response);
        Console.Out.Flush();
    }

    private static void RespondError(JsonElement id, int code, string message)
    {
        var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOpts);
        Console.WriteLine(response);
        Console.Out.Flush();
    }
}

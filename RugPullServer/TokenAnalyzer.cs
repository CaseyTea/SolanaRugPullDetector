using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML;
using RugPullShared;

namespace RugPullServer;

/// <summary>
/// Fetches live token data from DexScreener and runs heuristic risk scoring.
/// Also orchestrates the live ML inference path: extracts SolRPDS features
/// from Helius (cached per-mint), runs the trained FastTree model, and
/// merges both outputs into a single <see cref="AnalysisResponse"/>.
///
/// DexScreener has no API key requirement (rate limit ~300 req/min). Helius
/// requires HELIUS_API_KEY; when unset, the extractor returns null and we
/// populate <c>MlError</c> accordingly so heuristic signals still flow.
/// </summary>
public class TokenAnalyzer
{
    private static readonly Regex MintAddressRegex = new(
        @"^[1-9A-HJ-NP-Za-km-z]{32,44}$", RegexOptions.Compiled);

    // Model metadata is hard-coded here. When a new model is trained this
    // constant gets bumped by hand. Not worth automating for a demo app.
    private static readonly ModelInfo CurrentModel = new(
        Version: "fasttree-1.0",
        TrainingDataset: "SolRPDS 2021-2024",
        TrainingAccuracy: 0.866f,
        TrainingAuc: 0.895f);

    private readonly HttpClient _http;
    private readonly HeliusFeatureExtractor _heliusExtractor;
    private readonly AnalysisCache _cache;
    private readonly PredictionEngine<RugPullData, RugPullPrediction> _predictionEngine;

    // PredictionEngine is not thread-safe. Guard it with a lock so concurrent
    // /api/analyze requests don't race into Predict() at the same time.
    private readonly object _predictionLock = new();

    public TokenAnalyzer(
        HttpClient http,
        HeliusFeatureExtractor heliusExtractor,
        AnalysisCache cache,
        PredictionEngine<RugPullData, RugPullPrediction> predictionEngine)
    {
        _http = http;
        _heliusExtractor = heliusExtractor;
        _cache = cache;
        _predictionEngine = predictionEngine;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RugPullDetector/1.0");
    }

    public async Task<AnalysisOutcome> AnalyzeAsync(string mintAddress)
    {
        if (string.IsNullOrEmpty(mintAddress))
            return new AnalysisInvalidInput("Mint address is empty");

        if (!MintAddressRegex.IsMatch(mintAddress))
        {
            Log($"Invalid mint format: {Truncate(mintAddress)}");
            return new AnalysisInvalidInput(
                "Address is not a valid Solana mint format (must be 32-44 Base58 characters)");
        }

        try
        {
            return await AnalyzeInternalAsync(mintAddress);
        }
        catch (HttpRequestException ex)
        {
            Log($"Network error: {ex.Message}");
            return new AnalysisUpstreamError($"Could not reach DexScreener: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Log($"JSON parse error: {ex.Message}");
            return new AnalysisUpstreamError("DexScreener returned invalid JSON");
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return new AnalysisUpstreamError($"Analysis failed: {ex.Message}");
        }
    }

    private async Task<AnalysisOutcome> AnalyzeInternalAsync(string mintAddress)
    {
        var url = $"https://api.dexscreener.com/latest/dex/tokens/{mintAddress}";
        Log($"GET {url}");

        var response = await _http.GetAsync(url);
        Log($"Response: HTTP {(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
            return new AnalysisUpstreamError($"DexScreener returned HTTP {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("pairs", out var pairs))
        {
            Log("Response missing 'pairs' field");
            return new AnalysisNotFound(
                "DexScreener has no record of this address",
                $"Verify the mint on solscan.io/token/{mintAddress}");
        }

        if (pairs.ValueKind == JsonValueKind.Null)
        {
            Log("'pairs' field is null — address unknown to DexScreener");
            return new AnalysisNotFound(
                "DexScreener returned no pools for this address",
                "Common causes: the address is a pair/pool address (not a token mint), the token was delisted or rugged, " +
                "the address is a wallet or NFT, or the token is too new to be indexed. " +
                $"Verify on solscan.io/token/{mintAddress}");
        }

        if (pairs.ValueKind != JsonValueKind.Array)
        {
            Log($"Unexpected 'pairs' type: {pairs.ValueKind}");
            return new AnalysisUpstreamError("Unexpected response format from DexScreener");
        }

        if (pairs.GetArrayLength() == 0)
        {
            Log("'pairs' is an empty array");
            return new AnalysisNotFound(
                "DexScreener has no active pools for this token",
                "The token may be delisted, abandoned, or have had all liquidity removed");
        }

        Log($"Found {pairs.GetArrayLength()} total pools across all chains");

        // Find the Solana pool with the highest liquidity
        JsonElement? topPool = null;
        double topLiquidity = 0;
        int solanaCount = 0;

        foreach (var pair in pairs.EnumerateArray())
        {
            if (pair.TryGetProperty("chainId", out var chain) && chain.GetString() == "solana")
            {
                solanaCount++;
                double liq = 0;
                if (pair.TryGetProperty("liquidity", out var liquidity) &&
                    liquidity.TryGetProperty("usd", out var usd) &&
                    usd.ValueKind == JsonValueKind.Number)
                    liq = usd.GetDouble();

                if (liq > topLiquidity)
                {
                    topLiquidity = liq;
                    topPool = pair;
                }
            }
        }

        Log($"Solana pools: {solanaCount}, top liquidity: ${topLiquidity:N0}");

        if (topPool == null)
        {
            return new AnalysisNotFound(
                "Token exists on DexScreener but has no Solana pools",
                "This detector only analyzes Solana tokens. The token may only trade on other chains");
        }

        var pool = topPool.Value;

        var tokenRef = new TokenRef(
            Name: GetString(pool, "baseToken", "name") ?? "Unknown",
            Symbol: GetString(pool, "baseToken", "symbol") ?? "???",
            MintAddress: mintAddress
        );

        var poolCreatedAtMs = pool.TryGetProperty("pairCreatedAt", out var created)
            && created.ValueKind == JsonValueKind.Number
            ? created.GetInt64() : 0;

        // Parse priceUsd — DexScreener returns it as a string.
        var priceUsdStr = GetString(pool, "priceUsd") ?? "0";
        double priceUsd = double.TryParse(priceUsdStr, out var p) ? p : 0;

        var volume24hUsd = GetNestedDouble(pool, "volume", "h24");
        var priceChange24h = GetNestedDouble(pool, "priceChange", "h24");
        var buys24h = GetNestedInt(pool, "txns", "h24", "buys");
        var sells24h = GetNestedInt(pool, "txns", "h24", "sells");

        var poolCreatedAtIso = poolCreatedAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(poolCreatedAtMs)
                .UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ssZ")
            : "";

        var poolAgeHours = poolCreatedAtMs > 0
            ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - poolCreatedAtMs) / 3_600_000.0
            : 0;

        var marketData = new MarketData(
            Dex: GetString(pool, "dexId") ?? "unknown",
            PairAddress: GetString(pool, "pairAddress") ?? "",
            LiquidityUsd: topLiquidity,
            Volume24hUsd: volume24hUsd,
            PriceUsd: priceUsd,
            PriceChange24hPercent: priceChange24h,
            Buys24h: buys24h,
            Sells24h: sells24h,
            PoolCreatedAt: poolCreatedAtIso,
            PoolAgeHours: Math.Round(poolAgeHours, 1),
            TotalPools: pairs.GetArrayLength()
        );

        Log($"Analyzing {tokenRef.Symbol} on {marketData.Dex}");

        var marketSignals = ScoreRisk(marketData, poolCreatedAtMs);

        // Live ML inference path: cache-then-Helius feature extraction, then
        // feed the 5 SolRPDS features into the trained FastTree model. Any
        // failure leaves mlPrediction null and populates mlError with a
        // human-readable reason so the heuristic sections still return cleanly.
        MlPrediction? mlPrediction = null;
        string? mlError = null;

        try
        {
            var (features, error) = await GetFeaturesAsync(mintAddress, marketData.PairAddress);
            if (features is null)
            {
                mlError = error ?? "Feature extraction failed";
                Log($"ML path skipped: {mlError}");
            }
            else
            {
                mlPrediction = RunPrediction(features);
            }
        }
        catch (Exception ex)
        {
            mlError = $"ML inference failed: {ex.GetType().Name}: {ex.Message}";
            Log(mlError);
        }

        return new AnalysisFound(new AnalysisResponse(
            Token: tokenRef,
            MlPrediction: mlPrediction,
            MlError: mlError,
            MarketSignals: marketSignals,
            MarketData: marketData,
            Agreement: ComputeAgreement(mlPrediction, marketSignals)));
    }

    /// <summary>
    /// Resolve SolRPDS features for a mint, consulting the in-memory cache
    /// first (keyed by mint) and falling back to Helius. Returns a tuple of
    /// (features, errorReason); exactly one side is non-null. When Helius
    /// yields non-null features they are cached for 10 minutes. Null returns
    /// are NOT cached so transient failures get retried on the next request.
    /// </summary>
    private async Task<(HeliusFeatures? features, string? error)> GetFeaturesAsync(
        string mintAddress, string poolAddress)
    {
        if (string.IsNullOrEmpty(poolAddress))
            return (null, "No pool address available for feature extraction");

        // Tracks why the factory returned null so we can surface a clean
        // mlError upstream even though AnalysisCache deliberately does not
        // cache nulls (it would be wrong to persist a transient failure).
        string? factoryError = null;

        var features = await _cache.GetOrSetAsync(mintAddress, async () =>
        {
            var result = await _heliusExtractor.ExtractAsync(poolAddress);
            if (result is null)
            {
                factoryError = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELIUS_API_KEY"))
                    ? "HELIUS_API_KEY not configured"
                    : "Helius feature extraction failed";
            }
            return result;
        });

        if (features is null)
            return (null, factoryError ?? "Feature extraction failed");

        return (features, null);
    }

    /// <summary>
    /// Maps <see cref="HeliusFeatures"/> into a <see cref="RugPullData"/>,
    /// runs the FastTree PredictionEngine under a lock (the engine is not
    /// thread-safe), and builds the <see cref="MlPrediction"/> response
    /// record with derived verdict, risk level, and model metadata.
    /// </summary>
    private MlPrediction RunPrediction(HeliusFeatures features)
    {
        var input = new RugPullData
        {
            TotalAddedLiquidity = features.TotalAddedLiquidity,
            TotalRemovedLiquidity = features.TotalRemovedLiquidity,
            NumLiquidityAdds = features.NumLiquidityAdds,
            NumLiquidityRemoves = features.NumLiquidityRemoves,
            // SolRPDS represents an "infinite" ratio (removes == 0) as a very
            // large float. PositiveInfinity would poison the model input, so
            // clamp to a finite sentinel that still reads as "no removes yet".
            AddToRemoveRatio = float.IsFinite(features.AddToRemoveRatio)
                ? features.AddToRemoveRatio
                : 1_000_000f
        };

        RugPullPrediction prediction;
        lock (_predictionLock)
        {
            prediction = _predictionEngine.Predict(input);
        }

        var probability = prediction.Probability;
        var verdict = probability > 0.5f ? "LIKELY RUG PULL" : "LIKELY LEGITIMATE";
        var riskLevel = probability switch
        {
            > 0.9f => "CRITICAL",
            > 0.75f => "HIGH",
            > 0.5f => "MEDIUM",
            _ => "LOW"
        };

        var mlFeatures = new MlFeatures(
            TotalAddedLiquidity: features.TotalAddedLiquidity,
            TotalRemovedLiquidity: features.TotalRemovedLiquidity,
            NumLiquidityAdds: features.NumLiquidityAdds,
            NumLiquidityRemoves: features.NumLiquidityRemoves,
            AddToRemoveRatio: input.AddToRemoveRatio);

        var cachedAtIso = DateTimeOffset.UtcNow
            .UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ");

        return new MlPrediction(
            Verdict: verdict,
            Probability: probability,
            RawScore: prediction.Score,
            RiskLevel: riskLevel,
            Features: mlFeatures,
            FeaturesSource: "helius",
            FeaturesCachedAt: cachedAtIso,
            Model: CurrentModel);
    }

    private static MarketSignalsBlock ScoreRisk(MarketData pool, long poolCreatedAtMs)
    {
        var flags = new List<RiskFlag>();
        int score = 0;

        // Liquidity depth
        if (pool.LiquidityUsd < 1_000)
        {
            flags.Add(new RiskFlag("danger", "liquidity", $"Extremely low liquidity (${pool.LiquidityUsd:N0})", pool.LiquidityUsd));
            score += 3;
        }
        else if (pool.LiquidityUsd < 10_000)
        {
            flags.Add(new RiskFlag("warning", "liquidity", $"Low liquidity (${pool.LiquidityUsd:N0})", pool.LiquidityUsd));
            score += 2;
        }
        else if (pool.LiquidityUsd < 50_000)
        {
            flags.Add(new RiskFlag("info", "liquidity", $"Moderate liquidity (${pool.LiquidityUsd:N0})", pool.LiquidityUsd));
            score += 1;
        }
        else
        {
            flags.Add(new RiskFlag("ok", "liquidity", $"Healthy liquidity (${pool.LiquidityUsd:N0})", pool.LiquidityUsd));
        }

        // Buy/sell pressure
        if (pool.Buys24h + pool.Sells24h > 0)
        {
            double sellRatio = pool.Sells24h / (double)Math.Max(pool.Buys24h, 1);
            double roundedRatio = Math.Round(sellRatio, 2);
            if (sellRatio > 2.0)
            {
                flags.Add(new RiskFlag("danger", "volume", $"Heavy sell pressure: {pool.Sells24h} sells vs {pool.Buys24h} buys (24h)", roundedRatio));
                score += 2;
            }
            else if (sellRatio > 1.0)
            {
                flags.Add(new RiskFlag("warning", "volume", $"More sells than buys: {pool.Sells24h} sells vs {pool.Buys24h} buys (24h)", roundedRatio));
                score += 1;
            }
            else
            {
                flags.Add(new RiskFlag("ok", "volume", $"Healthy trading: {pool.Buys24h} buys vs {pool.Sells24h} sells (24h)", roundedRatio));
            }
        }

        // Price crash detection
        if (pool.PriceChange24hPercent < -50)
        {
            flags.Add(new RiskFlag("danger", "price", $"Price crashed {pool.PriceChange24hPercent:F1}% in 24h", pool.PriceChange24hPercent));
            score += 2;
        }
        else if (pool.PriceChange24hPercent < -20)
        {
            flags.Add(new RiskFlag("warning", "price", $"Price down {pool.PriceChange24hPercent:F1}% in 24h", pool.PriceChange24hPercent));
            score += 1;
        }

        // Token age
        if (poolCreatedAtMs > 0)
        {
            var ageHours = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - poolCreatedAtMs) / 3_600_000.0;
            if (ageHours < 24)
            {
                flags.Add(new RiskFlag("warning", "age", $"Very new token: pool created {ageHours:F0} hours ago", Math.Round(ageHours, 1)));
                score += 2;
            }
            else if (ageHours < 168)
            {
                flags.Add(new RiskFlag("info", "age", $"Young token: pool created {ageHours / 24:F0} days ago", Math.Round(ageHours, 1)));
                score += 1;
            }
        }

        // Wash trading signal
        if (pool.LiquidityUsd > 0 && pool.Volume24hUsd > pool.LiquidityUsd * 10)
        {
            double volToLiq = Math.Round(pool.Volume24hUsd / pool.LiquidityUsd, 2);
            flags.Add(new RiskFlag("warning", "wash_trading", "Volume exceeds 10x liquidity — possible wash trading", volToLiq));
            score += 1;
        }

        score = Math.Min(score, 10);

        string overallRisk = score switch
        {
            >= 8 => "CRITICAL",
            >= 5 => "HIGH",
            >= 3 => "MEDIUM",
            _ => "LOW"
        };

        return new MarketSignalsBlock(overallRisk, score, flags);
    }

    /// <summary>
    /// Compares the ML verdict with the heuristic verdict and classifies the
    /// pair as agree / conflict / partial / ml_unavailable.
    ///
    /// Rules:
    ///   - null ML           → ml_unavailable
    ///   - rug (p>0.5)  + HIGH/CRITICAL heuristic → agree
    ///   - legit (p<=0.5) + LOW heuristic         → agree
    ///   - rug (p>0.5)  + LOW heuristic           → conflict
    ///   - legit (p<=0.5) + CRITICAL heuristic    → conflict
    ///   - everything else                        → partial
    /// </summary>
    public static string ComputeAgreement(MlPrediction? ml, MarketSignalsBlock market)
    {
        if (ml is null) return "ml_unavailable";

        bool mlSaysRug = ml.Probability > 0.5f;
        var risk = market.OverallRisk;

        if (mlSaysRug && (risk == "HIGH" || risk == "CRITICAL")) return "agree";
        if (!mlSaysRug && risk == "LOW") return "agree";
        if (mlSaysRug && risk == "LOW") return "conflict";
        if (!mlSaysRug && risk == "CRITICAL") return "conflict";

        return "partial";
    }

    private static void Log(string message)
        => Console.Error.WriteLine($"[TokenAnalyzer] {message}");

    private static string Truncate(string s) => s.Length > 20 ? s[..20] + "..." : s;

    // JSON extraction helpers
    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var val) ? val.GetString() : null;

    private static string? GetString(JsonElement el, string parent, string child)
        => el.TryGetProperty(parent, out var p) && p.TryGetProperty(child, out var c)
            ? c.GetString() : null;

    private static double GetNestedDouble(JsonElement el, string parent, string child)
    {
        if (el.TryGetProperty(parent, out var p) && p.TryGetProperty(child, out var c))
        {
            if (c.ValueKind == JsonValueKind.Number) return c.GetDouble();
            if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), out var d)) return d;
        }
        return 0;
    }

    private static int GetNestedInt(JsonElement el, string l1, string l2, string l3)
    {
        if (el.TryGetProperty(l1, out var v1) &&
            v1.TryGetProperty(l2, out var v2) &&
            v2.TryGetProperty(l3, out var v3) &&
            v3.ValueKind == JsonValueKind.Number)
            return v3.GetInt32();
        return 0;
    }
}

public record RiskFlag(string Level, string Category, string Message, double Value);

/// <summary>
/// Result of a token analysis request. Uses a discriminated union so callers
/// can pattern-match on the specific outcome and return appropriate responses.
/// </summary>
public abstract record AnalysisOutcome;
public sealed record AnalysisFound(AnalysisResponse Response) : AnalysisOutcome;
public sealed record AnalysisNotFound(string Reason, string Suggestion) : AnalysisOutcome;
public sealed record AnalysisInvalidInput(string Reason) : AnalysisOutcome;
public sealed record AnalysisUpstreamError(string Reason) : AnalysisOutcome;

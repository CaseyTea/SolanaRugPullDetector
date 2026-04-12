using System.Text.Json;
using System.Text.RegularExpressions;

namespace RugPullServer;

/// <summary>
/// Fetches live token data from DexScreener and runs heuristic risk scoring.
/// No API key required. Rate limit ~300 req/min.
/// </summary>
public class TokenAnalyzer
{
    private static readonly Regex MintAddressRegex = new(
        @"^[1-9A-HJ-NP-Za-km-z]{32,44}$", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public TokenAnalyzer(HttpClient http)
    {
        _http = http;
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

        return new AnalysisFound(new AnalysisResponse(
            Token: tokenRef,
            MlPrediction: null,
            MlError: null,
            MarketSignals: marketSignals,
            MarketData: marketData,
            Agreement: ComputeAgreement(null, marketSignals)));
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
    /// Compares the ML verdict with the heuristic verdict and returns a string
    /// indicating whether they agree. Story 5 will implement the real logic
    /// (agree / disagree / ml_says_safer / ml_says_riskier). For Story 4 we
    /// only have the heuristic path, so this always returns "not_evaluated".
    /// </summary>
    public static string ComputeAgreement(MlPrediction? ml, MarketSignalsBlock market)
    {
        // Story 5: replace this with the real comparison once ML predictions
        // are wired in via Helius feature extraction.
        if (ml is null) return "not_evaluated";
        return "not_evaluated";
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

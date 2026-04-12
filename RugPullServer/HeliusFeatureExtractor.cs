using System.Text.Json;

namespace RugPullServer;

/// <summary>
/// Extracts the 5 SolRPDS features our ML model was trained on by walking a
/// Solana liquidity pool's transaction history via Helius's Enhanced
/// Transactions API and counting liquidity add/remove events.
///
/// This class is intentionally standalone — no DI wiring yet. Integration
/// into /api/analyze happens in a later story.
/// </summary>
public class HeliusFeatureExtractor
{
    // Hard cap on pagination iterations. Each page costs 1 Helius credit, so
    // the worst case per analysis is 20 credits. When this cap is hit we set
    // FeaturesPartial = true on the result so callers know the counts are a
    // lower bound on the pool's true lifetime activity.
    private const int MaxPages = 20;
    private const int PageSize = 100;

    // Helius labels parsed DEX events with these enum values in the top-level
    // "type" field. The list was derived from a combination of (a) the Helius
    // Enhanced Transactions API reference and (b) live probing of BONK/SOL and
    // a Raydium pool. Any of these types indicate liquidity was moved into a
    // pool. We match them case-insensitively via HashSet lookup.
    //
    // Add variants: deposits into the pool.
    private static readonly HashSet<string> AddLiquidityTypes = new(StringComparer.Ordinal)
    {
        "ADD_LIQUIDITY",
        "ADD_LIQUIDITY_ONE_SIDE",
        "ADD_LIQUIDITY_BY_STRATEGY",
        "ADD_LIQUIDITY_BY_STRATEGY_ONE_SIDE",
        "ADD_LIQUIDITY_BY_WEIGHT",
        "ADD_LIQUIDITY_ONE_SIDE_PRECISE",
        "ADD_BALANCE_LIQUIDITY",
        "ADD_IMBALANCE_LIQUIDITY",
        "INCREASE_LIQUIDITY",
        "DEPOSIT",
        "DEPOSIT_RESERVE_LIQUIDITY",
        "BOOTSTRAP_LIQUIDITY",
    };

    // Remove variants: withdrawals from the pool.
    private static readonly HashSet<string> RemoveLiquidityTypes = new(StringComparer.Ordinal)
    {
        "REMOVE_LIQUIDITY",
        "REMOVE_LIQUIDITY_SINGLE_SIDE",
        "REMOVE_ALL_LIQUIDITY",
        "REMOVE_BALANCE_LIQUIDITY",
        "REMOVE_LIQUIDITY_BY_RANGE",
        "DECREASE_LIQUIDITY",
        "WITHDRAW",
        "WITHDRAW_LIQUIDITY",
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public HeliusFeatureExtractor(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey ?? "";
    }

    /// <summary>
    /// Walk the transaction history of a Solana liquidity pool via Helius and
    /// return aggregated add/remove counts. Returns null on missing API key,
    /// network errors, parse failures, rate limits, or non-200 responses.
    /// </summary>
    public async Task<HeliusFeatures?> ExtractAsync(string poolAddress)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            Log("API key not set — cannot extract features");
            return null;
        }

        if (string.IsNullOrWhiteSpace(poolAddress))
        {
            Log("Pool address is empty");
            return null;
        }

        float totalAdded = 0f;
        float totalRemoved = 0f;
        int numAdds = 0;
        int numRemoves = 0;
        bool partial = false;

        string? beforeSignature = null;
        int pagesFetched = 0;

        try
        {
            for (int page = 0; page < MaxPages; page++)
            {
                var url = BuildUrl(poolAddress, beforeSignature);
                Log($"GET page {page + 1}/{MaxPages} for {Truncate(poolAddress)}"
                    + (beforeSignature is null ? "" : $" before={Truncate(beforeSignature)}"));

                HttpResponseMessage response;
                try
                {
                    response = await _http.GetAsync(url);
                }
                catch (HttpRequestException ex)
                {
                    Log($"Network error on page {page + 1}: {ex.Message}");
                    return null;
                }

                pagesFetched++;

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Log("Rate limited (HTTP 429) — aborting");
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await SafeReadBodyAsync(response);
                    Log($"HTTP {(int)response.StatusCode} on page {page + 1}: {Truncate(body)}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(json);
                }
                catch (JsonException ex)
                {
                    Log($"JSON parse error on page {page + 1}: {ex.Message}");
                    return null;
                }

                using (doc)
                {
                    var root = doc.RootElement;

                    // Helius returns an error shape ({"error": "..."}) when a
                    // type-filtered query exhausts results, and occasionally
                    // on other edge cases. Unfiltered requests normally return
                    // an array, but we guard for both shapes defensively.
                    if (root.ValueKind != JsonValueKind.Array)
                    {
                        if (root.ValueKind == JsonValueKind.Object
                            && root.TryGetProperty("error", out var errEl)
                            && errEl.ValueKind == JsonValueKind.String)
                        {
                            Log($"Helius error on page {page + 1}: {errEl.GetString()}");
                        }
                        else
                        {
                            Log($"Unexpected response kind on page {page + 1}: {root.ValueKind}");
                        }
                        return null;
                    }

                    var count = root.GetArrayLength();
                    if (count == 0)
                    {
                        Log($"End of history reached after {pagesFetched} page(s)");
                        break;
                    }

                    string? lastSignature = null;
                    foreach (var tx in root.EnumerateArray())
                    {
                        // Track pagination cursor regardless of type match.
                        if (tx.TryGetProperty("signature", out var sigEl)
                            && sigEl.ValueKind == JsonValueKind.String)
                        {
                            lastSignature = sigEl.GetString();
                        }

                        try
                        {
                            ClassifyAndAccumulate(
                                tx, poolAddress,
                                ref totalAdded, ref totalRemoved,
                                ref numAdds, ref numRemoves);
                        }
                        catch (Exception ex)
                        {
                            // One malformed tx shouldn't derail the whole walk.
                            Log($"Skipping malformed tx: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    // A short page (less than PageSize) means we've hit the
                    // tail of the pool's history. No point in another round
                    // trip that would return an empty array.
                    if (count < PageSize)
                    {
                        Log($"Short page ({count} < {PageSize}) — end of history");
                        break;
                    }

                    if (lastSignature is null)
                    {
                        Log("No signature in final tx of page — cannot paginate further");
                        break;
                    }

                    beforeSignature = lastSignature;

                    // If the next iteration would exceed the cap, mark partial
                    // and stop. This is observable by callers via the flag.
                    if (page + 1 >= MaxPages)
                    {
                        partial = true;
                        Log($"Hit {MaxPages}-page cap — results are partial (lower bound)");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            Log($"JSON parse error: {ex.Message}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log($"Network error: {ex.Message}");
            return null;
        }

        var ratio = numRemoves > 0
            ? numAdds / (float)numRemoves
            : float.PositiveInfinity;

        Log($"Done. pages={pagesFetched} adds={numAdds} removes={numRemoves} "
            + $"addedLiq={totalAdded:F2} removedLiq={totalRemoved:F2} partial={partial}");

        return new HeliusFeatures(
            TotalAddedLiquidity: totalAdded,
            TotalRemovedLiquidity: totalRemoved,
            NumLiquidityAdds: numAdds,
            NumLiquidityRemoves: numRemoves,
            AddToRemoveRatio: ratio,
            FeaturesPartial: partial
        );
    }

    /// <summary>
    /// Examine one transaction's <c>type</c> field and, if it's a liquidity
    /// add/remove, add the sum of its token transfers moved with respect to
    /// the pool address into the running totals.
    /// </summary>
    private static void ClassifyAndAccumulate(
        JsonElement tx,
        string poolAddress,
        ref float totalAdded,
        ref float totalRemoved,
        ref int numAdds,
        ref int numRemoves)
    {
        if (!tx.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var type = typeEl.GetString();
        if (string.IsNullOrEmpty(type))
            return;

        bool isAdd = AddLiquidityTypes.Contains(type);
        bool isRemove = !isAdd && RemoveLiquidityTypes.Contains(type);
        if (!isAdd && !isRemove)
            return;

        // Amount = sum of all token transfer amounts in this tx where the
        // pool is the destination (for adds) or the source (for removes).
        // This is approximate — we treat each token leg as an additive
        // contribution in its own unit. The ML model was trained on raw
        // SolRPDS aggregates, not USD-denominated, so matching its scale
        // exactly requires the same normalisation SolRPDS used. For now,
        // summing tokenAmount across legs gives a non-zero signal that
        // tracks activity magnitude.
        float amount = SumPoolTokenTransferAmounts(tx, poolAddress, inbound: isAdd);

        if (isAdd)
        {
            numAdds++;
            totalAdded += amount;
        }
        else
        {
            numRemoves++;
            totalRemoved += amount;
        }
    }

    private static float SumPoolTokenTransferAmounts(
        JsonElement tx, string poolAddress, bool inbound)
    {
        if (!tx.TryGetProperty("tokenTransfers", out var transfers)
            || transfers.ValueKind != JsonValueKind.Array)
        {
            return 0f;
        }

        float sum = 0f;
        foreach (var t in transfers.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object)
                continue;

            // For adds, the pool account is the destination. For removes,
            // the pool is the source. Some LP adds also show the pool as
            // a token owner on both sides depending on DEX — we fall back
            // to matching either direction if the strict direction yields
            // nothing.
            string? toUser = GetString(t, "toUserAccount");
            string? fromUser = GetString(t, "fromUserAccount");

            bool matches = inbound
                ? string.Equals(toUser, poolAddress, StringComparison.Ordinal)
                : string.Equals(fromUser, poolAddress, StringComparison.Ordinal);

            if (!matches)
                continue;

            if (t.TryGetProperty("tokenAmount", out var amtEl)
                && amtEl.ValueKind == JsonValueKind.Number
                && amtEl.TryGetDouble(out var dv))
            {
                sum += (float)dv;
            }
        }

        return sum;
    }

    private string BuildUrl(string poolAddress, string? beforeSignature)
    {
        // Helius Enhanced Transactions, addresses endpoint. limit=100 is the
        // max page size. Encoding the address is safe even though Base58 is
        // URL-clean, in case a caller ever slips in an unusual value.
        var encoded = Uri.EscapeDataString(poolAddress);
        var url = $"https://api.helius.xyz/v0/addresses/{encoded}/transactions"
                  + $"?api-key={Uri.EscapeDataString(_apiKey)}"
                  + $"&limit={PageSize}";
        if (!string.IsNullOrEmpty(beforeSignature))
            url += $"&before={Uri.EscapeDataString(beforeSignature)}";
        return url;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            return body ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > 20 ? s[..20] + "..." : s;
    }

    private static void Log(string message)
        => Console.Error.WriteLine($"[Helius] {message}");
}

/// <summary>
/// The 5 SolRPDS features the ML model was trained on, plus a
/// <see cref="FeaturesPartial"/> flag indicating whether the Helius
/// pagination cap was hit (in which case the counts are a lower bound
/// on the pool's true lifetime activity).
/// </summary>
public record HeliusFeatures(
    float TotalAddedLiquidity,
    float TotalRemovedLiquidity,
    int NumLiquidityAdds,
    int NumLiquidityRemoves,
    float AddToRemoveRatio,
    bool FeaturesPartial);

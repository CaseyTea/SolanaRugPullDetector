using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using DotNetEnv;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.ML;
using RugPullShared;
using RugPullServer;

// Load environment variables from .env if present. Silent when absent so the
// zero-config quickstart flow still works. Must run before any env var reads
// and before the --mcp branch so MCP stdio mode also picks up .env values.
Env.TraversePath().Load();

var mlContext = new MLContext();
mlContext.ComponentCatalog.RegisterAssembly(typeof(RugPullData).Assembly);
var model = mlContext.Model.Load("rugpull_model.zip", out _);
var predictionEngine = mlContext.Model.CreatePredictionEngine<RugPullData, RugPullPrediction>(model);

// HELIUS_API_KEY gates the live ML feature extraction path. If absent,
// HeliusFeatureExtractor returns null gracefully and /api/analyze responds
// with mlPrediction: null + an mlError string — heuristic signals still work.
var heliusApiKey = Environment.GetEnvironmentVariable("HELIUS_API_KEY") ?? "";
if (string.IsNullOrEmpty(heliusApiKey))
{
    Console.Error.WriteLine(
        "WARNING: HELIUS_API_KEY environment variable is not set. ML live inference is DISABLED — /api/analyze will return mlPrediction: null");
}
else
{
    Console.Error.WriteLine(
        $"HELIUS_API_KEY loaded (length={heliusApiKey.Length}, prefix={heliusApiKey[..Math.Min(4, heliusApiKey.Length)]}...). ML live inference enabled.");
}

var heliusExtractor = new HeliusFeatureExtractor(new HttpClient(), heliusApiKey);

// AnalysisCache needs an IMemoryCache. For the MCP + main-path shared case we
// build a small standalone cache instance here so both code paths share the
// same wiring shape. The DI registration below mirrors this for the HTTP path.
var sharedMemoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10_000 });
var sharedAnalysisCache = new AnalysisCache(sharedMemoryCache);

var tokenAnalyzer = new TokenAnalyzer(
    new HttpClient(), heliusExtractor, sharedAnalysisCache, predictionEngine);

if (args.Contains("--mcp"))
{
    await McpHandler.RunAsync(predictionEngine, tokenAnalyzer);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// When deployed behind a reverse proxy (Cloudflare Tunnel, nginx, Caddy, etc.),
// opt in to X-Forwarded-For so rate limiting sees the real client IP instead of
// the proxy's IP. Off by default to prevent spoofing on direct deployments.
var trustProxy = Environment.GetEnvironmentVariable("TRUST_FORWARDED_HEADERS") == "1";
if (trustProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        // Partition by API key when present so each authenticated client gets its own
        // bucket. Fall back to client IP when no key is provided. The key is hashed so
        // raw key values never live in the bucket dictionary.
        string partitionKey;
        if (httpContext.Request.Headers.TryGetValue("X-API-Key", out var providedKey)
            && !string.IsNullOrEmpty(providedKey.ToString()))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey.ToString()));
            partitionKey = "key:" + Convert.ToHexString(hash);
        }
        else
        {
            partitionKey = "ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: partitionKey,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });
});

builder.Services.AddMemoryCache(options => { options.SizeLimit = 10_000; });
builder.Services.AddSingleton<AnalysisCache>(sharedAnalysisCache);
builder.Services.AddSingleton(heliusExtractor);
builder.Services.AddSingleton(predictionEngine);
builder.Services.AddSingleton(tokenAnalyzer);

var app = builder.Build();

if (trustProxy)
{
    app.UseForwardedHeaders();
    Console.Error.WriteLine("TRUST_FORWARDED_HEADERS=1 — trusting X-Forwarded-For from upstream proxy.");
}

// API key auth: read RUGPULL_API_KEY at startup. If unset, auth is disabled
// (keeps `dotnet run` quickstart flow working without env vars).
var apiKey = Environment.GetEnvironmentVariable("RUGPULL_API_KEY");
byte[]? apiKeyBytes = null;
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine(
        "WARNING: RUGPULL_API_KEY environment variable is not set. API key authentication is DISABLED.");
}
else
{
    apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);
}

app.UseRateLimiter();

// API key middleware: only applies to /api/* paths. /health is untouched.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (!path.StartsWithSegments("/api"))
    {
        await next(ctx);
        return;
    }

    if (apiKeyBytes is null)
    {
        // Auth disabled (no env var set at startup).
        await next(ctx);
        return;
    }

    if (!ctx.Request.Headers.TryGetValue("X-API-Key", out var providedValues))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-API-Key header" });
        return;
    }

    var providedBytes = Encoding.UTF8.GetBytes(providedValues.ToString());
    if (!CryptographicOperations.FixedTimeEquals(providedBytes, apiKeyBytes))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid API key" });
        return;
    }

    await next(ctx);
});

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
})
.RequireRateLimiting("api");

app.MapGet("/api/analyze/{mintAddress}", async (string mintAddress) =>
{
    var outcome = await tokenAnalyzer.AnalyzeAsync(mintAddress);
    return outcome switch
    {
        AnalysisFound found => Results.Ok(found.Response),
        AnalysisNotFound nf => Results.NotFound(new
        {
            error = "Token not found",
            mintAddress,
            reason = nf.Reason,
            suggestion = nf.Suggestion
        }),
        AnalysisInvalidInput invalid => Results.BadRequest(new
        {
            error = "Invalid mint address",
            mintAddress,
            reason = invalid.Reason
        }),
        AnalysisUpstreamError err => Results.Problem(
            title: "Upstream API error",
            detail: err.Reason,
            statusCode: 502),
        _ => Results.StatusCode(500)
    };
})
.RequireRateLimiting("api");

app.Run();

record ManualCheckRequest(
    float TotalAddedLiquidity,
    float TotalRemovedLiquidity,
    float NumLiquidityAdds,
    float NumLiquidityRemoves,
    float AddToRemoveRatio);

using Microsoft.Extensions.Caching.Memory;

namespace RugPullServer;

/// <summary>
/// Thin wrapper around <see cref="IMemoryCache"/> that provides a
/// get-or-populate pattern with a fixed 10-minute absolute TTL. Entries are
/// sized at 1 unit each so the underlying cache's SizeLimit acts as an
/// approximate entry count cap with LRU eviction.
///
/// Not yet consumed by any endpoint — Story 5 wires this into the analysis
/// flow so repeated calls for the same token skip the expensive Helius
/// feature extraction path.
///
/// Cache stampede protection is intentionally not implemented: N concurrent
/// misses for the same key will all run the factory. Acceptable for the
/// low-traffic demo use case; swap in FusionCache or equivalent if that ever
/// stops being true.
/// </summary>
public class AnalysisCache
{
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;

    public AnalysisCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> if present,
    /// otherwise invokes <paramref name="factory"/>, caches its non-null
    /// result with a 10-minute TTL, and returns it. Null returns are NOT
    /// cached, so a factory that transiently fails will be retried on the
    /// next call.
    /// </summary>
    public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T?>> factory) where T : class
    {
        if (_cache.TryGetValue(key, out var existing) && existing is T cached)
        {
            Console.Error.WriteLine($"[Cache] hit: {key}");
            return cached;
        }

        Console.Error.WriteLine($"[Cache] miss: {key}");
        var value = await factory();
        if (value is null)
        {
            // Do not cache null — next call should retry the factory.
            return null;
        }

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = EntryTtl,
            Size = 1
        };
        _cache.Set(key, value, options);
        return value;
    }
}

using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public enum ViewSeedAuthResult
{
    Allowed,
    TooManyFailedAttempts,
    SeedViewLimitReached,
    InvalidPassword
}

/// <summary>
/// Stateful rate limiter + password gate for the ViewSeed endpoint.
/// Caps failed attempts and successful views at 3/hr per user (audit C5 requirement).
/// All state mutations happen inside <see cref="Evaluate"/> under an external lock.
/// </summary>
public class ViewSeedRateLimiter
{
    public const int MaxFailedAttempts = 3;
    public const int MaxSuccessfulViews = 3;

    readonly IMemoryCache _cache;
    readonly TimeSpan _ttl;

    public ViewSeedRateLimiter(IMemoryCache cache, TimeSpan? ttl = null)
    {
        _cache = cache;
        _ttl = ttl ?? TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Atomically checks rate limits, validates the password (via the supplied delegate),
    /// and updates counters. The caller is responsible for ensuring this runs under a
    /// per-user mutex so the read-then-write counter updates are serialized.
    /// </summary>
    public async Task<ViewSeedAuthResult> Evaluate(
        string userId, string? password, Func<string, Task<bool>> passwordVerifier)
    {
        var failKey = $"rgb:seed-fail:{userId}";
        var viewKey = $"rgb:seed-view:{userId}";

        var failed = _cache.GetOrCreate(failKey, e => { e.AbsoluteExpirationRelativeToNow = _ttl; return 0; });
        var views = _cache.GetOrCreate(viewKey, e => { e.AbsoluteExpirationRelativeToNow = _ttl; return 0; });

        if (failed >= MaxFailedAttempts) return ViewSeedAuthResult.TooManyFailedAttempts;
        if (views >= MaxSuccessfulViews) return ViewSeedAuthResult.SeedViewLimitReached;

        if (string.IsNullOrEmpty(password) || !await passwordVerifier(password))
        {
            _cache.Set(failKey, failed + 1, _ttl);
            return ViewSeedAuthResult.InvalidPassword;
        }

        _cache.Set(viewKey, views + 1, _ttl);
        return ViewSeedAuthResult.Allowed;
    }
}

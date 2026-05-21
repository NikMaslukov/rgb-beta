using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ViewSeedRateLimiterTests
{
    static ViewSeedRateLimiter MakeLimiter(TimeSpan? ttl = null) =>
        new(new MemoryCache(new MemoryCacheOptions()), ttl);

    [Fact]
    public async Task EmptyPassword_ReturnsInvalidPassword_AndIncrementsFailCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        var verifierCalled = false;
        var r1 = await limiter.Evaluate("u1", "", _ => { verifierCalled = true; return Task.FromResult(true); });

        Assert.Equal(ViewSeedAuthResult.InvalidPassword, r1);
        Assert.False(verifierCalled);
        Assert.Equal(1, cache.Get<int>("rgb:seed-fail:u1"));
        Assert.Equal(0, cache.Get<int>("rgb:seed-view:u1"));
    }

    [Fact]
    public async Task WrongPassword_ReturnsInvalidPassword_AndIncrementsFailCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        var r = await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));

        Assert.Equal(ViewSeedAuthResult.InvalidPassword, r);
        Assert.Equal(1, cache.Get<int>("rgb:seed-fail:u1"));
    }

    [Fact]
    public async Task CorrectPassword_ReturnsAllowed_AndIncrementsViewCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        var r = await limiter.Evaluate("u1", "right", _ => Task.FromResult(true));

        Assert.Equal(ViewSeedAuthResult.Allowed, r);
        Assert.Equal(0, cache.Get<int>("rgb:seed-fail:u1"));
        Assert.Equal(1, cache.Get<int>("rgb:seed-view:u1"));
    }

    [Fact]
    public async Task ThreeFailedAttempts_FourthBlockedWithTooManyFailed_VerifierNotCalled()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        for (int i = 0; i < 3; i++)
            await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));

        var verifierCalled = false;
        var r = await limiter.Evaluate("u1", "right",
            _ => { verifierCalled = true; return Task.FromResult(true); });

        Assert.Equal(ViewSeedAuthResult.TooManyFailedAttempts, r);
        Assert.False(verifierCalled);
    }

    [Fact]
    public async Task ThreeSuccessfulViews_FourthBlockedWithSeedViewLimit()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        for (int i = 0; i < 3; i++)
        {
            var r = await limiter.Evaluate("u1", "right", _ => Task.FromResult(true));
            Assert.Equal(ViewSeedAuthResult.Allowed, r);
        }

        var fourth = await limiter.Evaluate("u1", "right", _ => Task.FromResult(true));
        Assert.Equal(ViewSeedAuthResult.SeedViewLimitReached, fourth);
    }

    [Fact]
    public async Task DifferentUsers_HaveIndependentCounters()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        for (int i = 0; i < 3; i++)
            await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));

        var r = await limiter.Evaluate("u2", "right", _ => Task.FromResult(true));
        Assert.Equal(ViewSeedAuthResult.Allowed, r);
    }

    [Fact]
    public async Task FailCounter_ExpiresAfterTtl()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache, TimeSpan.FromMilliseconds(50));

        for (int i = 0; i < 3; i++)
            await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));

        await Task.Delay(100);
        var r = await limiter.Evaluate("u1", "right", _ => Task.FromResult(true));
        Assert.Equal(ViewSeedAuthResult.Allowed, r);
    }

    [Fact]
    public async Task SuccessfulAuth_DoesNotResetFailCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));
        await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));
        await limiter.Evaluate("u1", "right", _ => Task.FromResult(true));

        Assert.Equal(2, cache.Get<int>("rgb:seed-fail:u1"));
        Assert.Equal(1, cache.Get<int>("rgb:seed-view:u1"));
    }

    [Fact]
    public async Task RateLimitCheck_HappensBeforePasswordVerification()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        cache.Set("rgb:seed-fail:u1", 3, TimeSpan.FromHours(1));

        var verifierCalled = false;
        var r = await limiter.Evaluate("u1", "any",
            _ => { verifierCalled = true; return Task.FromResult(true); });

        Assert.Equal(ViewSeedAuthResult.TooManyFailedAttempts, r);
        Assert.False(verifierCalled);
    }

    [Fact]
    public async Task FailKeyDistinctFromViewKey()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new ViewSeedRateLimiter(cache);

        cache.Set("rgb:seed-view:u1", 3, TimeSpan.FromHours(1));

        var r = await limiter.Evaluate("u1", "wrong", _ => Task.FromResult(false));
        Assert.Equal(ViewSeedAuthResult.SeedViewLimitReached, r);
    }
}

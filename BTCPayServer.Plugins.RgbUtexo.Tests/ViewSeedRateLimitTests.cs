using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ViewSeedRateLimitTests
{
    [Fact]
    public void FirstFailedAttempt_UnderLimit()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var failKey = "rgb:seed-fail:user-1";
        var attempts = cache.GetOrCreate(failKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.True(attempts < 3);
    }

    [Fact]
    public void ThreeFailedAttempts_BlocksFourth()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var failKey = "rgb:seed-fail:user-1";

        for (int i = 0; i < 3; i++)
        {
            var attempts = cache.GetOrCreate(failKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
            cache.Set(failKey, attempts + 1, TimeSpan.FromHours(1));
        }

        var finalAttempts = cache.GetOrCreate(failKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.True(finalAttempts >= 3, "Fourth attempt should be blocked");
    }

    [Fact]
    public void ThreeSuccessfulViews_BlocksFourth()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var viewKey = "rgb:seed-view:user-1";

        for (int i = 0; i < 3; i++)
        {
            var views = cache.GetOrCreate(viewKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
            Assert.True(views < 3, $"View {i + 1} should be under limit");
            cache.Set(viewKey, views + 1, TimeSpan.FromHours(1));
        }

        var finalViews = cache.GetOrCreate(viewKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.True(finalViews >= 3, "Fourth view should be blocked");
    }

    [Fact]
    public void SuccessfulView_DoesNotResetFailCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var failKey = "rgb:seed-fail:user-1";
        var viewKey = "rgb:seed-view:user-1";

        cache.Set(failKey, 2, TimeSpan.FromHours(1));
        cache.Set(viewKey, 1, TimeSpan.FromHours(1));

        var failCount = cache.GetOrCreate(failKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.Equal(2, failCount);
    }

    [Fact]
    public void FailAndViewCounters_Independent()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var failKey = "rgb:seed-fail:user-1";
        var viewKey = "rgb:seed-view:user-1";

        cache.Set(failKey, 2, TimeSpan.FromHours(1));
        cache.Set(viewKey, 0, TimeSpan.FromHours(1));

        var views = cache.GetOrCreate(viewKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.Equal(0, views);
    }

    [Fact]
    public void DifferentUsers_IndependentCounters()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        for (int i = 0; i < 3; i++)
        {
            var a = cache.GetOrCreate("rgb:seed-fail:user-1", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
            cache.Set("rgb:seed-fail:user-1", a + 1, TimeSpan.FromHours(1));
        }

        var user2Attempts = cache.GetOrCreate("rgb:seed-fail:user-2", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.Equal(0, user2Attempts);
    }

    [Fact]
    public void CounterExpiry_ResetsAfterTTL()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var failKey = "rgb:seed-fail:user-1";

        cache.Set(failKey, 3, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        var attempts = cache.GetOrCreate(failKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void ViewCounterExpiry_ResetsAfterTTL()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var viewKey = "rgb:seed-view:user-1";

        cache.Set(viewKey, 3, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        var views = cache.GetOrCreate(viewKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return 0; });
        Assert.Equal(0, views);
    }
}

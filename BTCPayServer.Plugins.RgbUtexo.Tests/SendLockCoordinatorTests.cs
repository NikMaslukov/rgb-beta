using System.Collections.Concurrent;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SendLockCoordinatorTests
{
    sealed class Recorder
    {
        public readonly List<string> Events = new();
        public readonly HashSet<string> Marked = new();
        public readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

        public SendLockCoordinator Build() => new(
            Locks,
            (id, _) => { lock (Events) { Events.Add($"mark:{id}"); Marked.Add(id); } return Task.CompletedTask; },
            (id, _) => { lock (Events) { Events.Add($"clear:{id}"); Marked.Remove(id); } return Task.CompletedTask; },
            id => { lock (Events) { Events.Add($"evict:{id}"); } });
    }

    [Fact]
    public async Task WithSendLock_MarksBeforeOp_ClearsOnSuccess()
    {
        var r = new Recorder();
        var c = r.Build();
        await c.WithSendLockAsync("w", () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; });
        Assert.Equal(new[] { "mark:w", "op:w", "clear:w" }, r.Events);
        Assert.DoesNotContain("evict:w", r.Events);
        Assert.Empty(r.Marked);
    }

    [Fact]
    public async Task WithSendLock_OpThrows_LeavesMarked_Evicts_NoClear()
    {
        var r = new Recorder();
        var c = r.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.WithSendLockAsync("w", () => throw new InvalidOperationException("boom")));
        Assert.Equal(new[] { "mark:w", "evict:w" }, r.Events);
        Assert.Contains("w", r.Marked);
        Assert.DoesNotContain("clear:w", r.Events);
    }

    [Fact]
    public async Task WithSendLock_InnerRefreshFailurePropagates_LeavesMarked_Evicts_NoClear()
    {
        // Regression (Finding B blocker): a value-adding op (e.g. cleanup's post-op refresh)
        // MUST let a refresh/persist failure propagate out of the WithSendLock op. If the op
        // swallowed the failure, the coordinator would treat it as success and CLEAR the
        // quarantine over a possibly-incomplete Stock -> a later send could sign a burn.
        var r = new Recorder();
        var c = r.Build();
        async Task CleanupOpWhoseRefreshFails()
        {
            lock (r.Events) r.Events.Add("cleanup:w");
            await Task.Yield();
            throw new InvalidOperationException("refresh failed");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.WithSendLockAsync("w", CleanupOpWhoseRefreshFails));
        Assert.Contains("w", r.Marked);
        Assert.Contains("evict:w", r.Events);
        Assert.DoesNotContain("clear:w", r.Events);
    }

    [Fact]
    public async Task WithSendLock_SameWallet_IsMutuallyExclusive()
    {
        var r = new Recorder();
        var c = r.Build();
        int active = 0, maxActive = 0;
        async Task Op()
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref active);
        }
        await Task.WhenAll(
            c.WithSendLockAsync("w", Op),
            c.WithSendLockAsync("w", Op),
            c.WithSendLockAsync("w", Op));
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task WithSendLock_DifferentWallets_RunConcurrently()
    {
        var r = new Recorder();
        var c = r.Build();
        var gate = new TaskCompletionSource();
        var started = new CountdownEvent(2);
        Task Op()
        {
            started.Signal();
            return gate.Task;
        }
        var t1 = c.WithSendLockAsync("a", Op);
        var t2 = c.WithSendLockAsync("b", Op);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        gate.SetResult();
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task InlineWriteAhead_UnderHeldLock_DoesNotSelfDeadlock()
    {
        var r = new Recorder();
        var c = r.Build();
        // Simulate a send that already holds _sendLocks (acquired directly), then performs an
        // inline write-ahead for the same wallet — must not re-acquire and deadlock.
        var sendLock = r.Locks.GetOrAdd("w", _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync();
        try
        {
            var completed = c.WriteAheadInlineAsync("w",
                () => { lock (r.Events) r.Events.Add("inline:w"); return Task.CompletedTask; });
            var done = await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(completed, done);
        }
        finally { sendLock.Release(); }
        Assert.Equal(new[] { "mark:w", "inline:w", "clear:w" }, r.Events);
    }

    [Fact]
    public async Task TryWithSendLock_WhenHeld_SkipsWithoutRunning()
    {
        var r = new Recorder();
        var c = r.Build();
        var sendLock = r.Locks.GetOrAdd("w", _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync();
        try
        {
            var ran = false;
            var acquired = await c.TryWithSendLockAsync("w", () => { ran = true; return Task.CompletedTask; });
            Assert.False(acquired);
            Assert.False(ran);
            Assert.Empty(r.Events);
        }
        finally { sendLock.Release(); }
    }

    [Fact]
    public async Task TryWithSendLock_WhenFree_RunsWithWriteAhead()
    {
        var r = new Recorder();
        var c = r.Build();
        var acquired = await c.TryWithSendLockAsync("w",
            () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; });
        Assert.True(acquired);
        Assert.Equal(new[] { "mark:w", "op:w", "clear:w" }, r.Events);
    }
}

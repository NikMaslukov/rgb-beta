using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class SendLockCoordinator
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;
    readonly Func<string, CancellationToken, Task> _mark;
    readonly Func<string, CancellationToken, Task> _clear;
    readonly Action<string> _evict;

    public SendLockCoordinator(
        ConcurrentDictionary<string, SemaphoreSlim> locks,
        Func<string, CancellationToken, Task> mark,
        Func<string, CancellationToken, Task> clear,
        Action<string> evict)
    {
        _locks = locks;
        _mark = mark;
        _clear = clear;
        _evict = evict;
    }

    public async Task<T> WithSendLockAsync<T>(string walletId, Func<Task<T>> op, CancellationToken ct = default)
    {
        var sendLock = _locks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
            return await WriteAheadAsync(walletId, op, ct);
        }
        finally { sendLock.Release(); }
    }

    public Task WithSendLockAsync(string walletId, Func<Task> op, CancellationToken ct = default)
        => WithSendLockAsync<object?>(walletId, async () => { await op(); return null; }, ct);

    public async Task<bool> TryWithSendLockAsync(string walletId, Func<Task> op, CancellationToken ct = default)
    {
        var sendLock = _locks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        if (!await sendLock.WaitAsync(0, ct))
            return false;
        try
        {
            await WriteAheadAsync<object?>(walletId, async () => { await op(); return null; }, ct);
            return true;
        }
        finally { sendLock.Release(); }
    }

    // Write-ahead WITHOUT acquiring the send lock: callers that already hold it (in-send
    // refreshes, send_end, setup/restore reconciliation) use this to avoid self-deadlock.
    public async Task WriteAheadInlineAsync(string walletId, Func<Task> op, CancellationToken ct = default)
        => await WriteAheadAsync<object?>(walletId, async () => { await op(); return null; }, ct);

    async Task<T> WriteAheadAsync<T>(string walletId, Func<Task<T>> op, CancellationToken ct)
    {
        await _mark(walletId, ct);
        T result;
        try
        {
            result = await op();
        }
        catch
        {
            try { _evict(walletId); } catch { }
            throw;
        }
        await _clear(walletId, ct);
        return result;
    }
}

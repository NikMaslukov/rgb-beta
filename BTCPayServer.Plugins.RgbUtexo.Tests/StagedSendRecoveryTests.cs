using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Data.Sqlite;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class StagedSendRecoveryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"rgb-staged-send-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(_dir, "rgb_lib_db");

    [Fact]
    public async Task Discovery_ReturnsOnlyOutboundWaitingCounterpartyBatches()
    {
        await CreateSchema();
        await InsertBatch(batch: 10, status: 0, incoming: false);
        await InsertBatch(batch: 11, status: 0, incoming: true);
        await InsertBatch(batch: 12, status: 1, incoming: false);
        await InsertBatch(batch: 13, status: 4, incoming: false);

        var found = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);

        Assert.Equal(new[] { 10 }, found);
    }

    [Fact]
    public async Task Discovery_DeduplicatesMultiAssetBatchAndIsDeterministicallyOrdered()
    {
        await CreateSchema();
        await InsertBatch(batch: 20, status: 0, incoming: false);
        await InsertTransferForExistingBatch(batch: 20, incoming: false);
        await InsertBatch(batch: 3, status: 0, incoming: false);

        var found = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);

        Assert.Equal(new[] { 3, 20 }, found);
    }

    [Fact]
    public async Task Discovery_IsIdempotentAfterBatchWasFailed()
    {
        await CreateSchema();
        await InsertBatch(batch: 7, status: 0, incoming: false);
        Assert.Equal(new[] { 7 }, await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));

        await Execute("UPDATE batch_transfer SET status = 4 WHERE idx = 7");

        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public async Task MissingDatabase_HasNoOrphans()
    {
        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public async Task Discovery_IsMemoryBoundedAndAdvancesByDurableStatus()
    {
        await CreateSchema();
        for (var i = 1; i <= RGBWalletService.StagedRecoveryBatchSize + 5; i++)
            await InsertBatch(i, status: 0, incoming: false);

        var first = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);
        Assert.Equal(RGBWalletService.StagedRecoveryBatchSize, first.Count);
        Assert.Equal(Enumerable.Range(1, RGBWalletService.StagedRecoveryBatchSize), first);
        await Execute($"UPDATE batch_transfer SET status = 4 WHERE idx <= {RGBWalletService.StagedRecoveryBatchSize}");

        Assert.Equal(Enumerable.Range(RGBWalletService.StagedRecoveryBatchSize + 1, 5),
            await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public void RecoveryJournal_IsDurableOverwriteableAndIdempotentlyDeletable()
    {
        var path = Path.Combine(_dir, "fingerprint", RgbSendRecoveryJournal.FileName);

        RgbSendRecoveryJournal.Write(path, RgbSendRecoveryPhase.Staged);
        Assert.Equal(RgbSendRecoveryPhase.Staged, RgbSendRecoveryJournal.Read(path));

        RgbSendRecoveryJournal.Write(path, RgbSendRecoveryPhase.SendEndIndeterminate);
        Assert.Equal(RgbSendRecoveryPhase.SendEndIndeterminate, RgbSendRecoveryJournal.Read(path));

        RgbSendRecoveryJournal.Delete(path);
        RgbSendRecoveryJournal.Delete(path);
        Assert.Null(RgbSendRecoveryJournal.Read(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void CorruptRecoveryJournal_FailsClosed()
    {
        var path = Path.Combine(_dir, "fingerprint", RgbSendRecoveryJournal.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "unknown");

        Assert.Throws<InvalidDataException>(() => RgbSendRecoveryJournal.Read(path));
    }

    async Task CreateSchema()
    {
        Directory.CreateDirectory(_dir);
        await Execute("""
            CREATE TABLE batch_transfer (idx INTEGER PRIMARY KEY, status INTEGER NOT NULL);
            CREATE TABLE asset_transfer (idx INTEGER PRIMARY KEY, batch_transfer_idx INTEGER NOT NULL);
            CREATE TABLE transfer (idx INTEGER PRIMARY KEY, asset_transfer_idx INTEGER NOT NULL, incoming INTEGER NOT NULL);
            """);
    }

    async Task InsertBatch(int batch, int status, bool incoming)
    {
        await Execute($"INSERT INTO batch_transfer(idx,status) VALUES({batch},{status})");
        await InsertTransferForExistingBatch(batch, incoming);
    }

    async Task InsertTransferForExistingBatch(int batch, bool incoming)
    {
        var asset = await ScalarLong("SELECT COALESCE(MAX(idx),0)+1 FROM asset_transfer");
        var transfer = await ScalarLong("SELECT COALESCE(MAX(idx),0)+1 FROM transfer");
        await Execute($"INSERT INTO asset_transfer(idx,batch_transfer_idx) VALUES({asset},{batch})");
        await Execute($"INSERT INTO transfer(idx,asset_transfer_idx,incoming) VALUES({transfer},{asset},{(incoming ? 1 : 0)})");
    }

    async Task Execute(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={DbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    async Task<long> ScalarLong(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={DbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}

using System.IO.Compression;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RestoreReservedSingleFileNameTests
{
    public RestoreReservedSingleFileNameTests()
    {
        typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
    }

    const string SyntheticMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    const string FakeLibFingerprint = "00000000";

    [Fact]
    public void AnEmptyDirectoryAtTheParentLeaseName_IsFoundSoTheWalletIsNeverBrickedByAcquireParent()
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, RgbNativeSendLease.ParentFileName));

        Assert.Equal(RgbNativeSendLease.ParentFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Theory]
    [InlineData(RgbNativeSendLease.ParentFileName)]
    [InlineData(RgbNativeSendLease.WorkerFileName)]
    [InlineData(RgbNativeSendLease.WalletAccessFileName)]
    [InlineData(RgbNativeSendLease.RgbRuntimeLockFileName)]
    [InlineData(RgbSendRecoveryJournal.FileName)]
    [InlineData(RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName)]
    [InlineData(RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30NonWatchOnlyBdkStoreRepairTempFileName)]
    public void EveryReservedSingleFileNameIsFoundAsADirectory_BecauseEachIsOpenedOrRenamedAsARegularFileOnASendOrDeletePath(
        string reservedName)
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, reservedName));

        Assert.Equal(reservedName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void TheReservedNameSetIsExactlyFivePluginOwnedSingleFilePathsPlusTwoWrittenOnlyByThePinnedRgbLib()
    {
        Assert.Equal(
            new[]
            {
                RgbNativeSendLease.ParentFileName,
                RgbNativeSendLease.WorkerFileName,
                RgbNativeSendLease.WalletAccessFileName,
                RgbNativeSendLease.RgbRuntimeLockFileName,
                RgbSendRecoveryJournal.FileName,
                "bdk_db_watch_only.recovering",
                "bdk_db.recovering"
            },
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories);

        Assert.All(
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories.Take(5),
            name => Assert.False(
                RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant
                    .Contains(name),
                "the first five reserved names are sourced from the managed constants that own them "
                + "(RgbNativeSendLease.*, RgbSendRecoveryJournal.FileName), so they cannot drift away from their "
                + "writer without a compile break"));

        Assert.Equal(
            new[] { "bdk_db_watch_only.recovering", "bdk_db.recovering" },
            RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant);

        Assert.Equal(
            RgbStockDurability.WatchOnlyBdkStoreFileName + ".recovering",
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName);
    }

    [Fact]
    public void TheTwoBdkRepairTempNamesAreDifferentInKind_TheyHaveNoManagedWriterAndAreCreatedByThePinnedRgbLibItself()
    {
        var writtenByRgbLib =
            RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant;

        Assert.All(writtenByRgbLib, name => Assert.EndsWith(
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib,
            name));

        var pluginOwnedConstants = new[]
        {
            RgbNativeSendLease.ParentFileName,
            RgbNativeSendLease.WorkerFileName,
            RgbNativeSendLease.WalletAccessFileName,
            RgbNativeSendLease.RgbRuntimeLockFileName,
            RgbSendRecoveryJournal.FileName
        };
        Assert.All(writtenByRgbLib, name => Assert.DoesNotContain(name, pluginOwnedConstants));

        Assert.All(writtenByRgbLib, name => Assert.Equal(
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib,
            Path.GetExtension(name)));
    }

    [Fact]
    public void TheRgbLibVersionThisReservedNameWasReadOutOfIsStillTheOneReferenced_SoAnUpgradeForcesARereadOfLoadOrRecoverBdkStore()
    {
        var csproj = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "BTCPayServer.Plugins.RgbUtexo.csproj"));

        Assert.True(
            csproj.Contains("Include=\"RgbLib\" Version=\"0.3.0-beta.30\""),
            "the reserved names \"bdk_db_watch_only.recovering\" and \"bdk_db.recovering\" have no managed constant "
            + "behind them; they were read out of rgb-lib 0.3.0-beta.30's load_or_recover_bdk_store, which composes "
            + "the path as path.with_extension(\"recovering\"), calls fs::remove_file on it while DISCARDING the "
            + "error, and then fails Store::create if a directory still stands there. If RgbLib is being bumped, "
            + "re-read load_or_recover_bdk_store in the new version and re-derive this list before changing this pin");
    }

    [Fact]
    public void ADirectoryAtTheSendRecoveryJournalName_IsFoundBecauseTheJournalIsRenamedOntoThatPathAfterNeedsRecoveryIsAlreadyCommitted()
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, RgbSendRecoveryJournal.FileName));

        Assert.Equal(RgbSendRecoveryJournal.FileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ADirectoryAtTheJournalPathDefeatsBothTheFileExistsSendGateAndTheJournalWrite_WhichIsWhyRestoreMustRefuseIt()
    {
        using var walletData = new TempTree();
        var journalPath = RgbSendRecoveryJournal.PathFor(walletData.Path, FakeLibFingerprint);
        Directory.CreateDirectory(journalPath);

        Assert.False(File.Exists(journalPath),
            "File.Exists is false for a directory, so every pre-send and pre-delete gate that spells the "
            + "quarantine check as File.Exists(journal) admits the send");

        var thrown = Record.Exception(() =>
            RgbSendRecoveryJournal.Write(journalPath, RgbSendRecoveryPhase.Staged));

        Assert.True(thrown is IOException or UnauthorizedAccessException,
            $"renaming the journal onto a directory threw {thrown?.GetType().Name ?? "nothing"}; the write must "
            + "fail so that this test keeps describing the real brick, which is a send that has already "
            + "committed NeedsRecovery and then cannot write its journal");
        Assert.True(Directory.Exists(journalPath),
            "the failed write left no directory behind, so the condition would be self-clearing; it is not, "
            + "which is why it must be refused at restore time instead");
    }

    [Fact]
    public void ReservedNamesPresentAsRegularFilesAreAccepted_BecauseAGenuineBackupOfASentWalletCarriesThem()
    {
        using var staging = new TempTree();
        foreach (var reservedName in RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories)
            staging.WriteFile(Path.Combine(FakeLibFingerprint, reservedName), 32);

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ACaseVariantOfAReservedNameIsFound_BecauseMacOsAndWindowsResolveItToTheSameSingleFilePath()
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, ".SEND-Helper-Parent"));

        Assert.Equal(RgbNativeSendLease.ParentFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void AnOrdinaryWalletTreeIsAccepted_SoTheRefusalCannotStrandAHealthyBackup()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, "rgb_lib_db"), 128);
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "assets"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "media"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "transfers"));

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void TheRefusalMessageNamesTheOffendingEntryAndTellsTheOperatorWhatToDoWithoutShellAccess()
    {
        var message = RGBWalletService.ReservedSingleFileNameUsedAsDirectoryRefusal(
            RgbNativeSendLease.ParentFileName);

        Assert.Contains(RgbNativeSendLease.ParentFileName, message);
        Assert.Contains("Restore a backup taken by this plugin", message);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheParentLeaseName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(RgbNativeSendLease.ParentFileName);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheSendRecoveryJournalName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(RgbSendRecoveryJournal.FileName);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheWatchOnlyBdkStoreRepairTempName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName);
    }

    [Fact]
    public void ADirectoryAtTheWatchOnlyBdkStoreRepairTempNameIsFound_BecauseRgbLibsSelfHealingRemoveFileCannotRemoveItAndItsErrorIsDiscarded()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, RgbStockDurability.WatchOnlyBdkStoreFileName), 1024);
        staging.MakeDir(Path.Combine(FakeLibFingerprint,
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName));

        Assert.Equal(
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ADirectoryAtTheBdkRepairTempNameSurvivesAFileDelete_WhichIsWhyTheConditionIsNotSelfClearingAndMustBeRefusedAtRestore()
    {
        using var walletData = new TempTree();
        var repairTempPath = Path.Combine(walletData.Path, FakeLibFingerprint,
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName);
        Directory.CreateDirectory(repairTempPath);

        Assert.False(File.Exists(repairTempPath),
            "a directory is invisible to a regular-file existence probe, which is exactly why rgb-lib's "
            + "fs::remove_file of the repair temp path is a no-op it then discards");

        var deleteAttempt = Record.Exception(() => File.Delete(repairTempPath));
        Assert.True(deleteAttempt is UnauthorizedAccessException or IOException,
            $"deleting the planted directory as a file threw {deleteAttempt?.GetType().Name ?? "nothing"}; rgb-lib "
            + "spells this as `let _ = fs::remove_file(&tmp_path)` and drops the error, so the directory stays");
        Assert.True(Directory.Exists(repairTempPath),
            "the planted directory cleared itself, so an interrupted BDK append would self-heal and no refusal "
            + "would be needed; it does not, and every later wallet reconstruction fails at Store::create");
    }

    [Fact]
    public void TheCorruptForensicNamesAreNotReserved_BecauseUniqueCorruptPathSkipsAnyPlantedDirectoryAndSelfAvoids()
    {
        Assert.DoesNotContain("bdk_db_watch_only.corrupt",
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories);
        Assert.DoesNotContain("bdk_db_watch_only.corrupt.1",
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories);

        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "bdk_db_watch_only.corrupt"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "bdk_db_watch_only.corrupt.1"));

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    static async Task AssertRestoreIsRefusedForDirectoryAt(string reservedName)
    {
        var runner = new StagingShapingRunner(staging =>
            Directory.CreateDirectory(Path.Combine(staging, FakeLibFingerprint, reservedName)));
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-reserved-name-{Guid.NewGuid():N}"));
        var svc = BuildService(runner, cfg);
        using var backup = new TempBackup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", SyntheticMnemonic, backup.Path, "pw", "signet"));

        Assert.Equal(
            RGBWalletService.ReservedSingleFileNameUsedAsDirectoryRefusal(reservedName),
            ex.Message);
        Assert.False(Directory.Exists(runner.StagingDir),
            $"staging dir {runner.StagingDir} survived the refusal; a rejected restore must leave no tree behind");
        var walletsParent = Path.GetDirectoryName(cfg.GetWalletDataDir("probe", "signet"))!;
        var finalizedDirs = Directory.Exists(walletsParent)
            ? Directory.GetDirectories(walletsParent)
            : Array.Empty<string>();
        Assert.True(finalizedDirs.Length == 0,
            $"{finalizedDirs.Length} wallet data dir(s) were finalized under {walletsParent}; the refusal must "
            + "happen before Directory.Move so no unusable wallet dir is ever published");
        try { Directory.Delete(cfg.RgbBaseDir, true); } catch { }
    }

    [Fact]
    public void TheReservedNameCheckRunsInsideRestoreFromBackupAsyncAheadOfDirectoryMove()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var method = RoslynPins.Method(tree, "RGBWalletService", "RestoreFromBackupAsync");
        var body = RoslynPins.BodyOf(method);

        var checks = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText:
                "FindDirectoryAtAReservedSingleFileName" })
            .ToList();
        Assert.True(checks.Count == 1,
            $"RestoreFromBackupAsync invokes FindDirectoryAtAReservedSingleFileName {checks.Count} time(s); "
            + "exactly one call must stand between extraction and finalization, or a restored directory at a "
            + "reserved single-file name reaches disk and the wallet can then neither send nor be deleted");
        RoslynPins.AssertBindsToMemberOf(plugin, tree, checks[0].Expression, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService",
            "FindDirectoryAtAReservedSingleFileName",
            "RestoreFromBackupAsync's reserved-single-file-name check");

        var moves = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax access
                        && RoslynPins.NamesBclMember(access, "Directory", "Move"))
            .ToList();
        Assert.True(moves.Count == 1,
            $"RestoreFromBackupAsync performs {moves.Count} Directory.Move call(s); the pin compares the check "
            + "against exactly one finalization point");
        Assert.True(checks[0].SpanStart < moves[0].SpanStart,
            "the reserved-single-file-name check must precede Directory.Move; running it afterwards leaves the hostile "
            + "directory inside the live wallet data dir, which is the permanently unusable wallet this refusal exists to prevent");

        var deferredHost = checks[0].Ancestors()
            .TakeWhile(node => !ReferenceEquals(node, body))
            .FirstOrDefault(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        Assert.True(deferredHost == null,
            $"the only call to FindDirectoryAtAReservedSingleFileName sits inside a {deferredHost?.GetType().Name}; a call "
            + "reachable only through a local function or lambda that nothing invokes satisfies every lexical "
            + "clause above while no restore is ever checked");

        var declarator = checks[0].Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        Assert.True(declarator != null
                    && ReferenceEquals(declarator.Initializer?.Value, checks[0]),
            "the result of FindDirectoryAtAReservedSingleFileName must initialize a local; a call whose returned "
            + "value is discarded satisfies a call-site pin while every hostile backup is still accepted");
        var resultName = declarator!.Identifier.ValueText;
        RoslynPins.AssertNeverReassigned(method, resultName);

        var guards = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => statement.SpanStart > checks[0].SpanStart)
            .Where(statement => statement.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == resultName))
            .Where(statement => statement.Statement is BlockSyntax block
                                && block.Statements.OfType<ThrowStatementSyntax>().Any())
            .ToList();
        Assert.True(guards.Count == 1,
            $"'{resultName}' flows into {guards.Count} condition(s) that directly throw; exactly one must exist, "
            + "or the checked value never refuses the restore it was computed for");
        Assert.True(guards[0].Span.End < moves[0].SpanStart,
            $"the throw guarded by '{resultName}' does not complete before Directory.Move; the refusal must be "
            + "raised while the tree is still staging, not after finalization");

        RoslynPins.AssertNoLocalShadow(method, "FindDirectoryAtAReservedSingleFileName");
    }

    static RGBWalletService BuildService(IRestoreProcessRunner runner, RGBConfiguration cfg)
    {
        var rgbLib = new FakeRgbLib(cfg);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString =
                "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=1;Command Timeout=1"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(runner, cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec, null!);
    }

    sealed class StagingShapingRunner : IRestoreProcessRunner
    {
        readonly Action<string> _shape;
        public string StagingDir { get; private set; } = "";

        public StagingShapingRunner(Action<string> shape) => _shape = shape;

        public Task<RestoreRunResult> RunAsync(
            string backupPath, string stagingDir, string password, RestoreLimits limits, CancellationToken ct)
        {
            StagingDir = stagingDir;
            Directory.CreateDirectory(stagingDir);
            _shape(stagingDir);
            return Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 0, "", true));
        }
    }

    sealed class TempBackup : IDisposable
    {
        public string Path { get; }

        public TempBackup()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"rgb-reserved-name-backup-{Guid.NewGuid():N}.rgb");
            using var fs = File.Create(Path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            using (var enc = zip.CreateEntry("backup.enc").Open())
                enc.Write(new byte[16]);
            using var pub = new StreamWriter(zip.CreateEntry("backup.pub_data").Open());
            pub.Write("""{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""");
        }

        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    sealed class TempTree : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rgb-reserved-name-staging-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(Path);

        public void MakeDir(string relative) =>
            Directory.CreateDirectory(System.IO.Path.Combine(Path, relative));

        public void WriteFile(string relative, int bytes)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[bytes]);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}

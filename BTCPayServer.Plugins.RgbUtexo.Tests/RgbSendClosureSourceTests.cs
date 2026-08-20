namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSendClosureSourceTests
{
    static string Source()
    {
        var root = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RgbSendClosureSourceTests).Assembly)
            .Single(a => a.Key == "RepoRoot").Value!;
        return File.ReadAllText(Path.Combine(root, "Services", "RGBWalletService.cs"));
    }

    [Fact]
    public void EndpointNativeCallsAreOnlyMadeThroughTheKillableWorker()
    {
        var source = Source();
        Assert.DoesNotContain("_rgbLib.SendBeginAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_rgbLib.SendEndAsync(", source, StringComparison.Ordinal);
        Assert.Contains("RunNativeSendIsolatedAsync(\n                wallet, \"send-begin\"", source, StringComparison.Ordinal);
        Assert.Contains("RunNativeSendIsolatedAsync(\n                    wallet, \"send-end\"", source, StringComparison.Ordinal);
        Assert.Contains("if (!result.ChildReaped)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPostSendBeginOrdinaryFailureRunsDurableReconciliation()
    {
        var source = Source();
        Assert.Contains("sendBeginMayHaveRun && !sendEndStarted", source, StringComparison.Ordinal);
        Assert.Contains("await ReconcileWalletRecoveryAsync(wallet, CancellationToken.None)", source,
            StringComparison.Ordinal);
        Assert.Contains("RgbSendRecoveryPhase.SendEndIndeterminate", source, StringComparison.Ordinal);
        Assert.Contains("durable quarantine retained", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InvoiceHintsCannotBypassRefreshAndExpiryCleanup()
    {
        var root = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RgbSendClosureSourceTests).Assembly)
            .Single(a => a.Key == "RepoRoot").Value!;
        var source = File.ReadAllText(Path.Combine(root, "Services", "RGBInvoiceListener.cs"));
        var start = source.IndexOf("async Task CheckSingleInvoice", StringComparison.Ordinal);
        var end = source.IndexOf("internal static bool ShouldEnqueue", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("_queue.RequestRecovery()", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessTransfers(", method, StringComparison.Ordinal);
    }
}

using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SendAssetCrossNetworkTests
{
    [Fact]
    public void MatchedRegtest_DoesNotThrow()
    {
        RGBWalletService.EnsureInvoiceNetworkMatchesWallet("Regtest", "regtest");
    }

    [Fact]
    public void TestnetInvoice_OnRegtestWallet_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RGBWalletService.EnsureInvoiceNetworkMatchesWallet("Testnet", "regtest"));
        Assert.Contains("Testnet", ex.Message);
        Assert.Contains("Regtest", ex.Message);
    }

    [Fact]
    public void MainnetInvoice_OnTestnetWallet_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RGBWalletService.EnsureInvoiceNetworkMatchesWallet("Mainnet", "testnet"));
        Assert.Contains("Mainnet", ex.Message);
        Assert.Contains("Testnet", ex.Message);
    }

    [Fact]
    public void SignetInvoice_OnMainnetWallet_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RGBWalletService.EnsureInvoiceNetworkMatchesWallet("Signet", "mainnet"));
        Assert.Contains("Signet", ex.Message);
        Assert.Contains("Mainnet", ex.Message);
    }

    [Fact]
    public void EmptyNetwork_OnRegtestWallet_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RGBWalletService.EnsureInvoiceNetworkMatchesWallet("", "regtest"));
    }

    [Fact]
    public void CaseInsensitive_REGTEST_OnRegtest_DoesNotThrow()
    {
        RGBWalletService.EnsureInvoiceNetworkMatchesWallet("REGTEST", "regtest");
    }

    [Fact]
    public void SendAssetInternalAsync_CallsCrossNetworkCheck_BetweenDecodeAndValidate()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "RGBWalletService.cs"));
        Assert.True(File.Exists(sourcePath),
            $"Could not locate RGBWalletService.cs at {sourcePath}");

        var content = File.ReadAllText(sourcePath);

        var bodyMatch = Regex.Match(content,
            @"SendAssetInternalAsync\s*\([^)]*\)\s*\{(?<body>.*?)\n\s{4}\}",
            RegexOptions.Singleline);
        Assert.True(bodyMatch.Success, "Could not locate SendAssetInternalAsync method body");

        var body = bodyMatch.Groups["body"].Value;
        var idxDecode = body.IndexOf("DecodeInvoice(", StringComparison.Ordinal);
        var idxEnsure = body.IndexOf("EnsureInvoiceNetworkMatchesWallet(", StringComparison.Ordinal);
        var idxValidate = body.IndexOf("ValidateSendAssetRequest(", StringComparison.Ordinal);

        Assert.True(idxDecode >= 0, "DecodeInvoice call not found in SendAssetInternalAsync body");
        Assert.True(idxEnsure >= 0,
            "EnsureInvoiceNetworkMatchesWallet call not found in SendAssetInternalAsync body — " +
            "cross-network check is not wired into the send path");
        Assert.True(idxValidate >= 0, "ValidateSendAssetRequest call not found in SendAssetInternalAsync body");
        Assert.True(idxDecode < idxEnsure,
            $"EnsureInvoiceNetworkMatchesWallet must come AFTER DecodeInvoice (got Decode@{idxDecode}, Ensure@{idxEnsure})");
        Assert.True(idxEnsure < idxValidate,
            $"EnsureInvoiceNetworkMatchesWallet must come BEFORE ValidateSendAssetRequest (got Ensure@{idxEnsure}, Validate@{idxValidate})");
    }
}

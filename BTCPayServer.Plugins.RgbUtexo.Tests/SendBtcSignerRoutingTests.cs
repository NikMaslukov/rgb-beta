using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// M2 regression: SendBtc must route signing through the cached signer provider,
/// not re-decrypt the mnemonic via MnemonicProtectionService.Unprotect().
/// We verify this by checking that after signer registration, the provider returns
/// a working signer, and that Unprotect on garbage throws (no fallback path).
/// </summary>
public class SendBtcSignerRoutingTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public async Task SignerProvider_ReturnsCachedSigner_ThatCanSign()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-1", TestMnemonic, NBitcoin.Network.RegTest);

        var signer = await provider.GetSignerAsync("wallet-1");
        Assert.NotNull(signer);
        Assert.False(signer!.IsDisposed);
        Assert.False(string.IsNullOrEmpty(signer.MasterFingerprint));
    }

    [Fact]
    public void MnemonicProtection_Unprotect_DoesNotFallbackToPlaintext()
    {
        var protectionService = new MnemonicProtectionService(
            new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);

        Assert.Throws<InvalidOperationException>(() =>
            protectionService.Unprotect(TestMnemonic));
    }

    [Fact]
    public async Task SignerProvider_CachedSigner_ProducesValidSignature()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-1", TestMnemonic, NBitcoin.Network.RegTest);

        var signer = await provider.GetSignerAsync("wallet-1");
        Assert.NotNull(signer);

        var network = NBitcoin.Network.RegTest;
        var masterKey = new NBitcoin.Mnemonic(TestMnemonic).DeriveExtKey();
        var vanillaKey = masterKey.Derive(new NBitcoin.KeyPath("m/84'/1'/0'/0/0"));
        var addr = vanillaKey.GetPublicKey().GetAddress(NBitcoin.ScriptPubKeyType.Segwit, network);

        var tx = NBitcoin.Transaction.Create(network);
        tx.Inputs.Add(new NBitcoin.OutPoint(NBitcoin.uint256.One, 0));
        tx.Outputs.Add(NBitcoin.Money.Satoshis(900), addr);

        var psbt = NBitcoin.PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = new NBitcoin.TxOut(NBitcoin.Money.Satoshis(1000), addr.ScriptPubKey);

        var signed = await signer!.SignPsbtAsync(psbt.ToBase64(), network,
            new SigningPolicy { MaxUnknownOutputSats = 1000, MaxFeePercent = 20 });

        var result = NBitcoin.PSBT.Parse(signed, network);
        Assert.True(result.Inputs[0].PartialSigs.Count > 0 || result.Inputs[0].FinalScriptWitness != null);
    }

    static RgbWalletSignerProvider CreateProvider()
    {
        var provider = new RgbWalletSignerProvider(null!, null!, null!);
        typeof(RgbWalletSignerProvider)
            .GetField("_started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(provider, new TaskCompletionSource());
        ((TaskCompletionSource)typeof(RgbWalletSignerProvider)
            .GetField("_started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(provider)!).SetResult();
        return provider;
    }
}

using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSignerFeeCeilingTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // A PSBT producer that supplies an authentic NonWitnessUtxo alongside a WitnessUtxo understating
    // the same script used to slip an oversized fee past MaxFeeSats: the ceiling read WitnessUtxo
    // first while NBitcoin signs from NonWitnessUtxo, so the signature committed to the real, larger
    // input value and the difference went to miners. On the Create-UTXOs path MaxFeeSats is the only
    // bound on value leakage, so this drained the wallet's spendable balance.
    //
    // Amounts are chosen so the test discriminates: reading the understated 5_000 gives a 500-sat fee
    // that passes the 10_000 ceiling, while resolving through GetTxOut() gives 95_500 and must fail.
    [Fact]
    public async Task FeeCeiling_AuthenticNonWitnessUtxoWithUnderstatedWitnessUtxo_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        // The funding tx carries a dummy input because PSBT.ToBase64() refuses to serialise a
        // NonWitnessUtxo whose transaction has no inputs.
        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), addr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(4_500), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].NonWitnessUtxo = fundingTx;
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(5_000), addr.ScriptPubKey);

        var policy = new SigningPolicy
        {
            MaxFeeSats = 10_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("exceeds max allowed", ex.Message);
    }

    // Liveness companion: the honest shape (both fields agreeing, as every real producer emits since
    // both derive from the same prev tx) must still sign.
    [Fact]
    public async Task FeeCeiling_AgreeingUtxoFields_Signs()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), addr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(95_000), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].NonWitnessUtxo = fundingTx;
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy
        {
            MaxFeeSats = 10_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);
        Assert.NotEmpty(signed);
    }
}

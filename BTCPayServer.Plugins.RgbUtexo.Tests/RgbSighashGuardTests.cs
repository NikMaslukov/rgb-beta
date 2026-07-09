using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSighashGuardTests
{
    static readonly Network Net = Network.RegTest;

    static PSBT SingleInputPsbt()
    {
        var key = new Key();
        var script = key.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(new TxOut(Money.Coins(1), script));
        var psbt = tx.CreatePSBT(Net);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Coins(1), script);
        return psbt;
    }

    [Fact]
    public void NoSighashSet_Passes()
    {
        RgbSighashGuard.EnsureAllInputsAllowed(SingleInputPsbt());
    }

    [Theory]
    [InlineData(TaprootSigHash.Default)]
    [InlineData(TaprootSigHash.All)]
    public void AllowedTaprootSighash_Passes(TaprootSigHash sighash)
    {
        var psbt = SingleInputPsbt();
        psbt.Inputs[0].TaprootSighashType = sighash;
        RgbSighashGuard.EnsureAllInputsAllowed(psbt);
    }

    [Theory]
    [InlineData(TaprootSigHash.None)]
    [InlineData(TaprootSigHash.Single)]
    [InlineData(TaprootSigHash.AnyoneCanPay)]
    [InlineData(TaprootSigHash.All | TaprootSigHash.AnyoneCanPay)]
    public void DisallowedTaprootSighash_Throws(TaprootSigHash sighash)
    {
        var psbt = SingleInputPsbt();
        psbt.Inputs[0].TaprootSighashType = sighash;
        Assert.Throws<InvalidOperationException>(() => RgbSighashGuard.EnsureAllInputsAllowed(psbt));
    }

    [Fact]
    public void LegacyAllSighash_Passes()
    {
        var psbt = SingleInputPsbt();
        psbt.Inputs[0].SighashType = SigHash.All;
        RgbSighashGuard.EnsureAllInputsAllowed(psbt);
    }

    [Theory]
    [InlineData(SigHash.None)]
    [InlineData(SigHash.Single)]
    [InlineData(SigHash.All | SigHash.AnyoneCanPay)]
    public void DisallowedLegacySighash_Throws(SigHash sighash)
    {
        var psbt = SingleInputPsbt();
        psbt.Inputs[0].SighashType = sighash;
        Assert.Throws<InvalidOperationException>(() => RgbSighashGuard.EnsureAllInputsAllowed(psbt));
    }
}

using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPsbtInspectorTests
{
    static readonly Network Net = Network.RegTest;

    static Script Opret(byte[] data) => new Script(OpcodeType.OP_RETURN, Op.GetPushOp(data));

    static Script TaprootScript() => new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

    static PSBT PsbtWithOutputs(params TxOut[] outs)
    {
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        foreach (var o in outs) tx.Outputs.Add(o);
        return tx.CreatePSBT(Net);
    }

    [Fact]
    public void SingleOpret_ReturnsThe32Bytes()
    {
        var commitment = new byte[32];
        for (int i = 0; i < 32; i++) commitment[i] = (byte)(i + 1);
        var psbt = PsbtWithOutputs(
            new TxOut(Money.Zero, Opret(commitment)),
            new TxOut(Money.Coins(1), TaprootScript()));

        var read = RgbPsbtInspector.ReadOpretCommitment(psbt);
        Assert.Equal(commitment, read);
    }

    [Fact]
    public void NoOpret_Throws()
    {
        var psbt = PsbtWithOutputs(new TxOut(Money.Coins(1), TaprootScript()));
        Assert.Throws<InvalidOperationException>(() => RgbPsbtInspector.ReadOpretCommitment(psbt));
    }

    [Fact]
    public void TwoOprets_Throws()
    {
        var psbt = PsbtWithOutputs(
            new TxOut(Money.Zero, Opret(new byte[32])),
            new TxOut(Money.Zero, Opret(new byte[32])));
        Assert.Throws<InvalidOperationException>(() => RgbPsbtInspector.ReadOpretCommitment(psbt));
    }

    [Fact]
    public void WrongLengthOpret_Throws()
    {
        var psbt = PsbtWithOutputs(new TxOut(Money.Zero, Opret(new byte[20])));
        Assert.Throws<InvalidOperationException>(() => RgbPsbtInspector.ReadOpretCommitment(psbt));
    }

    [Fact]
    public void IsTaproot_DetectsP2tr()
    {
        Assert.True(RgbPsbtInspector.IsTaproot(TaprootScript()));
        var segwit = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        Assert.False(RgbPsbtInspector.IsTaproot(segwit));
        Assert.False(RgbPsbtInspector.IsTaproot(Opret(new byte[32])));
    }
}

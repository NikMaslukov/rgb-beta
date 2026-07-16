using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RGBWalletServicePsbtSourceTests
{
    static readonly Network Net = Network.RegTest;

    static string BuildPsbtBase64(uint prevN)
    {
        var key = new Key();
        var script = key.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, prevN));
        tx.Outputs.Add(new TxOut(Money.Coins(1), script));
        return tx.CreatePSBT(Net).ToBase64();
    }

    static void AssertBothSourcesAgree(string json)
    {
        var viaExtract = RGBWalletService.ExtractPsbt(json);
        var viaDeserialize = JsonSerializer.Deserialize<SendBeginResult>(json)!.Psbt.Trim('"');
        Assert.Equal(viaDeserialize, viaExtract);

        var extractTxid = PSBT.Parse(viaExtract, Net).GetGlobalTransaction().GetHash();
        var deserializeTxid = PSBT.Parse(viaDeserialize, Net).GetGlobalTransaction().GetHash();
        Assert.Equal(deserializeTxid, extractTxid);
    }

    [Fact]
    public void PsbtSources_Agree_SingleKey()
    {
        var b64 = BuildPsbtBase64(0);
        AssertBothSourcesAgree($"{{\"psbt\":\"{b64}\",\"batch_transfer_idx\":0}}");
    }

    [Fact]
    public void PsbtSources_Agree_DuplicateKey_LastWins()
    {
        var decoy = BuildPsbtBase64(1);
        var real = BuildPsbtBase64(2);
        Assert.NotEqual(decoy, real);

        var json = $"{{\"psbt\":\"{decoy}\",\"psbt\":\"{real}\",\"batch_transfer_idx\":0}}";
        AssertBothSourcesAgree(json);
        Assert.Equal(real, JsonSerializer.Deserialize<SendBeginResult>(json)!.Psbt.Trim('"'));
    }
}

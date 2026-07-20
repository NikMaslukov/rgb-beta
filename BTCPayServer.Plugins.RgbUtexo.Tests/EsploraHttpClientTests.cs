using System;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class EsploraHttpClientTests
{
    const string TaprootSpkHex = "51207a60886d301d984f342e64419de5fd5a923c629450177d2b3d01169b0d1f0c0d";
    const string ExpectedBigEndianScriptHash = "0732507467b07e6be971976542a568436e6cda3b09c8c4349012e45f2300a91b";

    static Script Spk() => new(Convert.FromHexString(TaprootSpkHex));

    [Fact]
    public void EsploraScriptHash_IsBigEndianSha256()
    {
        Assert.Equal(ExpectedBigEndianScriptHash, EsploraHttpClient.EsploraScriptHash(Spk()));
    }

    [Fact]
    public void EsploraScriptHash_IsNotElectrumReversedForm()
    {
        var esplora = EsploraHttpClient.EsploraScriptHash(Spk());
        var electrum = ElectrumClient.ScriptHash(Spk());
        Assert.NotEqual(electrum, esplora);
        Assert.Equal(ByteReverse(electrum), esplora);
    }

    static string ByteReverse(string hex)
    {
        var b = Convert.FromHexString(hex);
        Array.Reverse(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
}

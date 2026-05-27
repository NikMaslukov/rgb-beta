using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SignerNetworkIsolationTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Theory]
    [InlineData("regtest")]
    [InlineData("testnet")]
    [InlineData("mainnet")]
    public void RestoreKeysFromMnemonic_MatchesSigner_Xpubs(string networkName)
    {
        var network = networkName switch
        {
            "regtest" => Network.RegTest,
            "testnet" => Network.TestNet,
            "mainnet" => Network.Main,
            _ => throw new ArgumentException(networkName)
        };

        using var signer = new MemoryWalletSigner(TestMnemonic, network);

        var mnemonicObj = new Mnemonic(TestMnemonic);
        var masterKey = mnemonicObj.DeriveExtKey();
        var isTestnet = network != Network.Main;
        var coinType = isTestnet ? 1 : 0;
        var vanillaXpub = masterKey.Derive(new KeyPath($"m/84'/{coinType}'/0'")).Neuter().ToString(network);
        var coloredXpub = masterKey.Derive(new KeyPath($"m/86'/{coinType}'/0'")).Neuter().ToString(network);

        Assert.Equal(signer.XpubVanilla, vanillaXpub);
        Assert.Equal(signer.XpubColored, coloredXpub);
    }

    [Fact]
    public void SameWallet_DifferentNetworks_DifferentXpubs()
    {
        using var regtest = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var mainnet = new MemoryWalletSigner(TestMnemonic, Network.Main);
        using var testnet = new MemoryWalletSigner(TestMnemonic, Network.TestNet);

        Assert.NotEqual(regtest.XpubVanilla, mainnet.XpubVanilla);
        Assert.NotEqual(regtest.XpubColored, mainnet.XpubColored);
        Assert.Equal(regtest.XpubVanilla, testnet.XpubVanilla);
    }

    [Fact]
    public void Regtest_UsesCoinType1()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        Assert.StartsWith("tpub", signer.XpubVanilla);
        Assert.StartsWith("tpub", signer.XpubColored);
    }

    [Fact]
    public void Mainnet_UsesCoinType0()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.Main);
        Assert.StartsWith("xpub", signer.XpubVanilla);
        Assert.StartsWith("xpub", signer.XpubColored);
    }

    [Fact]
    public void MasterFingerprint_ConsistentAcrossNetworks()
    {
        using var regtest = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var mainnet = new MemoryWalletSigner(TestMnemonic, Network.Main);

        Assert.Equal(regtest.MasterFingerprint, mainnet.MasterFingerprint);
    }

    [Fact]
    public void Provider_MultipleWallets_EachGetsOwnNetwork()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("regtest-wallet", TestMnemonic, Network.RegTest);
        provider.RegisterSigner("mainnet-wallet", TestMnemonic, Network.Main);

        var regtestSigner = provider.GetSignerAsync("regtest-wallet").GetAwaiter().GetResult();
        var mainnetSigner = provider.GetSignerAsync("mainnet-wallet").GetAwaiter().GetResult();

        Assert.NotNull(regtestSigner);
        Assert.NotNull(mainnetSigner);
        Assert.StartsWith("tpub", regtestSigner!.XpubVanilla);
        Assert.StartsWith("xpub", mainnetSigner!.XpubVanilla);
    }

    [Fact]
    public void Provider_ReplacingWallet_ChangesNetwork()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-1", TestMnemonic, Network.RegTest);
        var before = provider.GetSignerAsync("wallet-1").GetAwaiter().GetResult();
        Assert.StartsWith("tpub", before!.XpubVanilla);

        provider.RegisterSigner("wallet-1", TestMnemonic, Network.Main);
        var after = provider.GetSignerAsync("wallet-1").GetAwaiter().GetResult();
        Assert.StartsWith("xpub", after!.XpubVanilla);
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

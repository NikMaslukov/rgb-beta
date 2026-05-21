using BTCPayServer.Plugins.RgbUtexo.Controllers;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class NetworkMappingTests
{
    [Fact]
    public void Mainnet_ReturnsMapped()
    {
        Assert.Equal("mainnet", RGBController.MapChainNameToRgbNetwork(Network.Main.ChainName));
    }

    [Fact]
    public void Testnet_ReturnsMapped()
    {
        Assert.Equal("testnet", RGBController.MapChainNameToRgbNetwork(Network.TestNet.ChainName));
    }

    [Fact]
    public void Regtest_ReturnsMapped()
    {
        Assert.Equal("regtest", RGBController.MapChainNameToRgbNetwork(Network.RegTest.ChainName));
    }

    [Fact]
    public void Signet_ReturnsMapped()
    {
        var signetChainName = new ChainName("Signet");
        Assert.Equal("signet", RGBController.MapChainNameToRgbNetwork(signetChainName));
    }

    [Fact]
    public void UnknownChainName_Throws()
    {
        var unknown = new ChainName("FakeNet");
        Assert.Throws<InvalidOperationException>(() => RGBController.MapChainNameToRgbNetwork(unknown));
    }

    [Fact]
    public void GetForNetwork_UnknownNetwork_Throws()
    {
        Assert.Throws<ArgumentException>(() => NetworkSettings.GetForNetwork("foobar"));
    }

    [Fact]
    public void GetForNetwork_KnownNetworks_ReturnsSettings()
    {
        foreach (var net in new[] { "regtest", "testnet", "mainnet", "signet" })
            Assert.NotNull(NetworkSettings.GetForNetwork(net));
    }

    [Fact]
    public void MapNetworkFolder_UnknownNetwork_Throws()
    {
        Assert.Throws<ArgumentException>(() => RGBConfiguration.MapNetworkFolder("foobar"));
    }

    [Fact]
    public void MapNetworkFolder_Regtest_ReturnsRegTest()
    {
        Assert.Equal("RegTest", RGBConfiguration.MapNetworkFolder("regtest"));
    }
}

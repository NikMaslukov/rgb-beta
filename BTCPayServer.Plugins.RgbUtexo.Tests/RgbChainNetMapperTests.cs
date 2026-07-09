using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbChainNetMapperTests
{
    [Theory]
    [InlineData("bc")]
    [InlineData("tb3")]
    [InlineData("bcrt")]
    [InlineData("sb")]
    public void SupportedPrefixes_Map(string prefix)
    {
        Assert.True(RgbChainNetMapper.TryMapPrefix(prefix, out var net));
        Assert.NotNull(net);
    }

    [Fact]
    public void Prefixes_MapToExpectedNetworks()
    {
        RgbChainNetMapper.TryMapPrefix("bc", out var bc);
        RgbChainNetMapper.TryMapPrefix("tb3", out var tb3);
        RgbChainNetMapper.TryMapPrefix("bcrt", out var bcrt);
        Assert.Equal(Network.Main, bc);
        Assert.Equal(Network.TestNet, tb3);
        Assert.Equal(Network.RegTest, bcrt);
    }

    [Theory]
    [InlineData("tb4")]
    [InlineData("sbc")]
    [InlineData("lq")]
    [InlineData("tl")]
    [InlineData("")]
    [InlineData("BC")]
    [InlineData("regtest")]
    [InlineData("bcr")]
    public void UnsupportedPrefixes_FailClosed(string prefix)
    {
        Assert.False(RgbChainNetMapper.TryMapPrefix(prefix, out var net));
        Assert.Null(net);
    }

    [Fact]
    public void PrefixForNetwork_SupportedNetworks_Roundtrip()
    {
        Assert.Equal("bc", RgbChainNetMapper.PrefixForNetwork(Network.Main));
        Assert.Equal("tb3", RgbChainNetMapper.PrefixForNetwork(Network.TestNet));
        Assert.Equal("bcrt", RgbChainNetMapper.PrefixForNetwork(Network.RegTest));
    }
}

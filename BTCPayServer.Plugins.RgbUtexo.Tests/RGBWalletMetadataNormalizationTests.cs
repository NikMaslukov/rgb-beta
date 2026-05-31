using BTCPayServer.Plugins.RgbUtexo.Services;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RGBWalletMetadataNormalizationTests
{
    [Fact]
    public void StripsControlChars_FromTickerAndName()
    {
        var (t, n) = RGBWalletService.NormalizeAssetMetadata("USDT\x00\x07\n", "Tether\tUSD\r\n");
        Assert.Equal("USDT", t);
        Assert.Equal("TetherUSD", n);
    }

    [Fact]
    public void TruncatesTickerToThirtyTwo()
    {
        var longTicker = new string('A', 64);
        var (t, _) = RGBWalletService.NormalizeAssetMetadata(longTicker, "n");
        Assert.Equal(32, t.Length);
    }

    [Fact]
    public void TruncatesNameToSixtyFour()
    {
        var longName = new string('B', 128);
        var (_, n) = RGBWalletService.NormalizeAssetMetadata("t", longName);
        Assert.Equal(64, n.Length);
    }

    [Fact]
    public void NullInputs_BecomeEmpty()
    {
        var (t, n) = RGBWalletService.NormalizeAssetMetadata(null, null);
        Assert.Equal("", t);
        Assert.Equal("", n);
    }

    [Fact]
    public void PreservesNonControlAscii_AndUtf8()
    {
        var (t, n) = RGBWalletService.NormalizeAssetMetadata("USD-T", "Tëther (test)");
        Assert.Equal("USD-T", t);
        Assert.Equal("Tëther (test)", n);
    }

    [Fact]
    public void DoesNotSplitSurrogatePair_AtTickerBoundary()
    {
        // 31 'A' + one emoji 'U+1F600' (2 UTF-16 code units = 33 total).
        // Naive Substring(0, 32) would split the surrogate pair. Truncate must back off to 31.
        var ticker = new string('A', 31) + "😀";
        var (t, _) = RGBWalletService.NormalizeAssetMetadata(ticker, "n");
        Assert.Equal(31, t.Length);
        Assert.DoesNotContain((char)0xD83D, t);
    }
}

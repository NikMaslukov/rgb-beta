using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class FeeEstimationTests
{
    [Theory]
    [InlineData(1, 1, 1.0f)]
    [InlineData(1, 2, 1.0f)]
    [InlineData(3, 2, 2.0f)]
    [InlineData(1, 1, 10.0f)]
    public void EstimateTaprootFee_ReturnsPositive(int inputs, int outputs, float feeRate)
    {
        var fee = RGBWalletService.EstimateTaprootFee(inputs, outputs, feeRate);
        Assert.True(fee > 0, $"Fee should be positive for {inputs} inputs, {outputs} outputs, {feeRate} sat/vB");
    }

    [Fact]
    public void EstimateTaprootFee_SingleInput_SingleOutput_At1SatVb()
    {
        var fee = RGBWalletService.EstimateTaprootFee(1, 1, 1.0f);
        Assert.True(fee >= 100 && fee <= 150, $"Expected ~111 vbytes for 1-in/1-out taproot, got {fee}");
    }

    [Fact]
    public void EstimateTaprootFee_MoreInputs_HigherFee()
    {
        var fee1 = RGBWalletService.EstimateTaprootFee(1, 2, 1.0f);
        var fee3 = RGBWalletService.EstimateTaprootFee(3, 2, 1.0f);
        Assert.True(fee3 > fee1, "More inputs should produce higher fee");
    }

    [Fact]
    public void EstimateTaprootFee_MoreOutputs_HigherFee()
    {
        var fee1 = RGBWalletService.EstimateTaprootFee(1, 1, 1.0f);
        var fee3 = RGBWalletService.EstimateTaprootFee(1, 3, 1.0f);
        Assert.True(fee3 > fee1, "More outputs should produce higher fee");
    }

    [Fact]
    public void EstimateTaprootFee_HigherRate_HigherFee()
    {
        var fee1 = RGBWalletService.EstimateTaprootFee(2, 2, 1.0f);
        var fee10 = RGBWalletService.EstimateTaprootFee(2, 2, 10.0f);
        Assert.True(fee10 > fee1 * 9, "10x fee rate should produce roughly 10x fee");
        Assert.True(fee10 <= fee1 * 10 + 10, "10x fee rate should not produce more than ~10x fee");
    }

    [Fact]
    public void EstimateTaprootFee_RoundingCeils()
    {
        var fee = RGBWalletService.EstimateTaprootFee(1, 1, 1.0f);
        var vsize = 10.5 + 1 * 57.5 + 1 * 43.0;
        Assert.Equal((long)Math.Ceiling(vsize), fee);
    }

    [Theory]
    [InlineData(1, 2, 2.0f)]
    [InlineData(2, 2, 2.0f)]
    [InlineData(3, 3, 2.0f)]
    [InlineData(5, 2, 5.0f)]
    public void EstimateTaprootFee_MixedInputCounts_Reasonable(int inputs, int outputs, float feeRate)
    {
        var fee = RGBWalletService.EstimateTaprootFee(inputs, outputs, feeRate);
        var maxReasonable = (inputs * 70 + outputs * 50 + 15) * (long)Math.Ceiling((double)feeRate);
        Assert.True(fee <= maxReasonable, $"Fee {fee} exceeds reasonable upper bound {maxReasonable}");
        Assert.True(fee > 0);
    }
}

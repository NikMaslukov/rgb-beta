using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RateHandlingTests
{
    static readonly JsonSerializer _blobSerializer = BlobSerializer.CreateSerializer().Serializer;

    [Fact]
    public void AllowFallback_DefaultIsFalse()
    {
        var config = new RGBPaymentMethodConfig();
        Assert.False(config.AllowOneToOneRateFallback);
    }

    [Fact]
    public void AllowFallback_True_SurvivesBlobSerializerRoundtrip()
    {
        var config = new RGBPaymentMethodConfig
        {
            WalletId = "test-wallet",
            AllowOneToOneRateFallback = true
        };

        var token = JObject.FromObject(config, _blobSerializer);
        var deserialized = token.ToObject<RGBPaymentMethodConfig>(_blobSerializer);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.AllowOneToOneRateFallback);
        Assert.Equal("test-wallet", deserialized.WalletId);
    }

    [Fact]
    public void AllowFallback_False_OmittedFromJson_DeserializesAsFalse()
    {
        var config = new RGBPaymentMethodConfig
        {
            WalletId = "test-wallet",
            AllowOneToOneRateFallback = false
        };

        var token = JObject.FromObject(config, _blobSerializer);
        var json = token.ToString();

        Assert.DoesNotContain("allowOneToOneRateFallback", json);

        var deserialized = token.ToObject<RGBPaymentMethodConfig>(_blobSerializer);
        Assert.NotNull(deserialized);
        Assert.False(deserialized!.AllowOneToOneRateFallback);
    }

    [Fact]
    public void BareToObject_WithBlobSerializer_ReadsAllProperties()
    {
        var json = JObject.Parse(@"{
            ""walletId"": ""w1"",
            ""allowOneToOneRateFallback"": true,
            ""utxoCount"": 8
        }");

        var config = json.ToObject<RGBPaymentMethodConfig>(_blobSerializer);

        Assert.NotNull(config);
        Assert.Equal("w1", config!.WalletId);
        Assert.True(config.AllowOneToOneRateFallback);
        Assert.Equal(8, config.UtxoCount);
    }

    [Fact]
    public void BareToObject_WithoutSerializer_UsesDefaults()
    {
        var json = JObject.Parse(@"{
            ""allowOneToOneRateFallback"": true
        }");

        var withSerializer = json.ToObject<RGBPaymentMethodConfig>(_blobSerializer);
        Assert.True(withSerializer!.AllowOneToOneRateFallback);
    }
}

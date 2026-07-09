using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbTransferDataReaderTests
{
    const string SnakeCase = """
    {
      "donation": false,
      "entropy": 123,
      "transfers": {
        "rgb:CONTRACT": {
          "recipients": [
            {
              "recipient_id": "r1",
              "transport_endpoints": [
                { "transport_type": "JsonRpc", "endpoint": "rpc://proxy.example/0.2/json-rpc", "used": false, "usable": true }
              ]
            }
          ]
        }
      }
    }
    """;

    const string CamelCase = """
    {
      "transfers": {
        "rgb:CONTRACT": {
          "recipients": [
            {
              "recipientId": "r1",
              "transportEndpoints": [
                { "transportType": "JsonRpc", "endpoint": "rpc://a.example", "used": false },
                { "transportType": "JsonRpc", "endpoint": "rpc://b.example", "used": false }
              ]
            }
          ]
        }
      }
    }
    """;

    [Fact]
    public void SnakeCase_ExtractsEndpoint()
    {
        var endpoints = RgbTransferDataReader.ReadTransportEndpointsFromJson(SnakeCase);
        Assert.Equal(new[] { "rpc://proxy.example/0.2/json-rpc" }, endpoints);
    }

    [Fact]
    public void CamelCase_ExtractsAllEndpoints()
    {
        var endpoints = RgbTransferDataReader.ReadTransportEndpointsFromJson(CamelCase);
        Assert.Equal(new[] { "rpc://a.example", "rpc://b.example" }, endpoints);
    }

    [Fact]
    public void NoTransfers_ReturnsEmpty()
    {
        Assert.Empty(RgbTransferDataReader.ReadTransportEndpointsFromJson("{\"donation\":false}"));
    }
}

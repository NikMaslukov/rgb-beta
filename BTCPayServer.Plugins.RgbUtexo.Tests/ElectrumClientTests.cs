using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ElectrumClientTests
{
    [Fact]
    public async Task TcpScheme_InsecureFalse_Throws()
    {
        using var client = new ElectrumClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("tcp://electrum.example.com:50001", allowInsecure: false));
        Assert.Contains("Unencrypted", ex.Message);
        Assert.Contains("not allowed outside regtest", ex.Message);
    }

    [Fact]
    public async Task TcpScheme_InsecureDefault_Throws()
    {
        using var client = new ElectrumClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("tcp://localhost:50001"));
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task TcpScheme_CaseInsensitive_Throws()
    {
        using var client = new ElectrumClient();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("TCP://electrum.example.com:50001", allowInsecure: false));
    }

    [Fact]
    public async Task TcpScheme_InsecureTrue_DoesNotThrowSchemeError()
    {
        using var client = new ElectrumClient();
        try
        {
            await client.ConnectAsync("tcp://192.0.2.1:50001", allowInsecure: true);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
        }
    }

    [Fact]
    public async Task SchemeLessUrl_Throws()
    {
        using var client = new ElectrumClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("electrum.example.com:50001", allowInsecure: false));
        Assert.True(ex.Message.Contains("Malformed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownScheme_Throws()
    {
        using var client = new ElectrumClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("http://electrum.example.com:50001", allowInsecure: false));
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task EmptyUrl_Throws()
    {
        using var client = new ElectrumClient();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("", allowInsecure: false));
    }

    [Fact]
    public async Task MissingPort_Throws()
    {
        using var client = new ElectrumClient();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync("ssl://electrum.example.com", allowInsecure: false));
    }

    [Fact]
    public async Task SslUppercase_AcceptsButFailsConnect()
    {
        using var client = new ElectrumClient();
        try { await client.ConnectAsync("SSL://192.0.2.1:50002", allowInsecure: false); }
        catch (Exception ex) when (ex is not InvalidOperationException || !ex.Message.Contains("scheme")) { }
    }

    [Fact]
    public void EnsureValidTxid_Accepts64Hex_LowerAndUpper()
    {
        ElectrumClient.EnsureValidTxid(new string('a', 64));
        ElectrumClient.EnsureValidTxid(new string('A', 64));
        ElectrumClient.EnsureValidTxid("7b902a2d1578e8e50f5db1519ddf7170d4ba07c61e6e5ff704b6f284f8a2d289");
    }

    [Fact]
    public void EnsureValidTxid_Rejects_MalformedShapes()
    {
        Assert.Throws<InvalidOperationException>(() => ElectrumClient.EnsureValidTxid(""));
        Assert.Throws<InvalidOperationException>(() => ElectrumClient.EnsureValidTxid(new string('a', 63)));
        Assert.Throws<InvalidOperationException>(() => ElectrumClient.EnsureValidTxid(new string('a', 65)));
        Assert.Throws<InvalidOperationException>(() => ElectrumClient.EnsureValidTxid(new string('a', 63) + "g"));
        Assert.Throws<InvalidOperationException>(() => ElectrumClient.EnsureValidTxid(new string('a', 63) + " "));
        Assert.Throws<InvalidOperationException>(() => ElectrumClient.EnsureValidTxid(" " + new string('a', 63)));
    }
}

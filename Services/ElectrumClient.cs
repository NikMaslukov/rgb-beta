using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class ElectrumClient : IDisposable
{
    TcpClient? _tcp;
    Stream? _stream;
    int _requestId;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    const int MaxResponseBytes = 10 * 1024 * 1024;

    public async Task ConnectAsync(string electrumUrl, CancellationToken ct = default, bool allowInsecure = false)
    {
        Uri uri;
        try { uri = new Uri(electrumUrl); }
        catch (UriFormatException)
        {
            throw new InvalidOperationException(
                $"Malformed Electrum URL '{electrumUrl}'. Expected ssl://host:port or tcp://host:port.");
        }

        bool useSsl;
        if (uri.Scheme.Equals("ssl", StringComparison.OrdinalIgnoreCase))
            useSsl = true;
        else if (uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowInsecure)
                throw new InvalidOperationException(
                    "Unencrypted Electrum connections are not allowed outside regtest. Use ssl:// endpoint.");
            useSsl = false;
        }
        else
            throw new InvalidOperationException(
                $"Electrum URL scheme '{uri.Scheme}' is not allowed. Use ssl:// or (regtest only) tcp://.");

        if (uri.Port < 1 || uri.Port > 65535)
            throw new InvalidOperationException(
                $"Electrum URL '{electrumUrl}' is missing a valid port.");

        var host = uri.Host.Trim('[', ']');
        var port = uri.Port;

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);

        if (useSsl)
        {
            var ssl = new SslStream(_tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(host);
            _stream = ssl;
        }
        else
        {
            _stream = _tcp.GetStream();
        }

        await RequestAsync("server.version", ["btcpay-rgb", "1.4"], ct);
    }

    async Task<JsonElement> RequestAsync(string method, object[] parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        var bytes = Encoding.UTF8.GetBytes(request + "\n");
        await _stream!.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ReadTimeout);

        var buffer = new StringBuilder();
        var readBuf = new byte[4096];
        while (true)
        {
            var read = await _stream.ReadAsync(readBuf, timeoutCts.Token);
            if (read == 0) throw new InvalidOperationException("Electrum: connection closed");
            buffer.Append(Encoding.UTF8.GetString(readBuf, 0, read));
            if (buffer.Length > MaxResponseBytes)
                throw new InvalidOperationException("Electrum response exceeds maximum allowed size");
            var str = buffer.ToString();
            var nlIdx = str.IndexOf('\n');
            if (nlIdx >= 0)
            {
                var line = str[..nlIdx];
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException($"Electrum error: {error}");
                return doc.RootElement.GetProperty("result").Clone();
            }
        }
    }

    public async Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default)
    {
        var result = await RequestAsync("blockchain.transaction.get", [txid], ct);
        return result.GetString()!;
    }

    public async Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default)
    {
        var result = await RequestAsync("blockchain.transaction.broadcast", [rawTxHex], ct);
        return result.GetString()!;
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { /* best-effort */ }
        try { _tcp?.Dispose(); } catch { /* best-effort */ }
    }
}

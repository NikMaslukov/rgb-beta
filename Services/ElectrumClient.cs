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

    public async Task ConnectAsync(string electrumUrl, CancellationToken ct = default)
    {
        var useSsl = electrumUrl.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase);
        var normalized = electrumUrl
            .Replace("tcp://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("ssl://", "", StringComparison.OrdinalIgnoreCase);

        var parts = normalized.Split(':');
        var host = parts[0];
        var port = int.Parse(parts[1]);

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);

        if (useSsl)
        {
            // TODO: enforce certificate validation on non-regtest networks
            var ssl = new SslStream(_tcp.GetStream(), false, (_, _, _, _) => true);
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

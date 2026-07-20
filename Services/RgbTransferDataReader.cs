using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbTransferDataReader
{
    const string TransferDataFile = "transfer_data.txt";
    const long MaxTransferDataBytes = 5 * 1024 * 1024;

    public static IReadOnlyList<string> ReadTransportEndpoints(string fasciaPath)
    {
        var transferDir = Path.GetDirectoryName(fasciaPath)
            ?? throw new InvalidOperationException("cannot resolve transfer directory from fascia path");
        var path = Path.Combine(transferDir, TransferDataFile);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxTransferDataBytes)
            throw new InvalidOperationException($"transfer_data.txt exceeds {MaxTransferDataBytes} bytes");
        return ReadTransportEndpointsFromJson(File.ReadAllText(path));
    }

    public static IReadOnlyList<string> ReadTransportEndpointsFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var endpoints = new List<string>();

        if (!TryGetProp(doc.RootElement, "transfers", out var transfers) || transfers.ValueKind != JsonValueKind.Object)
            return endpoints;

        foreach (var assetTransfer in transfers.EnumerateObject())
        {
            if (!TryGetProp(assetTransfer.Value, "recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var recipient in recipients.EnumerateArray())
            {
                if (!TryGetProp(recipient, "transport_endpoints", out var transportEndpoints) || transportEndpoints.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var transportEndpoint in transportEndpoints.EnumerateArray())
                    if (TryGetProp(transportEndpoint, "endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.String)
                        endpoints.Add(endpoint.GetString()!);
            }
        }

        return endpoints;
    }

    static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        var target = Normalize(name);
        foreach (var property in obj.EnumerateObject())
            if (Normalize(property.Name) == target)
            {
                value = property.Value;
                return true;
            }
        return false;
    }

    static string Normalize(string s) => s.Replace("_", "").ToLowerInvariant();
}

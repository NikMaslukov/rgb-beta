namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class BitcoinChainClientFactory
{
    public static IBitcoinChainClient Create(string url, bool allowInsecure = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Bitcoin chain client URL is empty");

        Uri uri;
        try { uri = new Uri(url); }
        catch (UriFormatException) { throw new InvalidOperationException($"Malformed chain client URL '{url}'"); }

        var scheme = uri.Scheme.ToLowerInvariant();
        switch (scheme)
        {
            case "https":
                return new EsploraHttpClient(url);
            case "http":
                if (!allowInsecure)
                    throw new InvalidOperationException(
                        "Unencrypted http:// Esplora connections are not allowed outside regtest. Use https:// endpoint.");
                return new EsploraHttpClient(url);
            case "ssl":
                return new ElectrumClient(url, allowInsecure);
            case "tcp":
                return new ElectrumClient(url, allowInsecure);
            default:
                throw new InvalidOperationException(
                    $"Unsupported chain client URL scheme '{uri.Scheme}'. Use https:// (Esplora) or ssl:// (Electrum); http:// or tcp:// require allowInsecure (regtest only).");
        }
    }
}

using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbChainNetMapper
{
    static readonly Network? Signet = Network.GetNetwork("signet");

    public static bool TryMapPrefix(string prefix, out Network? network)
    {
        switch (prefix)
        {
            case "bc":
                network = Network.Main;
                return true;
            case "tb3":
                network = Network.TestNet;
                return true;
            case "sb":
                network = Signet;
                return Signet != null;
            case "bcrt":
                network = Network.RegTest;
                return true;
            default:
                network = null;
                return false;
        }
    }

    public static string PrefixForNetwork(Network network)
    {
        if (network == Network.Main) return "bc";
        if (network == Network.TestNet) return "tb3";
        if (network == Network.RegTest) return "bcrt";
        if (Signet != null && network == Signet) return "sb";
        throw new InvalidOperationException($"no RGB chain-net prefix for network {network}");
    }
}

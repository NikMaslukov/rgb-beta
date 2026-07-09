using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbSighashGuard
{
    public static void EnsureAllInputsAllowed(PSBT psbt)
    {
        for (int i = 0; i < psbt.Inputs.Count; i++)
        {
            var input = psbt.Inputs[i];

            var taproot = input.TaprootSighashType;
            if (taproot.HasValue && taproot.Value != TaprootSigHash.Default && taproot.Value != TaprootSigHash.All)
                throw new InvalidOperationException(
                    $"PSBT input #{i} uses disallowed taproot sighash {taproot.Value}; only Default and All are permitted");

            var legacy = input.SighashType;
            if (legacy.HasValue && legacy.Value != SigHash.All)
                throw new InvalidOperationException(
                    $"PSBT input #{i} uses disallowed sighash {legacy.Value}; only All is permitted");
        }
    }
}

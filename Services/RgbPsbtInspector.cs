using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbPsbtInspector
{
    const byte OpReturn = 0x6a;
    const byte Push32 = 0x20;
    const byte SegwitV1 = 0x51;
    const int WitnessScriptLength = 34;

    public static byte[] ReadOpretCommitment(PSBT psbt)
    {
        var tx = psbt.GetGlobalTransaction();

        byte[]? commitment = null;
        int opReturnCount = 0;

        foreach (var output in tx.Outputs)
        {
            var bytes = output.ScriptPubKey.ToBytes();
            if (bytes.Length == 0 || bytes[0] != OpReturn) continue;

            opReturnCount++;
            if (bytes.Length != WitnessScriptLength || bytes[1] != Push32)
                throw new InvalidOperationException(
                    $"OP_RETURN output is not a 34-byte 32-push opret commitment (script length {bytes.Length})");
            commitment = bytes[2..];
        }

        if (opReturnCount != 1 || commitment == null)
            throw new InvalidOperationException(
                $"expected exactly one OP_RETURN opret commitment in the send PSBT, found {opReturnCount}");

        return commitment;
    }

    public static bool IsTaproot(Script script)
    {
        var bytes = script.ToBytes();
        return bytes.Length == WitnessScriptLength && bytes[0] == SegwitV1 && bytes[1] == Push32;
    }
}

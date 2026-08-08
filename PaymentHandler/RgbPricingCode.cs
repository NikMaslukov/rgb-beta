using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public static class RgbPricingCode
{
    const int HexChars = 16;

    public static string For(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ArgumentException("Asset id is required to derive a pricing code", nameof(assetId));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(assetId));
        return string.Create(3 + HexChars, digest, static (span, bytes) =>
        {
            span[0] = 'R'; span[1] = 'G'; span[2] = 'B';
            for (var i = 0; i < HexChars / 2; i++)
            {
                var b = bytes[i];
                span[3 + i * 2] = HexDigit(b >> 4);
                span[4 + i * 2] = HexDigit(b & 0xF);
            }
        });
    }

    public static bool IsPricingCode(string? value)
    {
        if (value is null || value.Length != 3 + HexChars) return false;
        if (!(value[0] is 'R' or 'r') || !(value[1] is 'G' or 'g') || !(value[2] is 'B' or 'b')) return false;
        for (var i = 3; i < value.Length; i++)
        {
            var c = char.ToUpperInvariant(value[i]);
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
}

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbVerifyNative
{
    const string Library = "rgbverifycffi";

    static RgbVerifyNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(RgbVerifyNative).Assembly, ResolveNative);
    }

    static IntPtr ResolveNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library) return IntPtr.Zero;

        var baseDir = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;

        var fileName = OperatingSystem.IsWindows() ? "rgbverifycffi.dll"
            : OperatingSystem.IsMacOS() ? "librgbverifycffi.dylib"
            : "librgbverifycffi.so";

        foreach (var rid in RuntimeIdentifiers())
        {
            var candidate = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        var flat = Path.Combine(baseDir, fileName);
        if (File.Exists(flat) && NativeLibrary.TryLoad(flat, out var flatHandle))
            return flatHandle;

        return IntPtr.Zero;
    }

    static IEnumerable<string> RuntimeIdentifiers()
    {
        yield return RuntimeInformation.RuntimeIdentifier;
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        yield return $"{os}-{arch}";
    }

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_decode_invoice(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string invoice);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_validate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string consignmentPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string unsignedTxid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string indexerUrl,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string network,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stockDir);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_commitment_check(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fasciaPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string unsignedTxid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string opretCommitmentBytes,
        ulong entropy);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void rgbverify_string_free(IntPtr ptr);

    public static RgbDecodeInvoiceResult DecodeInvoice(string invoice)
        => Deserialize<RgbDecodeInvoiceResult>(Read(rgbverify_decode_invoice(invoice)), "decode_invoice");

    public static RgbValidateResult Validate(string consignmentPath, string unsignedTxid, string indexerUrl, string network, string stockDir)
        => Deserialize<RgbValidateResult>(Read(rgbverify_validate(consignmentPath, unsignedTxid, indexerUrl, network, stockDir)), "validate");

    public static RgbCommitmentCheckResult CommitmentCheck(string fasciaPath, string unsignedTxid, string opretCommitmentBytes, ulong entropy)
        => Deserialize<RgbCommitmentCheckResult>(Read(rgbverify_commitment_check(fasciaPath, unsignedTxid, opretCommitmentBytes, entropy)), "commitment_check");

    static string Read(CResultString result)
    {
        try
        {
            var payload = result.inner != IntPtr.Zero ? Marshal.PtrToStringUTF8(result.inner) : null;
            if (result.result != CResultValue.Ok)
                throw new RgbIntentVerificationException($"rgb-verify native call failed: {payload ?? "no detail"}");
            if (payload == null)
                throw new RgbIntentVerificationException("rgb-verify returned a null payload");
            return payload;
        }
        finally
        {
            if (result.inner != IntPtr.Zero)
                rgbverify_string_free(result.inner);
        }
    }

    static T Deserialize<T>(string json, string call)
        => JsonSerializer.Deserialize<T>(json)
           ?? throw new RgbIntentVerificationException($"rgb-verify {call} returned unparseable JSON");
}

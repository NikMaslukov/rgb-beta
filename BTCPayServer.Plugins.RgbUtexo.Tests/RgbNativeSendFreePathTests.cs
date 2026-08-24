using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RgbLib;
using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSendFreePathTests
{
    const string HelperFile = "RgbRestoreHelper/RgbNativeSend.cs";
    const string NativeMethodsTypeName = "RgbLib.NativeMethods";
    const string CResultStringTypeName = "RgbLib.CResultString";
    const string OfflineNativeCallThatAllocatesAStringNatively = "rgblib_get_address";
    const string RegtestAddressPrefix = "bcrt1";
    const int RepeatedAllocateAndFreeCycles = 64;

    const string WhyTheFreePathIsOnTheSuccessPath =
        "RgbNativeSend.ReadResult frees the rgb-lib payload in a finally block, so the free runs on the "
        + "SUCCESS path as well as the error path and an unreachable free replaces the returned text with "
        + "an exception. ReadResult is the only result reader for rgblib_send_begin and rgblib_send_end, "
        + "so a broken free there fails every RGB asset send rather than only failing sends.";

    [Fact]
    public void ReadResultReturnsTheNativelyAllocatedTextAndFreesItWithoutThrowing()
    {
        using var wallet = OfflineRegtestWallet.Create();
        var result = wallet.CallReturningCResultString(OfflineNativeCallThatAllocatesAStringNatively);
        Assert.Equal("Ok", OfflineRegtestWallet.StatusOf(result));
        Assert.NotEqual(IntPtr.Zero, OfflineRegtestWallet.PayloadPointerOf(result));

        string? text = null;
        var escaped = Record.Exception(() =>
            text = RgbNativeSend.ReadResult(result, OfflineNativeCallThatAllocatesAStringNatively));

        Assert.True(escaped is null,
            $"ReadResult threw {escaped?.GetType().Name} while reading a successful native result. "
            + WhyTheFreePathIsOnTheSuccessPath);
        Assert.True(text is not null && text.StartsWith(RegtestAddressPrefix, StringComparison.Ordinal),
            $"ReadResult must return the text rgb-lib allocated for "
            + $"{OfflineNativeCallThatAllocatesAStringNatively}; it returned "
            + $"{(text is null ? "null" : $"{text.Length} character(s) not starting with '{RegtestAddressPrefix}'")}. "
            + WhyTheFreePathIsOnTheSuccessPath);
        Assert.Equal(IntPtr.Zero, OfflineRegtestWallet.PayloadPointerOf(result));
    }

    [Fact]
    public void ReadResultSurvivesRepeatedRealAllocationAndFreeCycles()
    {
        using var wallet = OfflineRegtestWallet.Create();
        for (var cycle = 0; cycle < RepeatedAllocateAndFreeCycles; cycle++)
        {
            var result = wallet.CallReturningCResultString(OfflineNativeCallThatAllocatesAStringNatively);
            var text = RgbNativeSend.ReadResult(result, OfflineNativeCallThatAllocatesAStringNatively);
            Assert.True(text.StartsWith(RegtestAddressPrefix, StringComparison.Ordinal),
                $"cycle {cycle} read '{text}'. Each cycle allocates a string inside rgb-lib and hands the "
                + "pointer to the helper's free path, so a free that is not rgb-lib's own allocator "
                + "corrupts the native heap instead of failing an assertion. "
                + WhyTheFreePathIsOnTheSuccessPath);
        }
    }

    [Fact]
    public void EveryRgbLibMemberTheHelperNamesByStringResolvesInThePinnedAssembly()
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var nativeMethods = assembly.GetType(NativeMethodsTypeName);
        Assert.True(nativeMethods is not null,
            $"{NativeMethodsTypeName} is absent from the shipped RgbLib assembly "
            + $"({assembly.GetName().Version}); the helper resolves every native call through it");
        var resultType = assembly.GetType(CResultStringTypeName);
        Assert.True(resultType is not null,
            $"{CResultStringTypeName} is absent from the shipped RgbLib assembly");

        var root = HelperRoot();
        var literals = root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.StringLiteralToken))
            .Where(t => t.Parent?.Ancestors().OfType<AttributeSyntax>().Any() != true)
            .Select(t => (string?)t.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var nativeNames = literals
            .Where(v => v.StartsWith("rgblib_", StringComparison.Ordinal)
                        || v.StartsWith("free_", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(nativeNames);
        foreach (var name in nativeNames)
            Assert.True(nativeMethods!.GetMethod(name) is not null,
                $"{HelperFile} names the native call '{name}' as a string, but "
                + $"{NativeMethodsTypeName} in the pinned RgbLib assembly has no such method, so the "
                + "reflective lookup that consumes it returns null at run time. The compiler cannot see "
                + "this and the helper only executes inside a spawned child process. Literals inside "
                + "attributes are exempt because a DllImport EntryPoint names a native export rather than "
                + $"a member of {NativeMethodsTypeName}; those are checked by "
                + $"{nameof(EveryDllImportTheHelperDeclaresResolvesToARealExportOfTheShippedNativeLibrary)}. "
                + WhyTheFreePathIsOnTheSuccessPath);

        foreach (var name in literals.Where(v => v.StartsWith("RgbLib.", StringComparison.Ordinal)))
            Assert.True(assembly.GetType(name) is not null,
                $"{HelperFile} names the type '{name}' as a string and it is absent from the pinned "
                + "RgbLib assembly");

        var fieldNames = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodNameOf(i) == "GetField" && i.ArgumentList.Arguments.Count >= 1)
            .Select(i => i.ArgumentList.Arguments[0].Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(l => (string?)l.Token.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(fieldNames);
        foreach (var name in fieldNames)
        {
            var onWallet = typeof(RgbLibWallet).GetField(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var onResult = resultType!.GetField(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.True(onWallet is not null || onResult is not null,
                $"{HelperFile} reads the field '{name}' reflectively and it exists on neither "
                + $"RgbLibWallet nor {CResultStringTypeName} in the pinned RgbLib assembly");
        }
    }

    [Fact]
    public void EveryDllImportTheHelperDeclaresResolvesToARealExportOfTheShippedNativeLibrary()
    {
        var declarations = HelperRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select(m => (Method: m, Import: m.AttributeLists.SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString() is "DllImport" or "DllImportAttribute")))
            .Where(x => x.Import is not null)
            .ToList();
        Assert.NotEmpty(declarations);

        foreach (var (method, import) in declarations)
        {
            var arguments = import!.ArgumentList?.Arguments ?? default;
            var library = arguments
                .Where(a => a.NameEquals is null)
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Select(l => (string?)l.Token.Value)
                .FirstOrDefault();
            Assert.True(!string.IsNullOrEmpty(library),
                $"{HelperFile} declares {method.Identifier.ValueText} with a DllImport whose library name "
                + "is not a string literal, so this pin cannot check that its entry point exists");

            var entryPoint = arguments
                .Where(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint")
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Select(l => (string?)l.Token.Value)
                .FirstOrDefault() ?? method.Identifier.ValueText;

            var handle = NativeLibrary.Load(library!, typeof(RgbNativeSend).Assembly, null);
            Assert.True(NativeLibrary.TryGetExport(handle, entryPoint, out _),
                $"{HelperFile} declares an extern for '{entryPoint}' in '{library}', but the native "
                + $"library shipped for {RuntimeInformation.RuntimeIdentifier} exports no such symbol, so "
                + $"the first call to {method.Identifier.ValueText} throws EntryPointNotFoundException. "
                + WhyTheFreePathIsOnTheSuccessPath);
        }
    }

    static string MethodNameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static SyntaxNode HelperRoot()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, HelperFile);
        Assert.True(File.Exists(path), $"{HelperFile} is missing; it holds the live RGB send call site");
        return CSharpSyntaxTree
            .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)
            .GetRoot();
    }

    sealed class OfflineRegtestWallet : IDisposable
    {
        const string BitcoinNetwork = "Regtest";
        const int MaxAllocationsPerUtxo = 5;

        readonly string _dataDir;
        readonly RgbLibWallet _wallet;
        readonly Type _nativeMethods;
        readonly Type _resultType;
        readonly FieldInfo _walletField;

        OfflineRegtestWallet(string dataDir, RgbLibWallet wallet, Type nativeMethods, Type resultType,
            FieldInfo walletField)
        {
            _dataDir = dataDir;
            _wallet = wallet;
            _nativeMethods = nativeMethods;
            _resultType = resultType;
            _walletField = walletField;
        }

        internal static OfflineRegtestWallet Create()
        {
            var assembly = typeof(RgbLibWallet).Assembly;
            var nativeMethods = assembly.GetType(NativeMethodsTypeName)
                ?? throw new InvalidOperationException(NativeMethodsTypeName);
            var resultType = assembly.GetType(CResultStringTypeName)
                ?? throw new InvalidOperationException(CResultStringTypeName);
            var walletField = typeof(RgbLibWallet).GetField("_wallet",
                                  BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? throw new InvalidOperationException("RgbLibWallet._wallet");

            using var keys = JsonDocument.Parse(RgbLibWallet.GenerateKeys(BitcoinNetwork));
            var dataDir = Path.Combine(Path.GetTempPath(), $"rgb-free-path-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDir);

            var walletConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["data_dir"] = dataDir,
                ["bitcoin_network"] = BitcoinNetwork,
                ["database_type"] = "Sqlite",
                ["max_allocations_per_utxo"] = MaxAllocationsPerUtxo,
                ["supported_schemas"] = new[] { "Nia", "Cfa" }
            });
            var keysConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["account_xpub_vanilla"] = keys.RootElement.GetProperty("account_xpub_vanilla").GetString(),
                ["account_xpub_colored"] = keys.RootElement.GetProperty("account_xpub_colored").GetString(),
                ["master_fingerprint"] = keys.RootElement.GetProperty("master_fingerprint").GetString(),
                ["vanilla_keychain"] = (int?)null,
                ["mnemonic"] = (string?)null
            });

            return new OfflineRegtestWallet(dataDir, new RgbLibWallet(walletConfig, keysConfig),
                nativeMethods, resultType, walletField);
        }

        internal object CallReturningCResultString(string nativeMethod)
        {
            var method = _nativeMethods.GetMethod(nativeMethod)
                ?? throw new MissingMethodException(NativeMethodsTypeName, nativeMethod);
            object?[] args = [_walletField.GetValue(_wallet)];
            var result = method.Invoke(null, args)
                ?? throw new InvalidOperationException($"{nativeMethod} returned null");
            _walletField.SetValue(_wallet, args[0]);
            Assert.Equal(_resultType, result.GetType());
            return result;
        }

        internal static string? StatusOf(object result) =>
            result.GetType().GetField("result")?.GetValue(result)?.ToString();

        internal static IntPtr PayloadPointerOf(object result) =>
            (IntPtr)(result.GetType().GetField("inner")?.GetValue(result) ?? IntPtr.Zero);

        public void Dispose()
        {
            _wallet.Dispose();
            try
            {
                Directory.Delete(_dataDir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// These pins guard the vanilla-keychain input policy against edits that leave it compiling and its
// behavioural tests green. Two of them exist because a specific defect was found during review and
// would otherwise be silently reintroducible.
public class RgbVanillaInputGuardSourcePinTests
{
    const string SignerFile = "Services/MemoryWalletSigner.cs";
    const string ServiceFile = "Services/RGBWalletService.cs";
    const string SignerType = "MemoryWalletSigner";
    const string Guard = "EnsureInputsOnRgbVanillaAccount";
    const string Flag = "RequireRgbVanillaKeychainInputs";

    // (a) The flag belongs on exactly the two paths that sign a PSBT they did not build, and must NOT
    // reach asset-send, whose purpose is spending colored inputs.
    //
    // Bound to the ENCLOSING METHOD, not counted. Counting placements passes a swap — moving the flag
    // off Create-UTXOs and onto asset-send keeps the total at two — and that swap both reopens the
    // input gap on the rgb-lib-supplied PSBT and makes every RGB send refuse its own colored inputs,
    // with the whole suite still green. Review caught the counting version doing exactly that.
    [Fact]
    public void Flag_IsSetOnExactlyTheTwoIntendedSigningPolicies()
    {
        var expected = new Dictionary<string, bool>
        {
            ["CreateColorableUtxosInternalAsync"] = true,
            ["SendBtcInternalAsync"] = true,
            ["SendAssetInternalAsync"] = false
        };

        var tree = PluginCompilation.Shared.Tree(ServiceFile);
        var initializers = Enumerable.OfType<ObjectCreationExpressionSyntax>(tree.GetRoot().DescendantNodes())
            .Where(o => o.Type.ToString() == "SigningPolicy")
            .ToList();
        Assert.Equal(expected.Count, initializers.Count);

        var seen = new Dictionary<string, bool>();
        foreach (var init in initializers)
        {
            var method = init.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            Assert.NotNull(method);
            var name = method!.Identifier.ValueText;
            Assert.True(expected.ContainsKey(name),
                $"a SigningPolicy is constructed in unexpected method '{name}' — decide whether it needs the guard");
            Assert.False(seen.ContainsKey(name), $"more than one SigningPolicy in '{name}'");

            seen[name] = init.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == Flag && a.Right.ToString() == "true") == true;
        }

        Assert.Equal(expected.Count, seen.Count);
        foreach (var (method, mustHaveFlag) in expected)
            Assert.True(seen[method] == mustHaveFlag,
                mustHaveFlag
                    ? $"{method} must set {Flag} = true: it signs a PSBT it did not build"
                    : $"{method} must NOT set {Flag}: spending colored inputs is its purpose");
    }

    // (b) The guard must run after PopulateInputKeyPaths, which supplies the key paths it verifies, and
    // before ValidateOutputs so an input-side refusal wins. Ordering is not observable behaviourally.
    [Fact]
    public void Guard_RunsBetweenPopulateAndValidateOutputs()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var sign = RoslynPins.Method(tree, SignerType, "SignPsbtAsync");
        var body = RoslynPins.BodyOf(sign);

        int PositionOf(string name) => body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith(name, StringComparison.Ordinal))
            .Select(i => (int?)i.SpanStart)
            .FirstOrDefault() ?? -1;

        var populate = PositionOf("PopulateInputKeyPaths");
        var guard = PositionOf(Guard);
        var validate = PositionOf("ValidateOutputs");

        Assert.True(populate >= 0 && guard >= 0 && validate >= 0,
            $"missing call: populate={populate} guard={guard} validate={validate}");
        Assert.True(populate < guard, "the guard must run after PopulateInputKeyPaths");
        Assert.True(guard < validate, "the guard must run before ValidateOutputs");
    }

    // (c) The guard must not consult IsOwnScript's positive cache. That cache is keyed on the script
    // alone and is populated by matches against EVERY account, so reading it would answer "owned" for a
    // colored script and invert the very invariant this guard enforces.
    [Fact]
    public void Guard_DoesNotTouchTheOwnScriptCache()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, Guard));
        Assert.DoesNotContain("_verifiedScripts", body.ToString());
        Assert.DoesNotContain("IsOwnScript", body.ToString());
    }

    // (d1) The fee ceiling must resolve every input through GetTxOut(). Reading WitnessUtxo directly is
    // what let a producer understate the input value while the signature committed to the real amount,
    // so the ceiling passed and the difference was paid to miners.
    [Fact]
    public void FeeCeiling_ResolvesOnlyThroughGetTxOut()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, "ValidateOutputs")).ToString();
        Assert.DoesNotContain(".WitnessUtxo", body);
        Assert.DoesNotContain(".NonWitnessUtxo", body);
        Assert.Contains("GetTxOut()", body);
    }

    // (d2) Inside the guard the only permitted direct reads of the two utxo fields are the pair that
    // detects a disagreeing utxo pair — no accessor exposes both candidate txouts, so that comparison
    // cannot be written any other way. Every value used for a DECISION must come from GetTxOut().
    [Fact]
    public void Guard_ReadsUtxoFieldsOnlyToDetectDisagreement()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, Guard));

        var reads = body.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Name.Identifier.ValueText is "WitnessUtxo" or "NonWitnessUtxo")
            .ToList();

        // Every such read must sit inside the if-block whose condition tests both fields for null.
        var disagreementBlock = body.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .SingleOrDefault(s => s.Condition.ToString().Contains("WitnessUtxo != null")
                               && s.Condition.ToString().Contains("NonWitnessUtxo != null"));
        Assert.NotNull(disagreementBlock);

        foreach (var read in reads)
            Assert.True(disagreementBlock!.Span.Contains(read.Span),
                $"utxo field read outside the disagreement check at offset {read.SpanStart}: {read}");

        Assert.Contains("GetTxOut()", body.ToString());
    }

    // (d3) PopulateInputKeyPaths legitimately reads WitnessUtxo and is unchanged by this work; the pins
    // above are scoped per member rather than file-wide precisely so it stays exempt. This asserts the
    // exemption is still needed, so a future file-wide tightening cannot quietly assume otherwise.
    [Fact]
    public void PopulateInputKeyPaths_StillReadsWitnessUtxoDirectly()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, "PopulateInputKeyPaths")).ToString();
        Assert.Contains("WitnessUtxo", body);
    }
}

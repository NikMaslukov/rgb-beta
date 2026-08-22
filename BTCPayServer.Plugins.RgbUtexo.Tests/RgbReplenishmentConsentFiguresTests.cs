using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbReplenishmentConsentFiguresTests
{
    const string ControllerFile = "Controllers/RGBController.cs";
    const string ControllerType = "RGBController";
    const string Populate = "PopulateSettingsViewModel";
    const string GrantAction = "SetAutomaticReplenishmentAuthorization";
    const string PersistedGate = "HasPersistedReplenishmentFigures";

    static readonly string[] ConsentFigureMembers =
    [
        "PersistedUtxoCount",
        "PersistedUtxoSize",
        "WorstCaseReplenishFeeBaseSats",
        "WorstCaseReplenishFeePerVanillaUtxoSats"
    ];

    static RGBPaymentMethodConfig Config(int utxoCount, int utxoSize, int minConfirmations = 1) =>
        new() { UtxoCount = utxoCount, UtxoSize = utxoSize, MinConfirmations = minConfirmations };

    [Fact]
    public void NoPersistedConfig_HasNoStatableFigures()
        => Assert.False(RGBController.ArePersistedReplenishmentFiguresValid(null));

    [Theory]
    [InlineData(RgbConfigBounds.UtxoCountMin, RgbConfigBounds.UtxoSizeMin)]
    [InlineData(RgbConfigBounds.UtxoCountMax, RgbConfigBounds.UtxoSizeMax)]
    [InlineData(4, 1000)]
    public void InRangePersistedConfig_HasStatableFigures(int utxoCount, int utxoSize)
        => Assert.True(RGBController.ArePersistedReplenishmentFiguresValid(Config(utxoCount, utxoSize)));

    [Theory]
    [InlineData(0, 100_000)]
    [InlineData(-3, 100_000)]
    [InlineData(RgbConfigBounds.UtxoCountMax + 1, 100_000)]
    [InlineData(20, 0)]
    [InlineData(20, RgbConfigBounds.UtxoSizeMax + 1)]
    public void OutOfRangePersistedConfig_HasNoStatableFigures(int utxoCount, int utxoSize)
        => Assert.False(RGBController.ArePersistedReplenishmentFiguresValid(Config(utxoCount, utxoSize)));

    [Theory]
    [InlineData(20, 100_000, 0)]
    [InlineData(20, 100_000, RgbConfigBounds.MinConfirmationsMax + 1)]
    public void OutOfRangeMinConfirmations_HasNoStatableFigures(
        int utxoCount, int utxoSize, int minConfirmations)
        => Assert.False(RGBController.ArePersistedReplenishmentFiguresValid(
            Config(utxoCount, utxoSize, minConfirmations)));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public void ThePrintedFiguresReproduceTheEnforcedCeilingAtEveryVanillaUtxoCount(int utxoCount)
    {
        var printedBase = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(utxoCount);
        var printedPerUtxo = RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(utxoCount);
        for (var vanillaInputs = 1; vanillaInputs <= 250; vanillaInputs++)
        {
            var honest = RGBWalletService.EstimateTaprootFee(
                vanillaInputs, utxoCount + 1, RGBWalletService.CreateUtxosFeeRate);
            Assert.True(printedBase + printedPerUtxo * (vanillaInputs - 1) >= honest,
                $"UtxoCount {utxoCount}, vanilla inputs {vanillaInputs}: the card understates what can "
                + "actually be spent, so consent is given against a smaller number than the signer "
                + "admits.");
        }
    }

    [Fact]
    public void PopulateSettingsViewModel_DerivesEveryConsentFigureFromThePersistedConfigOnly()
    {
        var tree = PluginCompilation.Shared.Tree(ControllerFile);
        var method = RoslynPins.Method(tree, ControllerType, Populate);
        var body = RoslynPins.BodyOf(method);

        var postedReads = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax { Identifier.ValueText: "vm" }
                        && m.Name.Identifier.ValueText is "UtxoCount" or "UtxoSize" or "MinConfirmations")
            .Select(m => m.ToString())
            .ToList();
        Assert.True(postedReads.Count == 0,
            $"{Populate} reads {string.Join(", ", postedReads)}. On the ModelState-invalid re-render "
            + "those hold what the operator POSTED, and this method is called with preferSubmitted: true "
            + "from that path, so any consent figure derived from them describes a submission that was "
            + "rejected while the Authorize button on the very same page records a durable grant against "
            + "the PERSISTED values. Reachable with stored 20/100000 and a posted UtxoCount of 0: the "
            + "card read '0 at a time' at a 160-sat worst case.");

        var assignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "vm" }
            } left && ConsentFigureMembers.Contains(left.Name.Identifier.ValueText))
            .ToList();
        Assert.True(assignments.Count == ConsentFigureMembers.Length,
            $"{Populate} assigns {assignments.Count} of the {ConsentFigureMembers.Length} consent figures "
            + $"({string.Join(", ", ConsentFigureMembers)}); an unassigned one silently renders its "
            + "default and the card states a figure no configuration produced.");

        foreach (var assignment in assignments)
        {
            var right = assignment.Right.ToString();
            Assert.True(right.Contains("storedConfig", StringComparison.Ordinal),
                $"{Populate}: `{assignment}` does not read storedConfig. Every consent figure must come "
                + "from the persisted payment-method config, which is the only thing the unattended sweep "
                + "ever reads.");
        }
    }

    [Fact]
    public void TheGrantPost_RefusesWhenNoPersistedFiguresExist_AndNeverGatesRevocation()
    {
        var tree = PluginCompilation.Shared.Tree(ControllerFile);
        var method = RoslynPins.Method(tree, ControllerType, GrantAction);
        var body = RoslynPins.BodyOf(method);

        var gate = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: PersistedGate })
            .ToList();
        Assert.True(gate.Count == 1,
            $"{GrantAction} invokes {PersistedGate} {gate.Count} time(s); exactly one is expected. "
            + "Without it a direct POST records a durable grant against figures the consent card was "
            + "never able to state.");

        var record = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "RecordDecisionAsync"
            });
        Assert.True(gate[0].SpanStart < record.SpanStart,
            $"{GrantAction} must consult {PersistedGate} BEFORE writing the decision");

        var guard = gate[0].Ancestors().OfType<IfStatementSyntax>().First();
        var condition = guard.Condition.ToString();
        Assert.True(condition.Contains("grant &&", StringComparison.Ordinal),
            $"{GrantAction}'s guard condition is `{condition}`. It must be conjoined with `grant` so it "
            + "applies to authorization ONLY. Gating revocation would block the operator's emergency stop "
            + "on exactly the stores whose configuration has gone out of range — a permanent refusal of "
            + "the one action that must always be available.");
        Assert.Contains("return", guard.Statement.ToString());
    }

    [Fact]
    public void TheConsentCard_StatesPersistedFiguresAndWithholdsAuthorizeWithoutThem()
    {
        var card = ConsentCard();

        foreach (var posted in new[] { "@Model.UtxoCount", "@Model.UtxoSize" })
            Assert.DoesNotContain(posted, card);
        Assert.Contains("@Model.PersistedUtxoCount", card);
        Assert.Contains("@Model.PersistedUtxoSize", card);
        Assert.Contains("@Model.WorstCaseReplenishFeeBaseSats", card);
        Assert.Contains("@Model.WorstCaseReplenishFeePerVanillaUtxoSats", card);
        Assert.DoesNotContain("WorstCaseReplenishFeeSats", card);

        Assert.Matches(
            new Regex(@"else if \(Model\.PersistedUtxoCount\.HasValue\)[^}]*?"
                      + @"id=""rgb-authorize-auto-replenishment""", RegexOptions.Singleline),
            card);
        Assert.Contains("rgb-authorize-auto-replenishment-unavailable", card);

        var revokeAt = card.IndexOf("rgb-revoke-auto-replenishment", StringComparison.Ordinal);
        var authorizeGateAt = card.IndexOf(
            "else if (Model.PersistedUtxoCount.HasValue)", StringComparison.Ordinal);
        Assert.True(revokeAt >= 0 && authorizeGateAt > revokeAt,
            "the Revoke button must sit in the branch BEFORE the persisted-figures gate, so revocation is "
            + "never withheld — it is the emergency stop");
    }

    static string ConsentCard()
    {
        var view = RgbSettingsReadOnlyTests.ReadRepoFile(Path.Combine("Views", "RGB", "Settings.cshtml"));
        var start = view.IndexOf("id=\"rgb-auto-replenishment-card\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the automatic-replenishment consent card is missing from Settings.cshtml");
        var end = view.IndexOf("Wallet Information", start, StringComparison.Ordinal);
        Assert.True(end > start, "the consent card is no longer followed by the Wallet Information card");
        return view[start..end];
    }
}

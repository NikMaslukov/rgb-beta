using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPaymentRegistrationTests
{
    const string ListenerFile = "Services/RGBInvoiceListener.cs";

    // ---- the decisions, tested directly ----------------------------------------------------------

    [Theory] // G2-T1a, and the WaitingConfirmations row is G2-T7
    [InlineData(RGBInvoiceStatus.Settled, true, false)]
    [InlineData(RGBInvoiceStatus.Settled, false, true)]
    [InlineData(RGBInvoiceStatus.Underpaid, true, false)]
    [InlineData(RGBInvoiceStatus.Underpaid, false, true)]
    [InlineData(RGBInvoiceStatus.WaitingConfirmations, true, false)]
    [InlineData(RGBInvoiceStatus.WaitingConfirmations, false, true)]
    public void ShouldCommitAdvance_BlocksEveryAdvanceAfterAFailedRegistration(
        RGBInvoiceStatus status, bool registrationFailed, bool expected)
    {
        // The earlier form of this test asserted that WaitingConfirmations may commit after a failure
        // because it "self-heals". It does not: once the row leaves Pending, the waiting branch of
        // EvaluateInvoiceState stops matching it, so the failed registration is never re-attempted
        // while the transfer stays at status 2. Only the advance being held returns the row to that
        // branch. See WaitingConfirmations_HeldAfterAFailure_IsRetriedOnTheNextSweep below.
        Assert.Equal(expected, RGBInvoiceListener.ShouldCommitAdvance(status, registrationFailed));
    }

    [Fact]
    public void ShouldCommitAdvance_WithNoAdvanceToMake_IsNeverBlocked()
    {
        // A null status carries no payment work, so registrationFailed cannot be set for it; blocking
        // here would be a hold with nothing to retry.
        Assert.True(RGBInvoiceListener.ShouldCommitAdvance(null, true));
        Assert.True(RGBInvoiceListener.ShouldCommitAdvance(null, false));
    }

    [Fact]
    public void WaitingConfirmations_HeldAfterAFailure_IsRetriedOnTheNextSweep()
    {
        var invoice = new RGBInvoice
        {
            Id = "inv-1", WalletId = "w", RecipientId = "r", AssetId = "a",
            Amount = 100, Status = RGBInvoiceStatus.Pending
        };
        var inFlight = new[] { new RgbTransfer { Idx = 1, Status = 2, Amount = 100, Txid = "tx" } };

        var first = RGBInvoiceListener.EvaluateInvoiceState(invoice, inFlight);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, first.NewStatus);
        Assert.Equal(PaymentStatus.Processing, first.PaymentStatus);
        Assert.Single(first.PaymentsToRecord);

        // The gate refuses the advance, so the row keeps the status it had.
        Assert.False(RGBInvoiceListener.ShouldCommitAdvance(first.NewStatus, registrationFailed: true));

        var retry = RGBInvoiceListener.EvaluateInvoiceState(invoice, inFlight);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, retry.NewStatus);
        Assert.Equal(PaymentStatus.Processing, retry.PaymentStatus);
        Assert.Single(retry.PaymentsToRecord);
        Assert.Equal(1, retry.PaymentsToRecord[0].Idx);
    }

    [Fact]
    public void WaitingConfirmations_CommittedAfterAFailure_ProducesNoFurtherPaymentWork()
    {
        // The defect this fix closes, pinned as the counterfactual: had the advance been committed,
        // the same transfer yields no status and no payments, so nothing retries and nothing alarms.
        var advanced = new RGBInvoice
        {
            Id = "inv-1", WalletId = "w", RecipientId = "r", AssetId = "a",
            Amount = 100, Status = RGBInvoiceStatus.WaitingConfirmations
        };

        var result = RGBInvoiceListener.EvaluateInvoiceState(
            advanced, new[] { new RgbTransfer { Idx = 1, Status = 2, Amount = 100, Txid = "tx" } });

        Assert.Null(result.NewStatus);
        Assert.Null(result.PaymentStatus);
        Assert.Empty(result.PaymentsToRecord);
    }

    [Fact]
    public void AHeldWaitingConfirmationsInvoice_StillSettlesWhenTheTransferConfirms()
    {
        // Holding the row must not cost the settlement: the settled branch keys off "not Settled",
        // not off WaitingConfirmations, so a held invoice settles with the full cumulative amount.
        var held = new RGBInvoice
        {
            Id = "inv-1", WalletId = "w", RecipientId = "r", AssetId = "a",
            Amount = 100, Status = RGBInvoiceStatus.Pending
        };

        var result = RGBInvoiceListener.EvaluateInvoiceState(
            held, new[] { new RgbTransfer { Idx = 1, Status = 3, Amount = 100, Txid = "tx" } });

        Assert.Equal(RGBInvoiceStatus.Settled, result.NewStatus);
        Assert.Equal(PaymentStatus.Settled, result.PaymentStatus);
        Assert.Equal(100, result.ReceivedAmount);
    }

    [Fact] // G2-T9 and G2-T11
    public void ShouldRepublishOnAlreadyRecorded_IsBoundedToSettled()
    {
        Assert.True(RGBInvoiceListener.ShouldRepublishOnAlreadyRecorded(PaymentStatus.Settled));

        // An underpaid invoice stays in the sweep filter forever; republishing its already-Processing
        // payment would emit an event every ten seconds for the life of the invoice.
        Assert.False(RGBInvoiceListener.ShouldRepublishOnAlreadyRecorded(PaymentStatus.Processing));
    }

    [Fact] // G2-T12 — the blocker case: a failed insert must never satisfy the gate
    public void ClassifyNullAddPayment_WithTheInvoicePresentAndThePaymentAbsent_IsFailed()
    {
        var after = InvoiceWith("rgb:other:1");

        var outcome = RGBInvoiceListener.ClassifyNullAddPayment(after, new PaymentPrompt(), "rgb:me:0");

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Failed, outcome);
    }

    [Fact] // G2-T13 — the other three outcomes
    public void ClassifyNullAddPayment_DistinguishesDuplicateFromDeliberateRefusal()
    {
        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Recorded,
            RGBInvoiceListener.ClassifyNullAddPayment(InvoiceWith("rgb:me:0"), new PaymentPrompt(), "rgb:me:0"));

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Declined,
            RGBInvoiceListener.ClassifyNullAddPayment(null, null, "rgb:me:0"));

        // A missing prompt leaves the invoice present and the payment absent, which the presence test
        // alone reads as Failed — holding a deliberately refused registration forever.
        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Declined,
            RGBInvoiceListener.ClassifyNullAddPayment(InvoiceWith(), null, "rgb:me:0"));
    }

    // ---- the wiring, pinned syntactically because ProcessTransfers cannot be driven ---------------

    [Fact] // G2-T1b — a blocked advance must leave the row entirely untouched
    public void TheHeldAdvance_PrecedesEveryEntityWrite()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var gate = GateCall(body);
        var guard = gate.Ancestors().OfType<IfStatementSyntax>().First();
        var jump = guard.DescendantNodes().OfType<ContinueStatementSyntax>().ToList();
        Assert.True(jump.Count == 1, $"the gate must skip the advance with continue, found {jump.Count}");

        // All FOUR writes, not just inv.Status: comparing against one lets the other three be hoisted
        // above the guard, leaving a half-written row on a transition that was blocked.
        foreach (var field in new[] { "Status", "Txid", "ReceivedAmount", "SettledAt" })
        {
            var writes = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.ValueText == field
                            && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "inv" })
                .ToList();
            Assert.True(writes.Count == 1, $"expected exactly one write to inv.{field}, found {writes.Count}");
            Assert.True(jump[0].SpanStart < writes[0].SpanStart,
                $"inv.{field} is assigned before the held-advance continue — a blocked advance would "
                + "commit part of the transition");
        }
    }

    [Fact] // G2-T10(a) — the detection half; without it the whole fix ships inert
    public void ARegistrationThrow_SetsTheFailureFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var catches = body.DescendantNodes().OfType<CatchClauseSyntax>()
            .Where(c => c.Ancestors().OfType<ForEachStatementSyntax>()
                .Any(f => f.Expression.ToString().Contains("PaymentsToRecord")))
            .ToList();
        Assert.True(catches.Count == 1,
            $"expected exactly one catch around the registration call, found {catches.Count}");
        AssertSetsFlag(catches[0]);
    }

    [Fact] // G2-T10(b) — the other half of detection
    public void AFailedRegistrationOutcome_SetsTheFailureFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var comparisons = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("PaymentRegistration.Failed"))
            .ToList();
        Assert.True(comparisons.Count == 1,
            $"the Failed outcome must be compared exactly once, found {comparisons.Count}");
        AssertSetsFlag(comparisons[0]);
    }

    [Fact] // G2-T10(c) — the gate is called, and called with the real arguments
    public void TheAdvanceGate_IsCalledWithTheLiveStatusAndTheLiveFlag()
    {
        var body = RoslynPins.BodyOf(Listener("ProcessTransfers"));
        var gate = GateCall(body);
        var arguments = gate.ArgumentList.Arguments;
        Assert.True(arguments.Count == 2, $"expected (newStatus, registrationFailed), found {arguments.Count}");

        var status = Assert.IsType<MemberAccessExpressionSyntax>(arguments[0].Expression);
        Assert.Equal("NewStatus", status.Name.Identifier.ValueText);

        // A literal `false` here keeps every test and pin green while committing Settled after a
        // failed registration — G2 reproduced inside G2's own fix.
        Assert.True(arguments[1].Expression is IdentifierNameSyntax { Identifier.ValueText: "registrationFailed" },
            $"the second argument must be the live flag, found '{arguments[1]}'");
    }

    [Fact] // G2-T10(d) — the bounded republish, with its argument pinned
    public void TheAlreadyRecordedBranch_RepublishesThroughTheBoundedCondition()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var call = SingleCall(body, "ShouldRepublishOnAlreadyRecorded");
        Assert.True(call.ArgumentList.Arguments.Count == 1);

        // A literal PaymentStatus.Settled satisfies this clause and G2-T9/T11 while republishing
        // already-Processing payments on every poll forever.
        Assert.True(call.ArgumentList.Arguments[0].Expression
                is IdentifierNameSyntax { Identifier.ValueText: "targetStatus" },
            $"the republish must be decided from the live target status, found '{call.ArgumentList.Arguments[0]}'");

        var guard = call.Ancestors().OfType<IfStatementSyntax>().First();
        AssertPublishesNeedUpdate(guard);
    }

    [Fact] // G2-T10(e) — a genuine duplicate must still ask BTCPay to re-derive
    public void TheClassifiedDuplicate_PublishesNeedUpdate()
    {
        var body = RoslynPins.BodyOf(Listener("RecordOrUpdatePayment"));
        var classify = SingleCall(body, "ClassifyNullAddPayment");
        var branch = classify.Ancestors().OfType<BlockSyntax>().First();

        var recorded = branch.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("PaymentRegistration.Recorded"))
            .ToList();
        Assert.True(recorded.Count == 1,
            "the null-AddPayment path must publish when the classifier says Recorded; without it a "
            + $"genuine duplicate advances Settled with BTCPay never re-deriving, found {recorded.Count}");
        AssertPublishesNeedUpdate(recorded[0]);
    }

    [Fact] // G2-T15 — seam F wiring
    public void TheNullAddPaymentPath_RoutesThroughTheClassifier()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var method = Listener("RecordOrUpdatePayment");
        RoslynPins.AssertNoLocalShadow(method, "ClassifyNullAddPayment");

        var call = SingleCall(RoslynPins.BodyOf(method), "ClassifyNullAddPayment");
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, call));
        Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RGBInvoiceListener",
            symbol.ContainingType.ToDisplayString());

        // Re-queried state, not the stale entity the failed insert was built against.
        Assert.True(call.ArgumentList.Arguments.Count == 3);
        Assert.Equal("paymentId", call.ArgumentList.Arguments[2].Expression.ToString());
    }

    // ---- helpers ---------------------------------------------------------------------------------

    static InvoiceEntity InvoiceWith(params string[] paymentIds)
    {
        var invoice = new InvoiceEntity();
        // GetPayments reads this obsolete collection, and it is the only way to seed it from a test.
#pragma warning disable CS0618
        invoice.Payments = paymentIds
            .Select(id => new PaymentEntity { Id = id, Status = PaymentStatus.Settled })
            .ToList();
#pragma warning restore CS0618
        return invoice;
    }

    static MethodDeclarationSyntax Listener(string method) =>
        RoslynPins.Method(PluginCompilation.Shared.Tree(ListenerFile), "RGBInvoiceListener", method);

    static InvocationExpressionSyntax GateCall(SyntaxNode body) => SingleCall(body, "ShouldCommitAdvance");

    static InvocationExpressionSyntax SingleCall(SyntaxNode body, string name)
    {
        var matches = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == name)
            .ToList();
        Assert.True(matches.Count == 1, $"expected exactly one call to '{name}', found {matches.Count}");
        return matches[0];
    }

    static void AssertSetsFlag(SyntaxNode scope)
    {
        var sets = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "registrationFailed" }
                        && a.Right.ToString() == "true")
            .ToList();
        Assert.True(sets.Count == 1,
            $"registrationFailed must be set here, found {sets.Count} assignment(s) — the gate is a pure "
            + "function of this flag, so an unset flag makes the entire fix inert");
    }

    static void AssertPublishesNeedUpdate(SyntaxNode scope)
    {
        var published = scope.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Count(o => o.Type.ToString().EndsWith("InvoiceNeedUpdateEvent", StringComparison.Ordinal));
        Assert.True(published == 1,
            $"this branch must publish InvoiceNeedUpdateEvent exactly once, found {published}");
    }
}

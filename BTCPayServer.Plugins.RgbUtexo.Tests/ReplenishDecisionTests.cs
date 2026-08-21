using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ReplenishDecisionTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    const int Cap = 50;

    static ReplenishOutcome? Eligibility(
        bool isActive = true, bool needsRecovery = false, int maxAllocationsPerUtxo = 10,
        bool paymentMethodEnabled = true, string? configuredWalletId = "w1",
        DateTimeOffset? nextEligibleAt = null) =>
        RGBInvoiceListener.EvaluateReplenishEligibility(
            walletId: "w1", isActive: isActive, needsRecovery: needsRecovery,
            maxAllocationsPerUtxo: maxAllocationsPerUtxo, paymentMethodEnabled: paymentMethodEnabled,
            configuredWalletId: configuredWalletId, now: Now, nextEligibleAt: nextEligibleAt);

    static ReplenishDecision Demand(
        int colorableCount = 4, int usedByColorings = 0, int activePendingInvoices = 0,
        int maxAllocationsPerUtxo = 10, int minFreeSlots = 4, int utxoSize = 1000,
        int maxAutoColorableUtxos = Cap) =>
        RGBInvoiceListener.EvaluateReplenishDemand(
            colorableCount: colorableCount, usedByColorings: usedByColorings,
            activePendingInvoices: activePendingInvoices, maxAllocationsPerUtxo: maxAllocationsPerUtxo,
            minFreeSlots: minFreeSlots, utxoSize: utxoSize, maxAutoColorableUtxos: maxAutoColorableUtxos);

    [Fact]
    public void RgbExcludedForTheStore_Skips()
        => Assert.Equal(ReplenishOutcome.SkipPaymentMethodDisabled, Eligibility(paymentMethodEnabled: false));

    [Fact]
    public void NoRgbConfigAtAll_Skips()
        => Assert.Equal(ReplenishOutcome.SkipPaymentMethodDisabled,
            Eligibility(paymentMethodEnabled: false, configuredWalletId: null));

    [Fact]
    public void ConfigNamesADifferentWallet_Skips()
        => Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(configuredWalletId: "other"));

    [Fact]
    public void QuarantinedWallet_Skips()
        => Assert.Equal(ReplenishOutcome.SkipQuarantined, Eligibility(needsRecovery: true));

    [Fact]
    public void InactiveWallet_Skips()
        => Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(isActive: false));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxAllocations_Skips(int maxAlloc)
        => Assert.Equal(ReplenishOutcome.SkipInvalidWalletConfig, Eligibility(maxAllocationsPerUtxo: maxAlloc));

    [Fact]
    public void CooldownBoundary_SkipsOnlyStrictlyBefore()
    {
        Assert.Equal(ReplenishOutcome.SkipCooldown, Eligibility(nextEligibleAt: Now.AddTicks(1)));
        Assert.Null(Eligibility(nextEligibleAt: Now));
        Assert.Null(Eligibility(nextEligibleAt: Now.AddTicks(-1)));
    }

    // Pins the documented PRECEDENCE, one gate at a time: with every condition failing, satisfy the gates in
    // order and each successive outcome must appear. Asserting only the first-place gate would leave every
    // permutation of the other five green. (Which gates precede ListUnspentsAsync is P-C3's job, not this
    // test's — this one is about the order among the six refusals.)
    [Fact]
    public void SkipConditions_ArePrioritisedInTheDocumentedOrder()
    {
        Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(
            isActive: false, needsRecovery: true, maxAllocationsPerUtxo: 0,
            paymentMethodEnabled: false, configuredWalletId: "other", nextEligibleAt: Now.AddHours(1)));

        Assert.Equal(ReplenishOutcome.SkipCooldown, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0,
            paymentMethodEnabled: false, configuredWalletId: "other", nextEligibleAt: Now.AddHours(1)));

        Assert.Equal(ReplenishOutcome.SkipPaymentMethodDisabled, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0,
            paymentMethodEnabled: false, configuredWalletId: "other"));

        Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0, configuredWalletId: "other"));

        Assert.Equal(ReplenishOutcome.SkipQuarantined, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0));

        Assert.Equal(ReplenishOutcome.SkipInvalidWalletConfig, Eligibility(maxAllocationsPerUtxo: 0));
    }

    [Fact]
    public void HealthyEnabledMatchingWallet_IsEligible() => Assert.Null(Eligibility());

    [Fact]
    public void EnoughFreeSlots_DoesNotCreate()
    {
        var decision = Demand();
        Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, decision.Outcome);
        Assert.Equal(0, decision.RequestCount);
    }

    // The attacker's lever, isolated: identical inputs except the stale-invoice term.
    [Fact]
    public void StalePendingInvoices_AreTheOnlyDifferenceBetweenSkipAndCreate()
    {
        Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, Demand(activePendingInvoices: 0).Outcome);
        Assert.Equal(ReplenishOutcome.Create, Demand(activePendingInvoices: 37).Outcome);
    }

    [Fact]
    public void CapAlreadyReached_DoesNotCreate()
        => Assert.Equal(ReplenishOutcome.SkipCapReached, Demand(colorableCount: Cap).Outcome);

    // UtxoSize is the number of sats buried in each created UTXO, so returning anything but the configured
    // value changes how much the automatic path spends. It is asserted on the skip outcomes too — not
    // because the shell logs it there (it does not), but because a mutation that corrupts the size only on
    // the Create path is the one that costs money, and pinning every outcome leaves it nowhere to hide.
    [Theory]
    [InlineData(1000)]
    [InlineData(4242)]
    public void UtxoSize_IsCarriedThroughUnchanged(int utxoSize)
    {
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize, activePendingInvoices: 37).UtxoSize);
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize).UtxoSize);
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize, colorableCount: Cap).UtxoSize);
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize, maxAutoColorableUtxos: 0).UtxoSize);
    }

    // The cap must actually bind: with maxAlloc 10 and freeSlots 0, needed = ceil(minFreeSlots/10), so
    // minFreeSlots must exceed (Cap - colorableCount) * 10 = 100 for `needed + colorableCount` to pass 50.
    [Fact]
    public void DemandBeyondTheCap_IsClampedToTheCap()
        => Assert.Equal(Cap, Demand(colorableCount: 40, usedByColorings: 400, minFreeSlots: 200).RequestCount);

    // Genuine int overflow: freeSlots must be 0 and maxAlloc 1, so `needed` is int.MaxValue and
    // `needed + colorableCount` wraps negative under int arithmetic (Math.Clamp would then yield 0).
    [Fact]
    public void HugeMinFreeSlots_DoesNotOverflow()
    {
        var decision = Demand(colorableCount: Cap - 1, usedByColorings: Cap - 1, maxAllocationsPerUtxo: 1,
            minFreeSlots: int.MaxValue);
        Assert.Equal(ReplenishOutcome.Create, decision.Outcome);
        Assert.Equal(Cap, decision.RequestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NonPositiveMinFreeSlots_NeverCreates(int minFreeSlots)
        => Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, Demand(minFreeSlots: minFreeSlots).Outcome);

    // The invariant that makes a `request <= colorableCount` guard unreachable.
    [Fact]
    public void EveryCreateOutcome_RequestsStrictlyMoreThanTheCurrentCount()
    {
        foreach (var colorable in new[] { 0, 1, 7, Cap - 1 })
        foreach (var maxAlloc in new[] { 1, 3, 10 })
        foreach (var pending in new[] { 5, 50, 500 })
        {
            var decision = Demand(colorableCount: colorable, activePendingInvoices: pending,
                maxAllocationsPerUtxo: maxAlloc);
            if (decision.Outcome != ReplenishOutcome.Create) continue;
            Assert.True(decision.RequestCount > colorable);
            Assert.True(decision.RequestCount <= Cap);
        }
    }

    // A non-positive cap must not reach Math.Clamp, whose min > max throws ArgumentException.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCap_SkipsWithoutThrowing(int cap)
        => Assert.Equal(ReplenishOutcome.SkipCapReached,
            Demand(activePendingInvoices: 500, maxAutoColorableUtxos: cap).Outcome);

    // The unchanged-behaviour anchor for a healthy wallet on today's defaults.
    [Fact]
    public void TodaysDefaults_ForAHealthyWallet_DoNotCreate()
        => Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots,
            Demand(colorableCount: 4, usedByColorings: 0, activePendingInvoices: 0,
                maxAllocationsPerUtxo: 10, minFreeSlots: 4).Outcome);

    static bool FinalAuthorization(
        bool enabled = true,
        bool active = true,
        bool quarantined = false,
        string storeId = "s1",
        RGBPaymentMethodConfig? current = null,
        RGBPaymentMethodConfig? expected = null)
    {
        current ??= new RGBPaymentMethodConfig { UtxoCount = 4, UtxoSize = 1000, MinConfirmations = 1 };
        expected ??= new RGBPaymentMethodConfig { UtxoCount = 4, UtxoSize = 1000, MinConfirmations = 1 };
        return RGBInvoiceListener.IsAutomaticReplenishmentAuthorized(
            new RGBWallet
            {
                Id = "w1", StoreId = storeId, IsActive = active,
                NeedsRecovery = quarantined, MaxAllocationsPerUtxo = 10
            },
            "s1", enabled, current, expected);
    }

    [Fact]
    public void FinalAuthorization_HealthyUnchangedState_Passes()
        => Assert.True(FinalAuthorization());

    [Fact]
    public void FinalAuthorization_DisabledAfterDemandDecision_Rejects()
        => Assert.False(FinalAuthorization(enabled: false));

    [Theory]
    [InlineData(true, false, "other")]
    [InlineData(false, false, "s1")]
    [InlineData(true, true, "s1")]
    public void FinalAuthorization_WrongStoreInactiveOrQuarantined_Rejects(
        bool active, bool quarantined, string storeId)
        => Assert.False(FinalAuthorization(active: active, quarantined: quarantined, storeId: storeId));

    [Fact]
    public void FinalAuthorization_ConfigChangedAfterDemandDecision_Rejects()
        => Assert.False(FinalAuthorization(current: new RGBPaymentMethodConfig
        {
            UtxoCount = 4, UtxoSize = 2000, MinConfirmations = 1
        }));

    [Fact]
    public void FinalRequest_FreshDemandStillExactlyMatches_Passes()
    {
        var decision = Demand(activePendingInvoices: 37);
        Assert.True(RGBInvoiceListener.IsCurrentReplenishmentRequestAuthorized(
            decision, decision.RequestCount, decision.UtxoSize));
    }

    [Fact]
    public void FinalRequest_InvoiceDemandDisappearedWhileWaiting_Rejects()
    {
        var original = Demand(activePendingInvoices: 37);
        var fresh = Demand(activePendingInvoices: 0);
        Assert.Equal(ReplenishOutcome.Create, original.Outcome);
        Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, fresh.Outcome);
        Assert.False(RGBInvoiceListener.IsCurrentReplenishmentRequestAuthorized(
            fresh, original.RequestCount, original.UtxoSize));
    }

    [Fact]
    public void FinalRequest_UtxoStateChangedWhileWaiting_RejectsStaleCount()
    {
        var original = Demand(colorableCount: 4, activePendingInvoices: 37);
        var fresh = Demand(colorableCount: 5, activePendingInvoices: 47);
        Assert.Equal(ReplenishOutcome.Create, fresh.Outcome);
        Assert.NotEqual(original.RequestCount, fresh.RequestCount);
        Assert.False(RGBInvoiceListener.IsCurrentReplenishmentRequestAuthorized(
            fresh, original.RequestCount, original.UtxoSize));
    }
}

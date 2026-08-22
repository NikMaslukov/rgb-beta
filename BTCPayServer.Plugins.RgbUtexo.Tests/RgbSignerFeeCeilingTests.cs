using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSignerFeeCeilingTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // A PSBT producer that supplies an authentic NonWitnessUtxo alongside a WitnessUtxo understating
    // the same script used to slip an oversized fee past MaxFeeSats: the ceiling read WitnessUtxo
    // first while NBitcoin signs from NonWitnessUtxo, so the signature committed to the real, larger
    // input value and the difference went to miners. On the Create-UTXOs path MaxFeeSats is the only
    // bound on value leakage, so this drained the wallet's spendable balance.
    //
    // Amounts are chosen so the test discriminates: reading the understated 5_000 gives a 500-sat fee
    // that passes the 10_000 ceiling, while resolving through GetTxOut() gives 95_500 and must fail.
    [Fact]
    public async Task FeeCeiling_AuthenticNonWitnessUtxoWithUnderstatedWitnessUtxo_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        // The funding tx carries a dummy input because PSBT.ToBase64() refuses to serialise a
        // NonWitnessUtxo whose transaction has no inputs.
        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), addr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(4_500), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].NonWitnessUtxo = fundingTx;
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(5_000), addr.ScriptPubKey);

        var policy = new SigningPolicy
        {
            MaxFeeSats = 10_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("exceeds max allowed", ex.Message);
    }

    // Liveness companion: the honest shape (both fields agreeing, as every real producer emits since
    // both derive from the same prev tx) must still sign.
    [Fact]
    public async Task FeeCeiling_AgreeingUtxoFields_Signs()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), addr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(95_000), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].NonWitnessUtxo = fundingTx;
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy
        {
            MaxFeeSats = 10_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);
        Assert.NotEmpty(signed);
    }

    static (PSBT Psbt, BitcoinAddress Address) MultiInputSweep(
        Network network, int inputs, long perInputSats, long feeSats)
    {
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        for (var i = 0; i < inputs; i++)
            fundingTx.Outputs.Add(Money.Satoshis(perInputSats), addr);

        var tx = Transaction.Create(network);
        for (var i = 0; i < inputs; i++)
            tx.Inputs.Add(new OutPoint(fundingTx, i), Script.Empty);
        var spendable = perInputSats * inputs - feeSats;
        tx.Outputs.Add(Money.Satoshis(1_000), addr);
        tx.Outputs.Add(Money.Satoshis(spendable - 1_000), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        for (var i = 0; i < inputs; i++)
        {
            psbt.Inputs[i].NonWitnessUtxo = fundingTx;
            psbt.Inputs[i].WitnessUtxo = fundingTx.Outputs[i];
        }
        return (psbt, addr);
    }

    static SigningPolicy ProductionCreateUtxosFeePolicy(int requestCount, BitcoinAddress own) =>
        new()
        {
            MaxFeeSats = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount),
            MaxFeeSatsPerAdditionalInput =
                RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount),
            AllowedScripts = new HashSet<Script> { own.ScriptPubKey }
        };

    [Fact]
    public void ProductionCreateUtxosPolicyPair_SumsToExactlyThreeTimesTheHonestFeeForTheShapePresented()
    {
        for (var requestCount = 1; requestCount <= RgbConfigBounds.UtxoCountMax; requestCount++)
        for (var vanillaInputs = 1; vanillaInputs <= 250; vanillaInputs++)
        {
            var honest = RGBWalletService.EstimateTaprootFee(
                vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
            var ceiling = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
                + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount)
                  * (vanillaInputs - 1);
            Assert.True(ceiling == honest * RGBWalletService.CreateUtxosFeeCeilingMultiplier,
                $"requestCount {requestCount}, vanilla inputs {vanillaInputs}: the two policy members sum "
                + $"to {ceiling} sat where three times the honest estimate is "
                + $"{honest * RGBWalletService.CreateUtxosFeeCeilingMultiplier} sat. SCOPE OF THIS TEST, "
                + "stated because an earlier wording claimed a behavioural consequence this test cannot "
                + "observe: it is ARITHMETIC over "
                + $"{nameof(RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput)} and "
                + $"{nameof(RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput)} and it NEVER "
                + "invokes the signer, so it says nothing about the ceiling the signer enforces. That "
                + "the enforced ceiling equals this sum — and therefore that an honest sweep signs — is "
                + "measured only by "
                + $"{nameof(EnforcedCeiling_SignsTheHonestSweepOfDustValueVanillaInputs)} and "
                + $"{nameof(EnforcedCeiling_RefusesOneSatAboveTheShapeBoundedCeilingOnDustValueInputs)}, "
                + "which feed the signer 546-sat inputs. MEASURED: this arithmetic held over the whole "
                + "requestCount 1..20 x inputs 1..250 grid while the enforced ceiling still refused the "
                + "honest fee from 86 dust-valued inputs upward, because an absolute 10000-sat floor and "
                + "a value-proportional rule were min-clamped over this sum inside the signer. Every "
                + "behavioural row of this file funded each input with 100000 sat, which masks both "
                + "clamps across the entire grid. WHY the ceiling must be "
                + "affine in the INPUT count rather than in requestCount: rgb-lib 0.3.0-beta.30, commit "
                + "12da9a6, builds create_utxos_begin_impl's `inputs` from ALL of internal_unspents() "
                + "minus get_reserved_vanilla_outpoints and then calls "
                + "create_split_tx -> add_utxos(inputs).manually_selected_only(), so `num` sets the "
                + "RECIPIENT count and the input count is the wallet's whole non-reserved vanilla set.");
        }
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(1, 7)]
    [InlineData(1, 40)]
    [InlineData(20, 7)]
    public void TheRequestCountOnlyCeiling_RefusedTheHonestFeeFromSevenVanillaInputsUpward(
        int requestCount, int vanillaInputs)
    {
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
        var requestCountOnly = RGBWalletService.EstimateTaprootFee(
                requestCount, requestCount + 1, RGBWalletService.CreateUtxosFeeRate)
            * RGBWalletService.CreateUtxosFeeCeilingMultiplier;
        var shapeBounded = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
            + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount)
              * (vanillaInputs - 1);

        Assert.True(honest <= shapeBounded,
            $"requestCount {requestCount}, vanilla inputs {vanillaInputs}: the shape-bounded ceiling "
            + $"{shapeBounded} sat must admit the honest fee {honest} sat");
        Assert.Equal(
            requestCount == 1 && vanillaInputs >= 7,
            honest > requestCountOnly);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(1, 40)]
    [InlineData(4, 20)]
    public async Task ShapeBoundedCeiling_SignsTheHonestMultiInputSweep(int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, vanillaInputs, 100_000, honest);

        var signed = await signer.SignPsbtAsync(
            psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr));
        Assert.NotEmpty(signed);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(1, 40)]
    [InlineData(4, 20)]
    public async Task ShapeBoundedCeiling_StillRefusesOneSatAboveThreeTimesTheHonestFee(
        int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var ceiling = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
            + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount) * (vanillaInputs - 1);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, vanillaInputs, 100_000, ceiling + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(
                psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr)));
        Assert.Contains($"exceeds max allowed {ceiling} sat", ex.Message);
    }

    [Fact]
    public async Task ThePerInputTermIsLoadBearing_WithoutItSevenVanillaInputsCanNeverBeSweptAtUtxoCountOne()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var honest = RGBWalletService.EstimateTaprootFee(
            7, 2, RGBWalletService.CreateUtxosFeeRate);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, 7, 100_000, honest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), Network.RegTest, new SigningPolicy
            {
                MaxFeeSats = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(1),
                AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
            }));
        Assert.Contains(
            $"PSBT fee ({honest} sat) exceeds max allowed "
            + $"{RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(1)} sat",
            ex.Message);
    }

    [Fact]
    public async Task MaxFeeSatsPerAdditionalInput_DefaultsToZeroSoEverySingleInputPolicyIsUnchanged()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, 1, 100_000, 924);

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), Network.RegTest, new SigningPolicy
        {
            MaxFeeSats = 924,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        });
        Assert.NotEmpty(signed);

        var (tooDear, _) = MultiInputSweep(Network.RegTest, 1, 100_000, 925);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(tooDear.ToBase64(), Network.RegTest, new SigningPolicy
            {
                MaxFeeSats = 924,
                AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
            }));
        Assert.Contains("exceeds max allowed 924 sat", ex.Message);
    }

    const long DustValuedInputSats = 546;

    static long ShapeBoundedCeiling(int requestCount, int vanillaInputs) =>
        RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
        + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount) * (vanillaInputs - 1);

    [Theory]
    [InlineData(1, 85)]
    [InlineData(1, 86)]
    [InlineData(1, 250)]
    [InlineData(20, 71)]
    [InlineData(20, 72)]
    public async Task EnforcedCeiling_SignsTheHonestSweepOfDustValueVanillaInputs(
        int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
        var (psbt, addr) = MultiInputSweep(
            Network.RegTest, vanillaInputs, DustValuedInputSats, honest);

        var failure = await Record.ExceptionAsync(() => signer.SignPsbtAsync(
            psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr)));
        Assert.True(failure is null,
            $"requestCount {requestCount}, {vanillaInputs} vanilla inputs of {DustValuedInputSats} sat: "
            + $"the signer refused the honest fee {honest} sat with '{failure?.Message}' although the "
            + $"shape-bounded ceiling this "
            + $"policy declares is {ShapeBoundedCeiling(requestCount, vanillaInputs)} sat. The inputs are "
            + "DUST-VALUED on purpose: with 100000-sat inputs the value-proportional rule alone admits "
            + "hundreds of thousands of sat, so it and the absolute floor are both masked and this row "
            + "cannot see either. A refusal here is a PERMANENT false-REJECT — every automatic sweep and "
            + "the manual button both throw, the colorable pool is never refilled and RGB payments stop, "
            + "with no configuration able to lift it because neither the floor nor the percentage is a "
            + "policy member the create-UTXOs path sets. Whatever the signer composes MaxFeeSats and "
            + "MaxFeeSatsPerAdditionalInput with must not be able to LOWER their sum.");
    }

    [Theory]
    [InlineData(1, 86)]
    [InlineData(1, 250)]
    [InlineData(20, 72)]
    public async Task EnforcedCeiling_RefusesOneSatAboveTheShapeBoundedCeilingOnDustValueInputs(
        int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var ceiling = ShapeBoundedCeiling(requestCount, vanillaInputs);
        var (psbt, addr) = MultiInputSweep(
            Network.RegTest, vanillaInputs, DustValuedInputSats, ceiling + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(
                psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr)));
        Assert.True(
            ex.Message.Contains($"PSBT fee ({ceiling + 1} sat) exceeds max allowed {ceiling} sat",
                StringComparison.Ordinal),
            $"requestCount {requestCount}, {vanillaInputs} vanilla inputs of {DustValuedInputSats} sat: "
            + $"the refusal must name {ceiling} sat as the enforced ceiling, it said '{ex.Message}'. This "
            + "asserts the NUMBER, not merely that a refusal happened: a value-proportional or absolute "
            + "clamp under the shape-bounded sum still refuses this over-fee, so a message-blind row "
            + "passes while the honest fee of the same shape is refused too.");
    }

    [Fact]
    public async Task MaxFeeSatsOnlyPolicy_KeepsTheValueProportionalFloorAsItsEffectiveCeiling()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var floor = MemoryWalletSigner.ValueProportionalFeeCeilingFloorSats;
        var (atFloor, addr) = MultiInputSweep(Network.RegTest, 86, DustValuedInputSats, floor);

        SigningPolicy Policy() => new()
        {
            MaxFeeSats = 100_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var atFloorFailure = await Record.ExceptionAsync(
            () => signer.SignPsbtAsync(atFloor.ToBase64(), Network.RegTest, Policy()));
        Assert.True(atFloorFailure is null,
            $"a policy that declares MaxFeeSats alone must still admit a fee of exactly {floor} sat; it "
            + $"refused with '{atFloorFailure?.Message}'. Deleting the floor to fix the create-UTXOs "
            + "ceiling breaks every MaxFeeSats-only path on a low-value input set.");

        var (justAbove, _) = MultiInputSweep(Network.RegTest, 86, DustValuedInputSats, floor + 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(justAbove.ToBase64(), Network.RegTest, Policy()));
        Assert.True(
            ex.Message.Contains($"exceeds max allowed {floor} sat", StringComparison.Ordinal),
            $"a policy that declares MaxFeeSats alone and leaves MaxFeeSatsPerAdditionalInput at its "
            + $"default must keep the value-proportional rule and its {floor}-sat floor as its effective "
            + $"ceiling; here 10 percent of the output value is far below {floor}, so the floor governs "
            + $"and {floor + 1} sat must be refused naming {floor}. It said '{ex.Message}'. The "
            + "create-UTXOs fix must NOT be implemented by deleting the floor or the percentage: SendBtc "
            + "and SendAsset both declare MaxFeeSats without a per-input term and rely on this "
            + "composition unchanged.");
    }
}

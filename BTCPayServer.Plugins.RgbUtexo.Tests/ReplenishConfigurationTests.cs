using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ReplenishConfigurationTests
{
    // rgb.json is one of the two delivery paths (the environment is the other, pinned below), so the JSON
    // keys are part of the contract: a misspelled JsonPropertyName would silently ignore an operator who set
    // the cap to 0, and every property-level test would still pass. LoadConfiguration uses System.Text.Json.
    [Fact]
    public void SnakeCaseJsonKeys_DeserializeOntoTheKnobs()
    {
        const string json = """
            {
              "max_auto_colorable_utxos": 7,
              "auto_utxo_cooldown_minutes": 21,
              "auto_utxo_max_backoff_minutes": 99
            }
            """;
        var cfg = JsonSerializer.Deserialize<RGBConfiguration>(json);
        Assert.NotNull(cfg);
        Assert.Equal(7, cfg!.MaxAutoColorableUtxos);
        Assert.Equal(21, cfg.AutoUtxoCooldownMinutes);
        Assert.Equal(99, cfg.AutoUtxoMaxBackoffMinutes);
    }

    // An operator writing 0 means "no automatic creation"; it must survive deserialization unclamped so
    // EvaluateReplenishDemand can honour it as SkipCapReached.
    [Fact]
    public void ZeroCapInJson_SurvivesDeserialization()
    {
        var cfg = JsonSerializer.Deserialize<RGBConfiguration>("""{ "max_auto_colorable_utxos": 0 }""");
        Assert.NotNull(cfg);
        Assert.Equal(0, cfg!.MaxAutoColorableUtxos);
    }

    static Func<string, string?> Env(params (string Key, string Value)[] pairs) =>
        key => pairs.FirstOrDefault(p => p.Key == key).Value;

    // WHY the three knobs are settable from the environment: rgb.json is the only other delivery mechanism,
    // and writing that file is hazardous — it replaces the whole configuration object, so omitting
    // rgb_base_dir silently resets it to the literal default "/data" and moves every wallet path with no
    // migration. Forcing an operator to take that risk in order to BOUND unattended signing would be a
    // perverse trade, so these tests pin that the safe path exists.
    [Fact]
    public void EnvironmentOverrides_SetAllThreeKnobs()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, Env(
            ("RGB_MAX_AUTO_COLORABLE_UTXOS", "7"),
            ("RGB_AUTO_UTXO_COOLDOWN_MINUTES", "21"),
            ("RGB_AUTO_UTXO_MAX_BACKOFF_MINUTES", "99")));

        Assert.Equal(7, cfg.MaxAutoColorableUtxos);
        Assert.Equal(21, cfg.AutoUtxoCooldownMinutes);
        Assert.Equal(99, cfg.AutoUtxoMaxBackoffMinutes);
    }

    // The off-switch, and the reason this delivery path matters most: an operator disabling unattended
    // signing entirely is precisely the one who should not have to hand-edit rgb.json to do it.
    [Fact]
    public void EnvironmentZeroCap_DisablesAutomaticCreation()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, Env(("RGB_MAX_AUTO_COLORABLE_UTXOS", "0")));
        Assert.Equal(0, cfg.MaxAutoColorableUtxos);
    }

    // An unparseable value must not read as zero: zero is meaningful here and would silently disable
    // automatic creation on a typo.
    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("12x")]
    public void UnparseableEnvValue_LeavesTheConfiguredValue(string raw)
    {
        var cfg = new RGBConfiguration { MaxAutoColorableUtxos = 33 };
        RGBPlugin.ApplyEnvironmentOverrides(cfg, Env(("RGB_MAX_AUTO_COLORABLE_UTXOS", raw)));
        Assert.Equal(33, cfg.MaxAutoColorableUtxos);
    }

    [Fact]
    public void UnsetEnvironment_LeavesEveryKnobUntouched()
    {
        var cfg = new RGBConfiguration { MaxAutoColorableUtxos = 33, AutoUtxoCooldownMinutes = 44 };
        RGBPlugin.ApplyEnvironmentOverrides(cfg, _ => null);
        Assert.Equal(33, cfg.MaxAutoColorableUtxos);
        Assert.Equal(44, cfg.AutoUtxoCooldownMinutes);
    }

    // LoadConfiguration applies the environment after deserializing the file, so the environment wins.
    [Fact]
    public void EnvironmentOverride_BeatsTheFileValue()
    {
        var cfg = JsonSerializer.Deserialize<RGBConfiguration>("""{ "max_auto_colorable_utxos": 5 }""")!;
        RGBPlugin.ApplyEnvironmentOverrides(cfg, Env(("RGB_MAX_AUTO_COLORABLE_UTXOS", "9")));
        Assert.Equal(9, cfg.MaxAutoColorableUtxos);
    }

    [Fact]
    public void Defaults_MatchTheSpecifiedValues()
    {
        var cfg = new RGBConfiguration();
        Assert.Equal(50, cfg.MaxAutoColorableUtxos);
        Assert.Equal(30, cfg.AutoUtxoCooldownMinutes);
        Assert.Equal(160, cfg.AutoUtxoMaxBackoffMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveCooldown_FallsBackToTheDefault(int configured)
    {
        var cfg = new RGBConfiguration { AutoUtxoCooldownMinutes = configured };
        Assert.Equal(30, cfg.AutoUtxoCooldownMinutes);
    }

    // The defect this pins is invisible to every test of the tracker in isolation, because it lives in the
    // relationship between two constants in different files. A base cooldown at or below the sweep period is
    // unreachable: the listener stamps _lastUtxoCheck AFTER the sweep, so the next sweep starts later than
    // end + period, by which time a wallet settled during the sweep is already eligible. SkipCooldown then
    // never fires on a settle path and audit clause 3 ships its cap without its rate limit.
    // Raising the DEFAULT fixed the shipped case and left the trap armed for anyone who SETS the knob. `10`
    // is exactly what an operator upgrading from an earlier build pins to keep the old cadence, and it
    // reproduces the inert cooldown verbatim for that deployment.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(19)]
    public void ConfiguredCooldownWithoutAFullSweepOfMargin_IsRaised(int configured)
    {
        var cfg = new RGBConfiguration { AutoUtxoCooldownMinutes = configured };
        Assert.True(cfg.AutoUtxoCooldownMinutes >= RGBInvoiceListener.UtxoCheckMinutes * 2,
            $"a configured cooldown of {configured} min resolved to {cfg.AutoUtxoCooldownMinutes} min; it must "
            + $"leave a full sweep period of margin above {RGBInvoiceListener.UtxoCheckMinutes} min, because the "
            + "gate compares against an instant stamped mid-sweep");
    }

    // …and a value comfortably above it is left alone.
    [Fact]
    public void ConfiguredCooldownAboveTheSweepPeriod_IsRespected()
        => Assert.Equal(45, new RGBConfiguration { AutoUtxoCooldownMinutes = 45 }.AutoUtxoCooldownMinutes);

    [Fact]
    public void CooldownMustOutlastTheSweepPeriod()
    {
        Assert.True(new RGBConfiguration().AutoUtxoCooldownMinutes >= RGBInvoiceListener.UtxoCheckMinutes * 2,
            $"the default cooldown ({new RGBConfiguration().AutoUtxoCooldownMinutes} min) must exceed the "
            + $"sweep period ({RGBInvoiceListener.UtxoCheckMinutes} min) or it can never fire");
    }

    [Fact]
    public void MaxBackoffBelowTheCooldown_IsRaisedToTheCooldown()
    {
        var cfg = new RGBConfiguration { AutoUtxoCooldownMinutes = 30, AutoUtxoMaxBackoffMinutes = 5 };
        Assert.Equal(30, cfg.AutoUtxoMaxBackoffMinutes);
    }

    // WHY a zero cap is honoured rather than clamped: an operator writing 0 means "no automatic creation",
    // and EvaluateReplenishDemand turns it into SkipCapReached. Clamping it up would be a permission.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCap_IsHonouredNotClamped(int configured)
    {
        var cfg = new RGBConfiguration { MaxAutoColorableUtxos = configured };
        Assert.Equal(configured, cfg.MaxAutoColorableUtxos);
    }

    [Fact]
    public void ManualCeilingJsonKey_DeserializesOntoItsOwnKnob()
    {
        var cfg = JsonSerializer.Deserialize<RGBConfiguration>(
            """{ "max_manual_colorable_utxos": 12, "max_auto_colorable_utxos": 0 }""");
        Assert.NotNull(cfg);
        Assert.Equal(12, cfg!.MaxManualColorableUtxos);
        Assert.Equal(0, cfg.MaxAutoColorableUtxos);
    }

    [Fact]
    public void ManualCeilingDefault_IsAPositiveBoundOnTheColorablePool()
    {
        var cfg = new RGBConfiguration();
        Assert.Equal(250, cfg.MaxManualColorableUtxos);
        Assert.True(cfg.MaxManualColorableUtxos >= RgbConfigBounds.UtxoCountMax,
            $"the default manual ceiling ({cfg.MaxManualColorableUtxos}) must admit at least one "
            + $"maximum-size batch ({RgbConfigBounds.UtxoCountMax}) or a first press could be refused");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveManualCeiling_FallsBackToTheDefaultInsteadOfDisablingTheButton(int configured)
    {
        var cfg = new RGBConfiguration { MaxManualColorableUtxos = configured };
        Assert.True(cfg.MaxManualColorableUtxos > 0,
            $"a configured manual ceiling of {configured} resolved to {cfg.MaxManualColorableUtxos}. "
            + "Unlike MaxAutoColorableUtxos, zero is NOT a meaningful setting here: there is no "
            + "\"disable the Create UTXOs button\" feature, and a non-positive value would leave every "
            + "wallet already holding a colorable UTXO with no way to provision another.");
        Assert.Equal(250, cfg.MaxManualColorableUtxos);
    }

    [Fact]
    public void EnvironmentManualCeiling_IsSettableAndCannotBeDrivenNonPositive()
    {
        var raised = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(raised, Env(("RGB_MAX_MANUAL_COLORABLE_UTXOS", "900")));
        Assert.Equal(900, raised.MaxManualColorableUtxos);

        var zeroed = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(zeroed, Env(("RGB_MAX_MANUAL_COLORABLE_UTXOS", "0")));
        Assert.Equal(250, zeroed.MaxManualColorableUtxos);

        var typo = new RGBConfiguration { MaxManualColorableUtxos = 77 };
        RGBPlugin.ApplyEnvironmentOverrides(typo, Env(("RGB_MAX_MANUAL_COLORABLE_UTXOS", "12x")));
        Assert.Equal(77, typo.MaxManualColorableUtxos);
    }

    [Fact]
    public void TheAutomaticCapAndTheManualCeiling_AreIndependentKnobs()
    {
        var autoDisabled = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(autoDisabled, Env(("RGB_MAX_AUTO_COLORABLE_UTXOS", "0")));
        Assert.Equal(0, autoDisabled.MaxAutoColorableUtxos);
        Assert.Equal(250, autoDisabled.MaxManualColorableUtxos);

        var manualLowered = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(manualLowered, Env(("RGB_MAX_MANUAL_COLORABLE_UTXOS", "5")));
        Assert.Equal(5, manualLowered.MaxManualColorableUtxos);
        Assert.Equal(50, manualLowered.MaxAutoColorableUtxos);
    }
}

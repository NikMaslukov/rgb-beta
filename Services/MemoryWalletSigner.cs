using System.Collections.Concurrent;
using NBitcoin;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class MemoryWalletSigner : IRgbWalletSigner
{
    ExtKey? _masterKey;
    ExtKey? _vanillaAccountKey;
    ExtKey? _coloredAccountKey;
    ExtKey? _rgbColoredAccountKey;
    readonly ILogger? _logger;
    readonly object _lock = new();

    const uint GapLimitScanBuffer = 200;
    const uint MinScanBaseline = 1000;
    const uint MaxReasonableIndex = 100_000;
    KeyPath[]? _allowedAccountPrefixes;

    public string MasterFingerprint { get; }
    public string XpubVanilla { get; }
    public string XpubColored { get; }
    public bool IsDisposed { get; private set; }

    public MemoryWalletSigner(string mnemonic, Network network, ILogger? logger = null)
    {
        _logger = logger;

        var mnemonicObj = new Mnemonic(mnemonic);
        _masterKey = mnemonicObj.DeriveExtKey();

        MasterFingerprint = _masterKey.GetPublicKey().GetHDFingerPrint().ToString().ToLowerInvariant();

        var isTestnet = network != Network.Main;
        var vanillaPath = new KeyPath(isTestnet ? "m/84'/1'/0'" : "m/84'/0'/0'");
        var coloredPath = new KeyPath(isTestnet ? "m/86'/1'/0'" : "m/86'/0'/0'");

        _vanillaAccountKey = _masterKey.Derive(vanillaPath);
        _coloredAccountKey = _masterKey.Derive(coloredPath);

        var rgbCoinType = isTestnet ? 827167 : 827166;
        _rgbColoredAccountKey = _masterKey.Derive(new KeyPath($"m/86'/{rgbCoinType}'/0'"));

        XpubVanilla = _vanillaAccountKey.Neuter().ToString(network);
        XpubColored = _coloredAccountKey.Neuter().ToString(network);

        _allowedAccountPrefixes = [vanillaPath, coloredPath, new KeyPath($"m/86'/{rgbCoinType}'/0'")];
    }

    bool IsAllowedAccountPath(KeyPath path)
    {
        if (_allowedAccountPrefixes == null || path.Indexes.Length != 5) return false;
        var chain = path.Indexes[3];
        var index = path.Indexes[4];
        if (chain > 1) return false;
        if ((index & 0x80000000) != 0 || index > MaxReasonableIndex) return false;
        var accountIndexes = path.Indexes.AsSpan()[..3];
        foreach (var prefix in _allowedAccountPrefixes)
            if (prefix.Indexes.AsSpan().SequenceEqual(accountIndexes))
                return true;
        return false;
    }
    
    public Task<string> SignPsbtAsync(string psbtBase64, Network network, SigningPolicy policy, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var psbt = PSBT.Parse(psbtBase64.Trim('"'), network);

        CalibrateIndexCeiling(psbt);
        PopulateInputKeyPaths(psbt, network);
        ValidateOutputs(psbt, network, policy);
        RgbSighashGuard.EnsureAllInputsAllowed(psbt);

        foreach (var input in psbt.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignInput(psbt, input);
        }

        for (int i = 0; i < psbt.Inputs.Count; i++)
        {
            var inp = psbt.Inputs[i];
            if (inp.PartialSigs.Count == 0 && inp.TaprootKeySignature == null && inp.FinalScriptWitness == null && inp.FinalScriptSig == null)
                throw new InvalidOperationException(
                    $"PSBT input #{i} was not signed — no matching key found. The wallet may need to be re-synced.");
        }

        psbt.TryFinalize(out _);
        return Task.FromResult(psbt.ToBase64());
    }

    void CalibrateIndexCeiling(PSBT psbt)
    {
        foreach (var input in psbt.Inputs)
        {
            foreach (var kp in input.HDTaprootKeyPaths)
                UpdateCeiling(kp.Value.RootedKeyPath.MasterFingerprint, kp.Value.RootedKeyPath.KeyPath);
            foreach (var kp in input.HDKeyPaths)
                UpdateCeiling(kp.Value.MasterFingerprint, kp.Value.KeyPath);
        }
        foreach (var output in psbt.Outputs)
        {
            foreach (var kp in output.HDTaprootKeyPaths)
                UpdateCeiling(kp.Value.RootedKeyPath.MasterFingerprint, kp.Value.RootedKeyPath.KeyPath);
            foreach (var kp in output.HDKeyPaths)
                UpdateCeiling(kp.Value.MasterFingerprint, kp.Value.KeyPath);
        }
    }

    void UpdateCeiling(HDFingerprint fp, KeyPath path)
    {
        if (!fp.ToString().Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase)) return;
        if (!IsAllowedAccountPath(path)) return;
        var lastIndex = path.Indexes[^1];
        InterlockedMax(ref _highestVerifiedIndex, lastIndex);
    }

    internal bool IsOwnOutput(PSBTOutput output, Script outputScript, Network network)
    {
        if (_masterKey == null) return false;

        foreach (var kp in output.HDTaprootKeyPaths)
        {
            if (!kp.Value.RootedKeyPath.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedAccountPath(kp.Value.RootedKeyPath.KeyPath)) continue;

            var derived = _masterKey.Derive(kp.Value.RootedKeyPath.KeyPath);
            if (derived.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == outputScript)
                return true;
        }

        foreach (var kp in output.HDKeyPaths)
        {
            if (!kp.Value.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedAccountPath(kp.Value.KeyPath)) continue;

            var derived = _masterKey.Derive(kp.Value.KeyPath);
            if (derived.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey == outputScript)
                return true;
        }

        return false;
    }

    readonly ConcurrentDictionary<Script, byte> _verifiedScripts = new();
    uint _highestVerifiedIndex;

    const int MaxVerifiedScripts = 10_000;

    internal bool IsOwnScript(Script script, Network network)
    {
        if (_verifiedScripts.ContainsKey(script)) return true;

        var accounts = new ExtKey?[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };
        foreach (var account in accounts)
        {
            if (account == null) continue;
            var xpub = account.Neuter();
            for (int chain = 0; chain <= 1; chain++)
            {
                var chainPub = xpub.Derive((uint)chain);
                uint scanLimit = Math.Max(MinScanBaseline, Volatile.Read(ref _highestVerifiedIndex) + GapLimitScanBuffer);
                for (uint idx = 0; idx <= scanLimit; idx++)
                {
                    var pubkey = chainPub.Derive(idx).PubKey;
                    if (pubkey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == script ||
                        pubkey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey == script)
                    {
                        if (_verifiedScripts.Count < MaxVerifiedScripts)
                            _verifiedScripts.TryAdd(script, 0);
                        InterlockedMax(ref _highestVerifiedIndex, idx);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    static void InterlockedMax(ref uint location, uint value)
    {
        uint initial, computed;
        do
        {
            initial = Volatile.Read(ref location);
            if (value <= initial) return;
            computed = value;
        } while (Interlocked.CompareExchange(ref location, computed, initial) != initial);
    }

    void ValidateOutputs(PSBT psbt, Network network, SigningPolicy policy)
    {
        if (policy.MaxOutputCount.HasValue && psbt.Outputs.Count > policy.MaxOutputCount.Value)
            throw new InvalidOperationException(
                $"PSBT has {psbt.Outputs.Count} outputs, policy allows at most {policy.MaxOutputCount.Value}");

        if (policy.AllowedScripts != null)
        {
            foreach (var script in policy.AllowedScripts)
                if (!IsOwnScript(script, network))
                    throw new InvalidOperationException(
                        $"AllowedScripts contains address not derivable from wallet keys: {script.GetDestinationAddress(network)?.ToString() ?? script.ToHex()}");
        }

        Script? destScript = null;
        if (!string.IsNullOrEmpty(policy.ExpectedDestination))
        {
            destScript = BitcoinAddress.Create(policy.ExpectedDestination, network).ScriptPubKey;
        }

        long totalToDest = 0;
        long totalUnknown = 0;
        for (int i = 0; i < psbt.Outputs.Count; i++)
        {
            var txOut = psbt.GetGlobalTransaction().Outputs[i];
            var script = txOut.ScriptPubKey;
            var amount = txOut.Value.Satoshi;

            if (!policy.StrictAllowedScriptsOnly && IsOwnOutput(psbt.Outputs[i], script, network)) continue;
            if (policy.AllowedScripts != null && policy.AllowedScripts.Contains(script)) continue;
            if (destScript != null && script == destScript) { totalToDest += amount; continue; }
            if (script.IsUnspendable)
            {
                if (amount > 0)
                    throw new InvalidOperationException(
                        $"PSBT output #{i} is unspendable (OP_RETURN) with nonzero value ({amount} sat) — potential burn attack");
                continue;
            }

            if (amount > policy.MaxUnknownOutputSats)
            {
                var addr = script.GetDestinationAddress(network)?.ToString() ?? script.ToHex();
                throw new InvalidOperationException(
                    $"PSBT output #{i} to unknown address {addr}, amount {amount} sat exceeds policy limit of {policy.MaxUnknownOutputSats} sat");
            }
            totalUnknown += amount;
        }

        if (totalUnknown > policy.MaxUnknownOutputSats)
            throw new InvalidOperationException(
                $"PSBT cumulative unknown output total ({totalUnknown} sat) exceeds policy limit of {policy.MaxUnknownOutputSats} sat");

        if (policy.ExpectedAmountSats.HasValue && destScript != null && totalToDest != policy.ExpectedAmountSats.Value)
            throw new InvalidOperationException(
                $"PSBT total to destination ({totalToDest} sat) does not match expected ({policy.ExpectedAmountSats.Value} sat)");

        long totalInputValue = 0;
        // Resolve every input through GetTxOut(), which is the same accessor NBitcoin signs from.
        // It prefers NonWitnessUtxo, so reading WitnessUtxo first let a PSBT producer declare an
        // understated input value: the fee computed here stayed under MaxFeeSats while the sighash
        // committed to the real, larger amount, and the difference was paid to miners.
        foreach (var input in psbt.Inputs)
        {
            var prevOut = input.GetTxOut();
            if (prevOut != null)
                totalInputValue += prevOut.Value.Satoshi;
        }

        if (totalInputValue == 0 && psbt.Inputs.Count > 0)
            throw new InvalidOperationException("PSBT inputs lack UTXO data — cannot compute fee");

        if (totalInputValue > 0)
        {
            var totalOutputValue = psbt.GetGlobalTransaction().Outputs.Sum(o => o.Value.Satoshi);
            var fee = totalInputValue - totalOutputValue;
            var maxFee = (long)(totalOutputValue * policy.MaxFeePercent / 100.0);
            if (maxFee < 10_000) maxFee = 10_000;
            if (policy.MaxFeeSats.HasValue && policy.MaxFeeSats.Value < maxFee)
                maxFee = policy.MaxFeeSats.Value;
            if (fee > maxFee)
                throw new InvalidOperationException(
                    $"PSBT fee ({fee} sat) exceeds max allowed {maxFee} sat");
        }
    }

    void PopulateInputKeyPaths(PSBT psbt, Network network)
    {
        var fingerprint = new HDFingerprint(Convert.FromHexString(MasterFingerprint));
        var accounts = new ExtKey?[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };

        foreach (var input in psbt.Inputs)
        {
            if (input.HDKeyPaths.Count > 0 || input.HDTaprootKeyPaths.Count > 0) continue;
            if (input.WitnessUtxo == null) continue;
            var script = input.WitnessUtxo.ScriptPubKey;

            foreach (var account in accounts)
            {
                if (account == null) continue;
                var xpub = account.Neuter();
                var accountPath = account == _vanillaAccountKey
                    ? (network != Network.Main ? new KeyPath("84'/1'/0'") : new KeyPath("84'/0'/0'"))
                    : account == _coloredAccountKey
                        ? (network != Network.Main ? new KeyPath("86'/1'/0'") : new KeyPath("86'/0'/0'"))
                        : new KeyPath($"86'/{(network != Network.Main ? 827167 : 827166)}'/0'");

                for (int chain = 0; chain <= 1; chain++)
                {
                    var chainPub = xpub.Derive((uint)chain);
                    uint scanLimit = Math.Max(MinScanBaseline, Volatile.Read(ref _highestVerifiedIndex) + GapLimitScanBuffer);
                    for (uint idx = 0; idx <= scanLimit; idx++)
                    {
                        var pubkey = chainPub.Derive(idx).PubKey;

                        if (pubkey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == script)
                        {
                            var fullPath = accountPath.Derive(new KeyPath($"{chain}/{idx}"));
                            input.HDTaprootKeyPaths.Add(
                                pubkey.GetTaprootFullPubKey(),
                                new TaprootKeyPath(new RootedKeyPath(fingerprint, fullPath)));
                            InterlockedMax(ref _highestVerifiedIndex, idx);
                            goto nextInput;
                        }

                        if (pubkey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey == script)
                        {
                            var fullPath = accountPath.Derive(new KeyPath($"{chain}/{idx}"));
                            input.HDKeyPaths.Add(pubkey, new RootedKeyPath(fingerprint, fullPath));
                            InterlockedMax(ref _highestVerifiedIndex, idx);
                            goto nextInput;
                        }
                    }
                }
            }
            nextInput:;
        }
    }

    void SignInput(PSBT psbt, PSBTInput input)
    {
        if (_masterKey == null) return;

        foreach (var kp in input.HDKeyPaths)
        {
            if (!kp.Value.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedAccountPath(kp.Value.KeyPath)) continue;
            psbt.SignWithKeys(_masterKey.Derive(kp.Value.KeyPath));
        }

        foreach (var kp in input.HDTaprootKeyPaths)
        {
            if (!kp.Value.RootedKeyPath.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedAccountPath(kp.Value.RootedKeyPath.KeyPath)) continue;
            psbt.SignWithKeys(_masterKey.Derive(kp.Value.RootedKeyPath.KeyPath));
        }
    }
    
    public void Dispose()
    {
        if (IsDisposed) return;
        
        lock (_lock)
        {
            if (IsDisposed) return;
            
            ClearKeyMaterial();
            IsDisposed = true;
        }
        
        GC.SuppressFinalize(this);
        _logger?.LogDebug("MemoryWalletSigner disposed");
    }
    
    void ClearKeyMaterial()
    {
        _masterKey = null;
        _vanillaAccountKey = null;
        _coloredAccountKey = null;
        _rgbColoredAccountKey = null;
        _verifiedScripts.Clear();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }
}

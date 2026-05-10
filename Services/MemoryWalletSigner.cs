using NBitcoin;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class MemoryWalletSigner : IRgbWalletSigner
{
    ExtKey? _masterKey;
    ExtKey? _vanillaAccountKey;
    ExtKey? _coloredAccountKey;
    ExtKey? _rgbColoredAccountKey;
    readonly Dictionary<string, ExtKey> _derivedKeys = new();
    readonly ILogger? _logger;
    readonly object _lock = new();
    
    const int PreDeriveCount = 20;
    
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
        
        PreDeriveKeys();
    }
    
    void PreDeriveKeys()
    {
        foreach (var account in new[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey })
        {
            if (account == null) continue;
            for (int i = 0; i < PreDeriveCount; i++)
            {
                CacheKey(account, $"0/{i}");
                CacheKey(account, $"1/{i}");
            }
        }
    }
    
    void CacheKey(ExtKey? accountKey, string subPath)
    {
        if (accountKey == null) return;
        var fullPath = $"{accountKey.GetPublicKey().GetHDFingerPrint()}/{subPath}";
        _derivedKeys[fullPath] = accountKey.Derive(new KeyPath(subPath));
    }
    
    HashSet<Script>? _knownScripts;
    Network? _cachedNetwork;

    public Task<string> SignPsbtAsync(string psbtBase64, Network network, SigningPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var psbt = PSBT.Parse(psbtBase64.Trim('"'), network);

        ValidateOutputs(psbt, network, policy ?? new SigningPolicy());

        PopulateTaprootMetadata(psbt, network);

        foreach (var input in psbt.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignInput(psbt, input);
        }

        psbt.TryFinalize(out _);
        return Task.FromResult(psbt.ToBase64());
    }

    void ValidateOutputs(PSBT psbt, Network network, SigningPolicy policy)
    {
        var known = GetKnownScripts(network);
        Script? destScript = null;
        if (!string.IsNullOrEmpty(policy.ExpectedDestination))
        {
            try { destScript = BitcoinAddress.Create(policy.ExpectedDestination, network).ScriptPubKey; }
            catch { }
        }

        long totalToDest = 0;
        long totalUnknown = 0;
        for (int i = 0; i < psbt.Outputs.Count; i++)
        {
            var txOut = psbt.GetGlobalTransaction().Outputs[i];
            var script = txOut.ScriptPubKey;
            var amount = txOut.Value.Satoshi;

            if (known.Contains(script)) continue;
            if (destScript != null && script == destScript) { totalToDest += amount; continue; }
            if (script.IsUnspendable) continue;
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
        var globalInputs = psbt.GetGlobalTransaction().Inputs;
        for (int j = 0; j < psbt.Inputs.Count; j++)
        {
            var input = psbt.Inputs[j];
            if (input.WitnessUtxo != null)
                totalInputValue += input.WitnessUtxo.Value.Satoshi;
            else if (input.NonWitnessUtxo != null)
                totalInputValue += input.NonWitnessUtxo.Outputs[globalInputs[j].PrevOut.N].Value.Satoshi;
        }

        if (totalInputValue == 0 && psbt.Inputs.Count > 0)
            throw new InvalidOperationException("PSBT inputs lack UTXO data — cannot compute fee");

        if (totalInputValue > 0)
        {
            var totalOutputValue = psbt.GetGlobalTransaction().Outputs.Sum(o => o.Value.Satoshi);
            var fee = totalInputValue - totalOutputValue;
            var maxFee = (long)(totalOutputValue * policy.MaxFeePercent / 100.0);
            if (maxFee < 10_000) maxFee = 10_000;
            if (fee > maxFee)
                throw new InvalidOperationException(
                    $"PSBT fee ({fee} sat) exceeds {policy.MaxFeePercent}% of output total ({totalOutputValue} sat), max allowed {maxFee} sat");
        }
    }

    HashSet<Script> GetKnownScripts(Network network)
    {
        if (_knownScripts != null && _cachedNetwork == network) return _knownScripts;

        var scripts = new HashSet<Script>();
        var accounts = new[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };

        foreach (var account in accounts)
        {
            if (account == null) continue;
            for (int i = 0; i < 1000; i++)
            {
                var recv = account.Derive(new KeyPath($"0/{i}"));
                var change = account.Derive(new KeyPath($"1/{i}"));

                scripts.Add(recv.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey);
                scripts.Add(change.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey);
                scripts.Add(recv.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey);
                scripts.Add(change.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey);
            }
        }

        _knownScripts = scripts;
        _cachedNetwork = network;
        return scripts;
    }

    void PopulateTaprootMetadata(PSBT psbt, Network network)
    {
        var accounts = new[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };
        foreach (var input in psbt.Inputs)
        {
            if (input.TaprootInternalKey != null) continue;
            if (input.WitnessUtxo == null) continue;
            var script = input.WitnessUtxo.ScriptPubKey;
            var bytes = script.ToBytes();
            if (bytes.Length != 34 || bytes[0] != 0x51 || bytes[1] != 0x20) continue;

            foreach (var account in accounts)
            {
                if (account == null) continue;
                for (int chain = 0; chain <= 1; chain++)
                {
                    for (int idx = 0; idx < 1000; idx++)
                    {
                        var derived = account.Derive(new KeyPath($"{chain}/{idx}"));
                        var taprootKey = derived.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);
                        if (taprootKey.ScriptPubKey == script)
                        {
                            input.TaprootInternalKey = derived.GetPublicKey().GetTaprootFullPubKey().InternalKey;
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
        if (input.HDKeyPaths.Count > 0)
        {
            SignWithHDPaths(psbt, input);
        }
        else if (input.HDTaprootKeyPaths.Count > 0)
        {
            SignTaprootWithHDPaths(psbt, input);
        }
        else
        {
            SignWithPreDerivedKeys(psbt, input);
        }
    }
    
    void SignWithHDPaths(PSBT psbt, PSBTInput input)
    {
        if (_masterKey == null) return;

        foreach (var hdKeyPath in input.HDKeyPaths)
        {
            var fingerprint = hdKeyPath.Value.MasterFingerprint.ToString();

            if (!fingerprint.Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            var derivedKey = _masterKey.Derive(hdKeyPath.Value.KeyPath);
            psbt.SignWithKeys(derivedKey);
        }
    }

    void SignTaprootWithHDPaths(PSBT psbt, PSBTInput input)
    {
        if (_masterKey == null) return;

        foreach (var taprootKeyPath in input.HDTaprootKeyPaths)
        {
            var fingerprint = taprootKeyPath.Value.RootedKeyPath.MasterFingerprint.ToString();

            if (!fingerprint.Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            var derivedKey = _masterKey.Derive(taprootKeyPath.Value.RootedKeyPath.KeyPath);
            psbt.SignWithKeys(derivedKey);
        }
    }
    
    void SignWithPreDerivedKeys(PSBT psbt, PSBTInput input)
    {
        if (input.TaprootInternalKey != null)
        {
            SignTaprootDirect(psbt, input);
            if (InputIsSigned(input)) return;
        }

        foreach (var key in _derivedKeys.Values)
        {
            psbt.SignWithKeys(key);

            if (InputIsSigned(input))
                return;
        }

        if (!InputIsSigned(input))
        {
            ExtendAndSign(psbt, input);
        }
    }

    void SignTaprootDirect(PSBT psbt, PSBTInput input)
    {
        var accounts = new[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };
        foreach (var account in accounts)
        {
            if (account == null) continue;
            for (int chain = 0; chain <= 1; chain++)
            {
                for (int idx = 0; idx < 1000; idx++)
                {
                    var derived = account.Derive(new KeyPath($"{chain}/{idx}"));
                    if (derived.GetPublicKey().GetTaprootFullPubKey().InternalKey == input.TaprootInternalKey)
                    {
                        psbt.SignWithKeys(derived);
                        return;
                    }
                }
            }
        }
    }
    
    void ExtendAndSign(PSBT psbt, PSBTInput input)
    {
        var accounts = new[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };

        for (int i = PreDeriveCount; i < PreDeriveCount + 10; i++)
        {
            var paths = new[] { $"0/{i}", $"1/{i}" };
            foreach (var account in accounts)
            {
                if (account == null) continue;
                foreach (var path in paths)
                {
                    var key = account.Derive(new KeyPath(path));
                    psbt.SignWithKeys(key);
                    if (InputIsSigned(input)) return;
                }
            }
        }
    }
    
    static bool InputIsSigned(PSBTInput input) =>
        input.PartialSigs.Count > 0 || input.FinalScriptSig != null || input.FinalScriptWitness != null || input.TaprootKeySignature != null;
    
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
        _derivedKeys.Clear();
        _masterKey = null;
        _vanillaAccountKey = null;
        _coloredAccountKey = null;
        _rgbColoredAccountKey = null;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }
}

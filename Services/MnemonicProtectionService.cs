using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.RGB.Services;

public class MnemonicProtectionService
{
    private readonly IDataProtector _protector;
    private const string Purpose = "BTCPayServer.Plugins.RGB.MnemonicProtection.v1";

    public MnemonicProtectionService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string mnemonic)
    {
        if (string.IsNullOrEmpty(mnemonic))
            return mnemonic;
        
        return _protector.Protect(mnemonic);
    }

    public string Unprotect(string protectedMnemonic)
    {
        if (string.IsNullOrEmpty(protectedMnemonic))
            return protectedMnemonic;

        try
        {
            return _protector.Unprotect(protectedMnemonic);
        }
        catch (Exception)
        {
            // If unprotection fails, assume it's already unprotected (migration case)
            // This handles existing wallets with plaintext mnemonics
            if (IsLikelyPlainMnemonic(protectedMnemonic))
                return protectedMnemonic;
            
            throw;
        }
    }

    private static bool IsLikelyPlainMnemonic(string value)
    {
        // BIP39 mnemonics are 12-24 lowercase words separated by spaces
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 12 and <= 24 
               && words.All(w => w.All(char.IsLower));
    }
}


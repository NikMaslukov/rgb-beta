using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class MnemonicProtectionService
{
    readonly IDataProtector _protector;
    readonly ILogger<MnemonicProtectionService> _log;
    const string Purpose = "BTCPayServer.Plugins.RgbUtexo.MnemonicProtection.v1";

    public MnemonicProtectionService(IDataProtectionProvider provider, ILogger<MnemonicProtectionService> log)
    {
        _protector = provider.CreateProtector(Purpose);
        _log = log;
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
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to decrypt mnemonic — possible key mismatch or data corruption", ex);
        }
    }
}

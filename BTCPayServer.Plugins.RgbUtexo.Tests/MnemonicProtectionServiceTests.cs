using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class MnemonicProtectionServiceTests
{
    static MnemonicProtectionService CreateService(IDataProtectionProvider? provider = null)
    {
        provider ??= new EphemeralDataProtectionProvider();
        return new MnemonicProtectionService(provider,
            NullLogger<MnemonicProtectionService>.Instance);
    }

    [Fact]
    public void ProtectThenUnprotect_ReturnsOriginal()
    {
        var svc = CreateService();
        var original = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var encrypted = svc.Protect(original);
        Assert.NotEqual(original, encrypted);
        var decrypted = svc.Unprotect(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Unprotect_RandomGarbage_ThrowsInvalidOperation()
    {
        var svc = CreateService();
        Assert.Throws<InvalidOperationException>(() =>
            svc.Unprotect("dGhpcyBpcyBub3QgZW5jcnlwdGVk"));
    }

    [Fact]
    public void Unprotect_ValidBip39Plaintext_ThrowsInvalidOperation()
    {
        var svc = CreateService();
        Assert.Throws<InvalidOperationException>(() =>
            svc.Unprotect("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"));
    }

    [Fact]
    public void Unprotect_Null_ReturnsNull()
    {
        var svc = CreateService();
        var result = svc.Unprotect(null!);
        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_Empty_ReturnsEmpty()
    {
        var svc = CreateService();
        var result = svc.Unprotect("");
        Assert.Equal("", result);
    }

    [Fact]
    public void KeyRotation_ProtectWithA_UnprotectWithB_Throws()
    {
        var providerA = new EphemeralDataProtectionProvider();
        var providerB = new EphemeralDataProtectionProvider();
        var svcA = CreateService(providerA);
        var svcB = CreateService(providerB);

        var encrypted = svcA.Protect("test mnemonic phrase");
        Assert.Throws<InvalidOperationException>(() => svcB.Unprotect(encrypted));
    }
}

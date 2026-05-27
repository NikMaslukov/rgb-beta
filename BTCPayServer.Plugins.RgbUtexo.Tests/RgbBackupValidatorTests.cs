using System.IO.Compression;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbBackupValidatorTests
{
    static IFormFile CreateFormFile(byte[] content, string name = "backup.rgb")
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", name);
    }

    static byte[] CreateValidZip(params string[] entryNames)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entryNames)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("data");
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task ValidateAsync_ValidZip_Passes()
    {
        var content = CreateValidZip("rgb_lib_db.sqlite", "wallet_data.json");
        await RgbBackupValidator.ValidateAsync(CreateFormFile(content));
    }

    [Fact]
    public async Task ValidateAsync_PathTraversal_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("../../etc/passwd");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(CreateFormFile(ms.ToArray())));
        Assert.Contains("path traversal", ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_RootedPath_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("/etc/passwd");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(CreateFormFile(ms.ToArray())));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public void Constants_AreReasonable()
    {
        Assert.True(RgbBackupValidator.MaxEntryUncompressedBytes <= 100 * 1024 * 1024);
        Assert.True(RgbBackupValidator.MaxTotalUncompressedBytes <= 100 * 1024 * 1024);
        Assert.True(RgbBackupValidator.MaxEntryCount is > 0 and <= 10000);
    }
}

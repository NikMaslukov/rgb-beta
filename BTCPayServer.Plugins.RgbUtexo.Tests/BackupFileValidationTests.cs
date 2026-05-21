using System.IO.Compression;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class BackupFileValidationTests
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
    public async Task ValidZipArchive_Passes()
    {
        var content = CreateValidZip("backup.dat");
        await RGBController.ValidateBackupFileHeader(CreateFormFile(content));
    }

    [Fact]
    public async Task ValidZipMultipleEntries_Passes()
    {
        var content = CreateValidZip("backup.dat", "metadata.json", "assets/token.rgb");
        await RGBController.ValidateBackupFileHeader(CreateFormFile(content));
    }

    [Fact]
    public async Task FakeZipHeader_InvalidStructure_Throws()
    {
        var content = new byte[16];
        content[0] = (byte)'P'; content[1] = (byte)'K'; content[2] = 0x03; content[3] = 0x04;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
    }

    [Fact]
    public async Task EmptyZipArchive_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) { }
        var content = ms.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
    }

    [Fact]
    public async Task PathTraversal_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("../../etc/passwd");
        }
        var content = ms.ToArray();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
        Assert.Contains("path traversal", ex.Message);
    }

    [Fact]
    public async Task TooSmall_Throws()
    {
        var content = new byte[] { 0x01, 0x02, 0x03 };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public async Task RandomBinary_Throws()
    {
        var random = new byte[16];
        new Random(42).NextBytes(random);
        random[3] = 0x00;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(random)));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public async Task HtmlFile_Throws()
    {
        var content = new byte[16];
        content[0] = (byte)'<';
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public async Task PeExecutable_Throws()
    {
        var content = new byte[16];
        content[0] = (byte)'M'; content[1] = (byte)'Z';
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public async Task ElfExecutable_Throws()
    {
        var content = new byte[16];
        content[0] = 0x7F; content[1] = (byte)'E'; content[2] = (byte)'L'; content[3] = (byte)'F';
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public async Task PlaintextFile_Throws()
    {
        var content = new byte[16];
        for (int i = 0; i < 16; i++) content[i] = (byte)('A' + i);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(content)));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public async Task AbsolutePath_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("/etc/passwd");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(ms.ToArray())));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public async Task WindowsAbsolutePath_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("\\Windows\\System32\\cmd.exe");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(ms.ToArray())));
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public async Task ZipBomb_LargeClaimedSize_Throws()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("bomb.dat", CompressionLevel.SmallestSize);
            using var writer = entry.Open();
            var chunk = new byte[1024];
            for (int i = 0; i < 60_000; i++)
                writer.Write(chunk, 0, chunk.Length);
        }
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(ms.ToArray())));
        Assert.Contains("limit", ex.Message);
    }
}

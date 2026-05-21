using System.IO.Compression;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbBackupValidator
{
    public const long MaxEntryUncompressedBytes = 50 * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 50 * 1024 * 1024;
    public const int MaxEntryCount = 1000;

    public static async Task ValidateAsync(IFormFile file, CancellationToken ct = default)
    {
        using var memStream = new MemoryStream();
        using (var input = file.OpenReadStream())
            await input.CopyToAsync(memStream, ct);

        ValidateBytes(memStream);
    }

    internal static void ValidateBytes(MemoryStream memStream)
    {
        if (memStream.Length < 4)
            throw new InvalidOperationException("Backup file too small");

        var header = memStream.GetBuffer();
        if (header[0] != 'P' || header[1] != 'K' || header[2] != 0x03 || header[3] != 0x04)
            throw new InvalidOperationException("Invalid backup file — expected ZIP archive (rgb-lib backup format)");

        memStream.Position = 0;
        try
        {
            using var zip = new ZipArchive(memStream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count == 0)
                throw new InvalidOperationException("Backup archive is empty");
            if (zip.Entries.Count > MaxEntryCount)
                throw new InvalidOperationException("Backup archive contains too many entries");

            long totalUncompressed = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal))
                    throw new InvalidOperationException("Backup archive contains path traversal entry");
                if (Path.IsPathRooted(entry.FullName) || entry.FullName.StartsWith("/", StringComparison.Ordinal)
                    || entry.FullName.StartsWith("\\", StringComparison.Ordinal))
                    throw new InvalidOperationException("Backup archive contains absolute path entry");
                if (entry.Length > MaxEntryUncompressedBytes)
                    throw new InvalidOperationException(
                        $"Backup entry '{entry.FullName}' uncompressed size ({entry.Length / 1024 / 1024}MB) exceeds limit");
                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxTotalUncompressedBytes)
                    throw new InvalidOperationException(
                        $"Backup total uncompressed size exceeds {MaxTotalUncompressedBytes / 1024 / 1024}MB limit (ZIP bomb protection)");
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("Backup file is not a valid ZIP archive");
        }
    }
}

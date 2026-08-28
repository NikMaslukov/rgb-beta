namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbRestoreStderrRedaction
{
    public const string UploadedBackupFilePlaceholder = "<the backup file you uploaded>";

    public const string UploadDirectoryPlaceholder = "<the server upload directory>";

    public const string StagingDirectoryPlaceholder = "<the restore staging directory>";

    public const string WalletDataDirectoryPlaceholder = "<the wallet data directory>";

    public const string RestoreHelperAssemblyPlaceholder = "<the restore helper>";

    public const string PluginInstallDirectoryPlaceholder = "<the plugin install directory>";

    public static string ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheHelper(
        string? childStdErr, string backupPath, string stagingDir, string? helperDll = null)
    {
        var text = childStdErr ?? string.Empty;
        foreach (var known in KnownAbsolutePathsLongestFirst(backupPath, stagingDir, helperDll))
            text = text.Replace(known.Path, known.Placeholder, StringComparison.Ordinal);
        return text;
    }

    static IEnumerable<(string Path, string Placeholder)> KnownAbsolutePathsLongestFirst(
        string backupPath, string stagingDir, string? helperDll)
        => new[]
        {
            (Path: backupPath, Placeholder: UploadedBackupFilePlaceholder),
            (Path: stagingDir, Placeholder: StagingDirectoryPlaceholder),
            (Path: helperDll ?? string.Empty, Placeholder: RestoreHelperAssemblyPlaceholder),
            (Path: ContainingDirectoryOrEmpty(backupPath), Placeholder: UploadDirectoryPlaceholder),
            (Path: ContainingDirectoryOrEmpty(stagingDir), Placeholder: WalletDataDirectoryPlaceholder),
            (Path: ContainingDirectoryOrEmpty(helperDll), Placeholder: PluginInstallDirectoryPlaceholder)
        }
        .Where(candidate => NamesAHostLocationRatherThanAFragmentThatCouldMangleTheDiagnostic(candidate.Path))
        .OrderByDescending(candidate => candidate.Path.Length)
        .ToList();

    static string ContainingDirectoryOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetDirectoryName(path) ?? string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    static bool NamesAHostLocationRatherThanAFragmentThatCouldMangleTheDiagnostic(string path)
        => !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path)
            && ContainingDirectoryOrEmpty(path).Length > 0;
}

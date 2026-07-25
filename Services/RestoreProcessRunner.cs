using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class RestoreProcessRunner : IRestoreProcessRunner
{
    readonly ILogger<RestoreProcessRunner> _log;
    readonly Func<ProcessStartInfo, IChildHandle> _handleFactory;
    readonly Func<string> _resolveHelperDll;
    readonly Func<string> _resolveDotnetHost;

    public RestoreProcessRunner(ILogger<RestoreProcessRunner> log,
        Func<ProcessStartInfo, IChildHandle>? handleFactory = null,
        Func<string>? resolveHelperDll = null,
        Func<string>? resolveDotnetHost = null)
    {
        _log = log;
        _handleFactory = handleFactory ?? (psi => new RealChildHandle(psi));
        _resolveHelperDll = resolveHelperDll ?? (() => Path.Combine(
            Path.GetDirectoryName(typeof(RestoreProcessRunner).Assembly.Location)!,
            "RgbRestoreHelper.dll"));
        _resolveDotnetHost = resolveDotnetHost ?? DefaultDotnetHost;
    }

    public async Task<RestoreRunResult> RunAsync(
        string backupPath, string stagingDir, string password,
        RestoreLimits limits, CancellationToken ct)
    {
        var helperDll = _resolveHelperDll();
        if (!File.Exists(helperDll))
            throw new InvalidOperationException(
                $"Restore helper not found at {helperDll}. The wallet was not restored.");

        var psi = BuildStartInfo(helperDll, backupPath, stagingDir, limits);
        IChildHandle child;
        try
        {
            child = _handleFactory(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to launch the restore helper process. The wallet was not restored.", ex);
        }

        // Dispose kills a still-running child (see RealChildHandle.Dispose), so ANY exception
        // escaping this block — e.g. a broken-pipe write — terminates the child rather than
        // leaking a live, unbounded restore. That leak would reintroduce the very DoS this fixes.
        using (child)
        {
            await child.WriteStdinLineAndCloseAsync(password);

            var killedReason = RestoreKillReason.None;
            var killed = false;
            var deadline = limits.Timeout;
            var sw = Stopwatch.StartNew();

            while (!child.HasExited)
            {
                if (sw.Elapsed >= deadline) { killed = true; break; }
                var dirSize = DirectorySize(stagingDir);
                var reason = RestoreWatchdog.ShouldKill(dirSize, SafeWorkingSet(child), limits);
                if (reason != RestoreKillReason.None) { killed = true; killedReason = reason; break; }
                try { await Task.Delay(limits.Poll, ct); }
                catch (OperationCanceledException) { killed = true; break; }
            }

            if (!killed && child.HasExited)
                return new RestoreRunResult(RestoreOutcome.Exited, child.ExitCode, await child.ReadStdErrAsync(), true);

            child.Kill(true);
            var reaped = await child.WaitForExitAsync(limits.ReapGrace, CancellationToken.None);
            var outcome = killedReason switch
            {
                RestoreKillReason.Disk => RestoreOutcome.KilledDisk,
                RestoreKillReason.Ram => RestoreOutcome.KilledRam,
                _ => RestoreOutcome.TimedOut
            };
            return new RestoreRunResult(outcome, null, "", reaped);
        }
    }

    // Framework-dependent hosting (BTCPay prod runs `dotnet BTCPayServer.dll`, dev runs `dotnet run`)
    // makes the current process host the dotnet muxer, so `<host> exec <helper.dll>` launches the
    // helper. A self-contained/apphost deploy would make the host BTCPayServer itself, and passing
    // `exec <helper.dll>` would spawn a second BTCPayServer — so fail closed unless the host is dotnet.
    static string DefaultDotnetHost() => ResolveDotnetHost(
        Environment.ProcessPath,
        RuntimeEnvironment.GetRuntimeDirectory(),
        Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        File.Exists,
        OperatingSystem.IsWindows());

    // Locate the dotnet muxer to run `dotnet exec RgbRestoreHelper.dll`. Environment.ProcessPath is
    // NOT reliably the muxer: `dotnet run` and any apphost deployment make it the BTCPayServer apphost,
    // and exec'ing THAT would spawn a second BTCPayServer. So resolve the real muxer, and fail closed
    // if it cannot be found rather than exec an unknown host.
    public static string ResolveDotnetHost(string? processPath, string? runtimeDir, string? dotnetRoot,
        Func<string, bool> fileExists, bool isWindows)
    {
        var muxer = isWindows ? "dotnet.exe" : "dotnet";

        if (!string.IsNullOrEmpty(processPath)
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        // Shared-framework layout: <root>/shared/Microsoft.NETCore.App/<ver>/ -> <root>/<muxer>.
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var derived = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", muxer));
            if (fileExists(derived)) return derived;
        }

        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var fromRoot = Path.Combine(dotnetRoot, muxer);
            if (fileExists(fromRoot)) return fromRoot;
        }

        throw new InvalidOperationException(
            "Could not locate the dotnet host to launch the restore helper. The wallet was not restored.");
    }

    ProcessStartInfo BuildStartInfo(string helperDll, string backupPath, string stagingDir, RestoreLimits limits)
    {
        var dotnet = _resolveDotnetHost();

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var prlimit = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ResolvePrlimitPath() : null;
        if (prlimit != null)
        {
            // prlimit sets the rlimit on itself then execvp()s the target IN PLACE (same PID),
            // and `dotnet exec` loads the CLR + helper + native rgblibcffi in-process (no fork).
            // So the tracked PID is the real restore process: WorkingSet64/Kill/WaitForExit all
            // observe/terminate the native work, not a wrapper. Use the verified absolute path (not a
            // bare name) so the launched binary never depends on PATH resolution.
            psi.FileName = prlimit;
            psi.ArgumentList.Add($"--cpu={(int)limits.CpuLimit.TotalSeconds}");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(dotnet);
        }
        else
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _log.LogWarning("prlimit unavailable on this Linux host — restore CPU is bounded only by the wall-clock kill");
            psi.FileName = dotnet;
        }
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(helperDll);
        psi.ArgumentList.Add(backupPath);
        psi.ArgumentList.Add(stagingDir);
        return psi;
    }

    static string? ResolvePrlimitPath()
    {
        foreach (var p in new[] { "/usr/bin/prlimit", "/bin/prlimit" })
            if (File.Exists(p)) return p;
        return null;
    }

    static long SafeWorkingSet(IChildHandle child)
    {
        try { return child.WorkingSet64; } catch { return 0; }
    }

    static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    sealed class RealChildHandle : IChildHandle
    {
        const int StdErrCapChars = 8192;

        readonly Process _p;
        readonly Task<string> _stderr;
        public RealChildHandle(ProcessStartInfo psi)
        {
            _p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            // Drain stderr concurrently from the start so a child that writes more than the OS pipe
            // buffer cannot block mid-restore (which would convert every restore into a timeout kill).
            // Retain only a capped prefix so a noisy child cannot shift the DoS into parent memory by
            // spewing unbounded stderr — the pipe is still fully drained, the overflow is discarded.
            _stderr = DrainCappedAsync(_p.StandardError, StdErrCapChars);
        }

        static async Task<string> DrainCappedAsync(StreamReader reader, int cap)
        {
            var sb = new StringBuilder();
            var buf = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                if (sb.Length < cap)
                    sb.Append(buf, 0, Math.Min(n, cap - sb.Length));
            }
            return sb.ToString();
        }
        public long WorkingSet64 { get { _p.Refresh(); return _p.WorkingSet64; } }
        public bool HasExited => _p.HasExited;
        public int ExitCode => _p.ExitCode;
        public void Kill(bool entireProcessTree)
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree); } catch { }
        }
        public async Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(grace);
            try { await _p.WaitForExitAsync(cts.Token); return true; }
            catch (OperationCanceledException) { return _p.HasExited; }
        }
        public Task<string> ReadStdErrAsync() => _stderr;
        public Task WriteStdinLineAndCloseAsync(string line)
        {
            try
            {
                _p.StandardInput.WriteLine(line);
                _p.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Child already exited (broken pipe): the supervise loop observes HasExited and
                // reports the real exit code — do not fault the whole restore on a closed stdin.
            }
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            try { if (!_p.HasExited) _p.Kill(true); } catch { }
            try { _p.Dispose(); } catch { }
        }
    }
}

namespace RgbRestoreHelper;

public static class Program
{
    public static int Main(string[] args)
        => Run(args, Console.In, Console.Out, Console.Error);

    public static int Run(string[] args, TextReader stdin, TextWriter stderr)
        => Run(args, stdin, TextWriter.Null, stderr);

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 2 && args[0] is "send-begin" or "send-end"
            && int.TryParse(args[1], out var selfTimeoutMs) && selfTimeoutMs is >= 100 and <= 600_000)
        {
            using var watchdog = new Timer(_ => Environment.FailFast("native send helper self-timeout"),
                null, selfTimeoutMs, Timeout.Infinite);
            try
            {
                var request = stdin.ReadToEnd();
                stdout.Write(RgbNativeSend.Invoke(args[0], request));
                return 0;
            }
            catch (Exception ex)
            {
                stderr.WriteLine(ex.GetBaseException().Message);
                return 1;
            }
        }

        if (args.Length != 2)
        {
            stderr.WriteLine("usage: RgbRestoreHelper <backupPath> <stagingDir> (password on stdin)");
            return 2;
        }

        var password = stdin.ReadLine();
        if (string.IsNullOrWhiteSpace(password))
        {
            stderr.WriteLine("no password provided on stdin");
            return 3;
        }

        try
        {
            var rc = RgbRestoreNative.Restore(args[0], args[1], password, out var error);
            if (rc != 0) stderr.WriteLine(error);
            return rc;
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }
}

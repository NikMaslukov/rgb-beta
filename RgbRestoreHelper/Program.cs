namespace RgbRestoreHelper;

public static class Program
{
    public static int Main(string[] args)
        => Run(args, Console.In, Console.Error);

    public static int Run(string[] args, TextReader stdin, TextWriter stderr)
    {
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

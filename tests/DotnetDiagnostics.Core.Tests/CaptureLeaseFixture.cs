namespace DotnetDiagnostics.Core.Tests;

internal static class CaptureLeaseFixture
{
    internal static int Run(string leasePath)
    {
        try
        {
            using var lease = File.Open(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Console.WriteLine("ready");
            Console.Out.Flush();
            if (Console.ReadLine() != "release")
            {
                Console.Error.WriteLine("Expected release command while holding the capture lease.");
                return 2;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

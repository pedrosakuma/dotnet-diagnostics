using System.Runtime.CompilerServices;

/// <summary>
/// Synthetic, secret-free workload for cross-version method-parameter capture tests. The
/// profiler allowlist targets only <see cref="Capture"/> (module MultiVersionSample.dll, signature
/// Int32/String/String); every call carries the same known label and an oversized payload so the
/// capture and preview caps are observable.
/// </summary>
static class MethodParameterFixture
{
    private const string Label = "known-label";
    private static readonly string Payload = "known-prefix-" + new string('x', 8_192);

    public static void Run()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();

        var checksum = 0;
        var iteration = 0;
        while (true)
        {
            checksum ^= Capture(123 + (iteration++ % 3), Label, Payload);
            if (checksum == int.MinValue)
            {
                Console.WriteLine(checksum);
            }

            Thread.Sleep(50);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Capture(int sequence, string label, string payload)
        => HashCode.Combine(sequence, label.Length, payload.Length);
}

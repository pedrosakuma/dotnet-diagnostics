using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = budget.Token;
await Request("/warmup", 0, false, token);
Console.WriteLine($"READY {Environment.ProcessId} {Environment.Version}");
while (await Console.In.ReadLineAsync(token) is { } command && command != "quit")
{
    if (command == "observe")
    {
        while (!EventSource.GetSources().Any(s => s.Name == "System.Net.Http" && s.IsEnabled()))
            await Task.Delay(10, token);
        Console.WriteLine("OBSERVING");
        continue;
    }

    var allocated = GC.GetTotalAllocatedBytes();
    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
    var watch = Stopwatch.StartNew();
    await Request("/serial-fast", 80, true, token);
    await Request("/serial-medium", 250, true, token);
    await Request("/serial-slow", 450, true, token);
    await Task.WhenAll(Request("/fast", 80, true, token), Request("/medium", 250, true, token),
        Request("/slow", 450, true, token));
    await Tls(token);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Phase = command, ElapsedMs = watch.Elapsed.TotalMilliseconds,
        CpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
        AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
        ActivityTrackingEnabled = ActivityTrackingEnabled(),
    }));
    Console.WriteLine("DONE");
}

static bool ActivityTrackingEnabled()
{
    // Observation only: read the runtime's sticky tracker state without enabling any EventListener.
    var type = typeof(EventSource).Assembly.GetType("System.Diagnostics.Tracing.ActivityTracker", throwOnError: true)!;
    var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
    return type.GetField("m_current", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance) is not null;
}

static async Task Tls(CancellationToken token)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    // Importing the PFX gives native Schannel a usable key handle instead of an ephemeral RSA key.
    // The key is not persisted (no PersistKeySet); only this loopback SslStream accepts the certificate.
    using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null,
        X509KeyStorageFlags.DefaultKeySet);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var server = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await Task.Delay(150, token);
        using var ssl = new SslStream(socket.GetStream());
        var watch = Stopwatch.StartNew();
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12,
            }, token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TLS server: {ex}");
            throw;
        }
        watch.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { TlsSide = "server", ElapsedMs = watch.Elapsed.TotalMilliseconds }));
    }, token);
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
    using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
    var watch = Stopwatch.StartNew();
    try
    {
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12,
        }, token);
        watch.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { TlsSide = "client", ElapsedMs = watch.Elapsed.TotalMilliseconds }));
    }
    finally { await server; }
}

static async Task Request(string path, int delay, bool report, CancellationToken token)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        using var stream = socket.GetStream();
        var buffer = new byte[4096];
        var used = 0;
        while (!Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), token);
            if (read == 0 || used + read == buffer.Length) throw new IOException("Incomplete HTTP headers.");
            used += read;
        }
        await Task.Delay(delay, token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
    }, token);
    using var handler = new SocketsHttpHandler { UseProxy = false };
    using var client = new HttpClient(handler);
    var uri = new Uri($"http://127.0.0.1:{port}{path}");
    var watch = Stopwatch.StartNew();
    using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
    watch.Stop();
    response.EnsureSuccessStatusCode();
    if (report)
        Console.WriteLine(JsonSerializer.Serialize(new { Path = path, ElapsedMs = watch.Elapsed.TotalMilliseconds }));
    await server;
}

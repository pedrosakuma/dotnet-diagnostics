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

    if (command is "failures-mixed" or "failures-only")
    {
        await FailureWorkload(command == "failures-mixed", token);
        Console.WriteLine("DONE");
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

static async Task FailureWorkload(bool includeResponses, CancellationToken token)
{
    if (includeResponses)
    {
        await ControlledHttpRequest("/success-200", 100, HttpStatusCode.OK, null, null, token);
        await ControlledHttpRequest("/status-503", 180, HttpStatusCode.ServiceUnavailable, null, null, token);
    }

    await ControlledHttpRequest("/cancelled", 0, null, TimeSpan.FromMilliseconds(250), null, token);
    await ControlledHttpRequest("/timed-out", 0, null, null, TimeSpan.FromMilliseconds(350), token);
    await FailedTls(token);
}

static async Task ControlledHttpRequest(
    string path,
    int serverDelayMilliseconds,
    HttpStatusCode? responseStatus,
    TimeSpan? cancelAfter,
    TimeSpan? clientTimeout,
    CancellationToken token)
{
    using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(token);
    lifecycle.CancelAfter(TimeSpan.FromSeconds(5));
    token = lifecycle.Token;
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var clientFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = ServeControlledHttpRequest(listener, serverDelayMilliseconds, responseStatus, clientFinished.Task, token);
    using var handler = new SocketsHttpHandler { UseProxy = false };
    using var client = new HttpClient(handler);
    if (clientTimeout is { } timeout) client.Timeout = timeout;
    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    if (cancelAfter is { } cancellationDelay) requestCancellation.CancelAfter(cancellationDelay);
    var watch = Stopwatch.StartNew();
    Exception? observed = null;
    HttpStatusCode? observedStatus = null;
    try
    {
        using var response = await client.GetAsync(
            new Uri($"http://127.0.0.1:{port}{path}"),
            HttpCompletionOption.ResponseHeadersRead,
            requestCancellation.Token);
        observedStatus = response.StatusCode;
    }
    catch (OperationCanceledException ex) when (cancelAfter is not null
        && !token.IsCancellationRequested && requestCancellation.IsCancellationRequested)
    {
        observed = ex;
    }
    catch (OperationCanceledException ex) when (clientTimeout is not null
        && !token.IsCancellationRequested
        && !requestCancellation.IsCancellationRequested
        && HasTimeoutException(ex))
    {
        observed = ex;
    }
    finally
    {
        watch.Stop();
        clientFinished.TrySetResult();
        await server;
    }

    var outcome = responseStatus switch
    {
        HttpStatusCode.OK when observedStatus == HttpStatusCode.OK && observed is null => "status-200",
        HttpStatusCode.ServiceUnavailable when observedStatus == HttpStatusCode.ServiceUnavailable && observed is null
            => "status-503",
        null when cancelAfter is not null && observed is OperationCanceledException => "cancelled",
        null when clientTimeout is not null && observed is OperationCanceledException => "timed-out",
        _ => throw new InvalidOperationException(
            $"Unexpected HTTP result for {path}: status={observedStatus}, exception={observed?.GetType().FullName}."),
    };
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Kind = "http",
        Path = path,
        Outcome = outcome,
        ElapsedMs = watch.Elapsed.TotalMilliseconds,
        Exception = observed?.GetType().FullName,
    }));
}

static async Task ServeControlledHttpRequest(
    TcpListener listener,
    int delayMilliseconds,
    HttpStatusCode? responseStatus,
    Task clientFinished,
    CancellationToken token)
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
    if (responseStatus is null)
    {
        // Cancellation/timeout must win, not an arbitrary scheduled peer close.
        await clientFinished.WaitAsync(token);
        return;
    }
    await Task.Delay(delayMilliseconds, token);
    var reason = responseStatus == HttpStatusCode.OK ? "OK" : "Service Unavailable";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {(int)responseStatus} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
}

static bool HasTimeoutException(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
        if (current is TimeoutException) return true;
    return false;
}

static async Task FailedTls(CancellationToken token)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var clientFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        using var stream = socket.GetStream();
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        var length = (header[3] << 8) | header[4];
        if (header[0] != 0x16 || length is < 1 or > 18432)
            throw new IOException("Expected a bounded TLS ClientHello record.");
        await stream.ReadExactlyAsync(new byte[length], token);
        await Task.Delay(250, token);
        // Fatal handshake_failure alert, after consuming ClientHello. Keep the socket open
        // until the client observes it; closing with unread bytes can cause a Windows TCP reset.
        await stream.WriteAsync(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 }, token);
        await clientFinished.Task.WaitAsync(token);
    }, token);
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
    using var ssl = new SslStream(client.GetStream());
    var watch = Stopwatch.StartNew();
    Exception observed;
    try
    {
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
        }, token);
        throw new InvalidOperationException("Rejecting TLS peer unexpectedly completed a handshake.");
    }
    catch (AuthenticationException ex)
    {
        observed = ex;
    }
    catch (IOException ex) when (ex.InnerException is AuthenticationException)
    {
        observed = ex;
    }
    finally
    {
        watch.Stop();
        clientFinished.TrySetResult();
        await server;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Kind = "tls",
        Outcome = "authentication-failed",
        ElapsedMs = watch.Elapsed.TotalMilliseconds,
        Exception = observed.GetType().FullName,
    }));
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
    var startedTimestamp = Stopwatch.GetTimestamp();
    try
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        var stoppedTimestamp = Stopwatch.GetTimestamp();
        response.EnsureSuccessStatusCode();
        if (report)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Path = path, Host = uri.GetLeftPart(UriPartial.Authority),
                ElapsedMs = Stopwatch.GetElapsedTime(startedTimestamp, stoppedTimestamp).TotalMilliseconds,
                StartedTimestamp = startedTimestamp, StoppedTimestamp = stoppedTimestamp,
            }));
    }
    finally
    {
        await server;
    }
}

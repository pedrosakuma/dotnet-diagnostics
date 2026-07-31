using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class TransportSecurityIntegrationTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("+")]
    public async Task CleartextNonLoopback_WithoutExplicitPolicy_IsRejectedAtStartup(
        string host)
    {
        var serverDll = FindServerDll();
        if (serverDll is null)
        {
            return;
        }

        using var process = StartServer(
            serverDll,
            GetAvailablePort(),
            "http",
            host);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var output = await CombinedOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);

        process.ExitCode.Should().Be(1);
        output.Should().Contain("Refusing to start: server is configured for cleartext HTTP on a non-loopback address");
        output.Should().Contain("MCP_ALLOW_INSECURE_HTTP");
    }

    [Fact]
    public async Task ExplicitInsecureDevelopmentOverride_StartsAndEmitsProminentWarning()
    {
        var serverDll = FindServerDll();
        if (serverDll is null)
        {
            return;
        }

        var port = GetAvailablePort();
        using var process = StartServer(
            serverDll,
            port,
            ("MCP_ALLOW_INSECURE_HTTP", "true"),
            ("Auth__BearerTokens__0__Name", "transport-test"),
            ("Auth__BearerTokens__0__Token", "transport-test-token"),
            ("Auth__BearerTokens__0__Scopes__0", "read-counters"));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await WaitForResponseAsync(
                process,
                client,
                $"http://127.0.0.1:{port}/health",
                stdoutTask,
                stderrTask,
                timeout.Token).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        var output = await CombinedOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        output.Should().Contain("INSECURE DEVELOPMENT OVERRIDE ACTIVE");
        output.Should().Contain("MCP_ALLOW_INSECURE_HTTP=true");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("+")]
    public async Task DirectTlsPem_WildcardHost_StartsHttpsListener(string host)
    {
        if (OperatingSystem.IsWindows())
        {
            // Kestrel's '*'/'+' URL aliases are accepted configuration syntax but
            // are not a reliable live-listener primitive on Windows CI. The
            // cross-platform BindingInspectorTests theory still verifies both
            // HTTPS aliases, while the HTTP process theory here still proves the
            // startup guard rejects both aliases before Kestrel binds.
            return;
        }

        var serverDll = FindServerDll();
        if (serverDll is null)
        {
            return;
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));

        var port = GetAvailablePort();
        using var process = StartServer(
            serverDll,
            port,
            "https",
            host,
            ("MCP_TLS_CERTIFICATE_PEM", certificate.ExportCertificatePem()),
            ("MCP_TLS_PRIVATE_KEY_PEM", rsa.ExportPkcs8PrivateKeyPem()),
            ("ASPNETCORE_HTTP_PORTS", "8080"),
            ("Auth__BearerTokens__0__Name", "tls-test"),
            ("Auth__BearerTokens__0__Token", "tls-test-token"),
            ("Auth__BearerTokens__0__Scopes__0", "read-counters"));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
            };
            using var client = new HttpClient(handler);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await WaitForResponseAsync(
                process,
                client,
                $"https://127.0.0.1:{port}/health",
                stdoutTask,
                stderrTask,
                timeout.Token).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        var output = await CombinedOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        output.Should().NotContain("Refusing to start");
    }

    [Fact]
    public async Task TrustedProxy_OnlyAcceptsForwardedHttpsFromConfiguredNetwork()
    {
        await using var trustedFactory = CreateProxyFactory(IPAddress.Parse("10.1.2.3"));
        using var trustedClient = trustedFactory.CreateClient();

        using var forwardedHttps = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        forwardedHttps.Headers.Add("X-Forwarded-Proto", "https");
        var trustedResponse = await trustedClient.SendAsync(forwardedHttps).ConfigureAwait(false);
        trustedResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "trusted forwarded HTTPS should reach bearer authentication");

        var missingProtoResponse = await trustedClient.GetAsync("/mcp").ConfigureAwait(false);
        missingProtoResponse.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a trusted proxy must still attest that the original request used HTTPS");

        await using var untrustedFactory = CreateProxyFactory(IPAddress.Parse("192.0.2.10"));
        using var untrustedClient = untrustedFactory.CreateClient();
        using var spoofedHttps = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        spoofedHttps.Headers.Add("X-Forwarded-Proto", "https");
        var untrustedResponse = await untrustedClient.SendAsync(spoofedHttps).ConfigureAwait(false);
        untrustedResponse.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "X-Forwarded-Proto from an untrusted origin must be ignored");
    }

    private static WebApplicationFactory<Program> CreateProxyFactory(IPAddress remoteIp)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ASPNETCORE_URLS", "http://0.0.0.0:5130");
            builder.UseSetting("MCP_TRUSTED_PROXY_CIDRS", "10.0.0.0/8");
            builder.UseSetting("Auth:BearerTokens:0:Name", "proxy-test");
            builder.UseSetting("Auth:BearerTokens:0:Token", "proxy-test-token");
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "read-counters");
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIp)));
        });

    private static Process StartServer(
        string serverDll,
        int port,
        params (string Name, string Value)[] environment)
        => StartServer(serverDll, port, "http", environment);

    private static Process StartServer(
        string serverDll,
        int port,
        string scheme,
        params (string Name, string Value)[] environment)
        => StartServer(serverDll, port, scheme, "0.0.0.0", environment);

    private static Process StartServer(
        string serverDll,
        int port,
        string scheme,
        string host,
        params (string Name, string Value)[] environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(serverDll);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"{scheme}://{host}:{port}");

        foreach (var key in new[]
        {
            "MCP_ALLOW_INSECURE_HTTP",
            "MCP_TRUSTED_PROXY_CIDRS",
            "MCP_TLS_CERTIFICATE_PEM",
            "MCP_TLS_PRIVATE_KEY_PEM",
            "MCP_BEARER_TOKEN",
        })
        {
            startInfo.Environment.Remove(key);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the MCP server process.");
    }

    private static async Task<string> CombinedOutputAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new StringBuilder(stdout.Length + stderr.Length)
            .Append(stdout)
            .Append(stderr)
            .ToString();
    }

    private static async Task<HttpResponseMessage> WaitForResponseAsync(
        Process process,
        HttpClient client,
        string url,
        Task<string> stdoutTask,
        Task<string> stderrTask,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (process.HasExited)
            {
                var output = await CombinedOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw new Xunit.Sdk.XunitException(
                    $"MCP server exited with code {process.ExitCode} before {url} became reachable.{Environment.NewLine}{output}");
            }

            try
            {
                return await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindServerDll()
    {
        var here = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(here);
        while (current is not null &&
            !File.Exists(Path.Combine(current.FullName, "DotnetDiagnostics.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            return null;
        }

        var tfm = Path.GetFileName(here);
        var configuration = Path.GetFileName(Path.GetDirectoryName(here)!);
        var candidate = Path.Combine(
            current.FullName,
            "src",
            "DotnetDiagnostics.Mcp",
            "bin",
            configuration,
            tfm,
            "DotnetDiagnostics.Mcp.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = remoteIp;
                    await continuation().ConfigureAwait(false);
                });
                next(app);
            };
    }
}

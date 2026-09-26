using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Mcp.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class McpRequestFramingTests
{
    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    [InlineData(1048576, false)]
    [InlineData(1048577, false)]
    public void CaptureFrame_EnforcesExactEncodedSize(int bytes, bool accepted)
    {
        var frame = Frame(bytes, capture: true);
        var action = () => McpRequestFraming.Validate(frame);
        if (accepted) action.Should().NotThrow();
        else action.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(65537, true)]
    [InlineData(1048576, true)]
    [InlineData(1048577, false)]
    public void NonCaptureFrame_PreservesOneMiBBudget(int bytes, bool accepted)
    {
        var frame = Frame(bytes, capture: false);
        var action = () => McpRequestFraming.Validate(frame);
        if (accepted) action.Should().NotThrow();
        else action.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("captureAction")]
    [InlineData("capture\\u0041ction")]
    public void EscapedPropertyAndPadding_CannotBypassAdmission(string property)
    {
        var json = """{"params":{"name":"get_bytes","arguments":{""" +
            "\"" + property + "\":\"upload-chunk\"}}}" + new string(' ', 65536);
        var action = () => McpRequestFraming.Validate(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void AnotherTool_WithSimilarlyNamedData_KeepsNonCaptureBudget()
    {
        var frame = Encoding.UTF8.GetBytes(
            """{"params":{"name":"other_tool","arguments":{"captureAction":"data"}}}""" + new string(' ', 65536));
        var action = () => McpRequestFraming.Validate(frame);
        action.Should().NotThrow();
    }

    [Fact]
    public async Task Stdio_SeparatesFramesAndAcceptsFinalFrameWithoutNewline()
    {
        var first = Frame(65536, capture: true);
        var second = Frame(100000, capture: false);
        using var source = new MemoryStream([.. first, .. second.AsSpan(0, second.Length - 1).ToArray()]);
        using var framed = new BoundedMcpInputStream(source);
        using var destination = new MemoryStream();
        await framed.CopyToAsync(destination);
        destination.ToArray().Should().Equal(source.ToArray());
    }

    [Theory]
    [InlineData(true, 65537)]
    [InlineData(false, 1048577)]
    public async Task Stdio_RejectsBeforeExposingAnyBytes(bool capture, int bytes)
    {
        using var source = new MemoryStream(Frame(bytes, capture));
        using var framed = new BoundedMcpInputStream(source);
        var target = new byte[1];
        var read = async () => await framed.ReadAsync(target);
        await read.Should().ThrowAsync<InvalidDataException>();
        target[0].Should().Be(0);
    }

    [Fact]
    public async Task Stdio_CountsActualBytesWhenNoNewlineArrives()
    {
        using var source = new MemoryStream(new byte[McpRequestFraming.MaximumFrameBytes + 1]);
        using var framed = new BoundedMcpInputStream(source);
        var read = async () => await framed.ReadAsync(new byte[1]);
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(true, 65536, true)]
    [InlineData(true, 65537, false)]
    [InlineData(false, 1048576, true)]
    [InlineData(false, 1048577, false)]
    public async Task Http_UnknownContentLength_IsBoundedBeforeNextMiddleware(bool capture, int bytes, bool accepted)
    {
        var body = Frame(bytes, capture);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/mcp";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength.Should().BeNull();
        var called = false;
        var middleware = new McpRequestFramingMiddleware(async http =>
        {
            called = true;
            using var received = new MemoryStream();
            await http.Request.Body.CopyToAsync(received);
            received.ToArray().Should().Equal(body);
        });
        await middleware.InvokeAsync(context);
        called.Should().Be(accepted);
        context.Response.StatusCode.Should().Be(accepted ? 200 : 413);
    }

    internal static byte[] Frame(int bytes, bool capture)
    {
        var prefix = capture
            ? """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_bytes","arguments":{"kind":"captures","captureAction":"upload-chunk"}}}"""
            : """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
        return Encoding.UTF8.GetBytes(prefix + new string(' ', bytes - prefix.Length - 1) + "\n");
    }
}

[Collection(nameof(EnvSerial))]
public sealed class McpRequestFramingHttpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualHttpPipeline_RejectsOversizedCaptureBeforeSdk(bool knownLength)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "framing-test");
            builder.UseSetting("Auth:BearerTokens:0:Token", "framing-test-value");
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "root");
            builder.UseSetting("Orchestrator:Enabled", "false");
            builder.UseSetting("AzureDiscovery:Enabled", "false");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "framing-test-value");
        var bytes = McpRequestFramingTests.Frame(65537, capture: true);
        using HttpContent content = knownLength
            ? new ByteArrayContent(bytes)
            : new UnknownLengthContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/mcp", content);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeLessThan(1024);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

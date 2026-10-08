using System.Text;
using DotnetDiagnostics.Mcp.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class BoundedMcpInputStreamTests
{
    [Theory]
    [InlineData(65537, "64 KiB")]
    [InlineData(1024 * 1024 + 1, "1 MiB")]
    public async Task OversizedFrame_IsRejectedAndLoggedBeforeAnyByteIsReturned(int frameBytes, string reason)
    {
        var frame = McpRequestFramingTests.Frame(frameBytes, capture: true);
        using var input = new MemoryStream(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\r\n").Concat(frame).ToArray());
        var provider = new CapturingProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        await using var stream = new BoundedMcpInputStream(input, () => factory);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        (await reader.ReadLineAsync()).Should().Contain("notifications/initialized");
        var act = async () => await reader.ReadLineAsync();

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage($"*{reason}*");
        provider.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Message.Contains(reason));
    }

    [Fact]
    public async Task EndOfInput_IsLogged()
    {
        using var input = new MemoryStream();
        var provider = new CapturingProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        await using var stream = new BoundedMcpInputStream(input, () => factory);

        (await stream.ReadAsync(new byte[16])).Should().Be(0);

        provider.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Information && entry.Message.Contains("end of stream"));
    }

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

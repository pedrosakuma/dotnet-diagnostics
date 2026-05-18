using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddSingleton<IProcessDiscovery, LocalProcessDiscovery>();
builder.Services.AddSingleton<ICapabilityDetector, CapabilityDetector>();
builder.Services.AddSingleton<ICounterCollector, EventPipeCounterCollector>();
builder.Services.AddSingleton<ICpuSampler, EventPipeCpuSampler>();
builder.Services.AddSingleton<IExceptionCollector, EventPipeExceptionCollector>();
builder.Services.AddSingleton<IGcCollector, EventPipeGcCollector>();
builder.Services.AddSingleton<IEventSourceCollector, EventPipeEventSourceCollector>();
builder.Services.AddSingleton<IProcessDumper, DiagnosticsClientDumper>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<DiagnosticTools>();

var app = builder.Build();

var token = BearerTokenOptions.LoadOrGenerate(app.Logger);
app.UseMiddleware<BearerTokenMiddleware>(token);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();

namespace DotnetDiagnosticsMcp.Server
{
    public partial class Program;
}

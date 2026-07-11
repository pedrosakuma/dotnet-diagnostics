using System.Globalization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    private static async Task<CliCommandResult> InspectHeapAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        // Source was already validated before the host was built; re-resolve to dispatch.
        TryResolveHeapSource(options, out var source, out _);

        var inspector = services.GetRequiredService<IDumpInspector>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var allowlist = services.GetRequiredService<SymbolServerAllowlist>();
        var topTypes = options.TopTypes ?? 20;
        var retentionLimit = options.RetentionPathLimit ?? 8;

        if (source == "dump")
        {
            var dumpResult = await HeapInspectionUseCases.InspectDump(
                inspector, handles, allowlist,
                // The CLI runs as the local operator: it owns any remote symbol fetch, so it gets the
                // same posture the stdio root accessor gives the MCP server (symbols-remote granted).
                principalAllowsSymbolsRemote: true,
                options.DumpFile!, topTypes, options.IncludeRetentionPaths, retentionLimit,
                options.IncludeStaticFields, options.IncludeDelegateTargets, options.IncludeDuplicateStrings,
                NullIfEmpty(options.SymbolPath), deprecation: null, cancellationToken).ConfigureAwait(false);

            return BuildResult<DumpInspection>(dumpResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
        }

        var resolver = services.GetRequiredService<IProcessContextResolver>();

        if (source == "gcdump")
        {
            var collector = services.GetRequiredService<IGcDumpHeapSnapshotCollector>();
            var gcResult = await HeapInspectionUseCases.InspectGcDump(
                collector, handles, resolver,
                options.Pid, topTypes, timeout: null, options.ExportTrace, cancellationToken).ConfigureAwait(false);

            return BuildResult<LiveHeapInspection>(gcResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
        }

        var liveResult = await HeapInspectionUseCases.InspectLiveHeap(
            inspector, handles, resolver, allowlist,
            principalAllowsSymbolsRemote: true,
            options.Pid, topTypes, options.IncludeRetentionPaths, retentionLimit,
            options.IncludeStaticFields, options.IncludeDelegateTargets, options.IncludeDuplicateStrings,
            NullIfEmpty(options.SymbolPath), deprecation: null, cancellationToken).ConfigureAwait(false);

        return BuildResult<LiveHeapInspection>(liveResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
    }

    private static async Task<CliCommandResult> DumpAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var dumper = services.GetRequiredService<IProcessDumper>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var dumpType = ProcessDumpType.Mini;
        if (options.DumpType is not null && TryParseDumpType(options.DumpType, out var parsedDumpType))
        {
            dumpType = parsedDumpType;
        }

        // --out is wired in as the artifact root for this invocation (CliHost), so the dump lands
        // directly there; pass a null sub-path. The CLI is a local operator and carries no bearer
        // principal, so audit-log fields are empty.
        var result = await ProcessDumpUseCases.CollectProcessDump(
            dumper, resolver, logger: null, principalName: null,
            options.Pid, dumpType, outputDirectory: null, options.Confirm, cancellationToken).ConfigureAwait(false);

        // The Core confirmation-required preview names the MCP tool (collect_process_dump) and
        // confirm=true in its summary/message; rewrite it to CLI vocabulary before rendering (#301).
        result = CliHintProjection.RewriteDumpPreview(result);

        // #387: disclose the resolved artifact directory the dump WOULD be written to *before* it is
        // written, so the operator sees the destination on the --confirm preview (not only in the
        // success envelope). The root is the CLI's sandbox (dump --out, or the temp / MCP_ARTIFACT_ROOT
        // default).
        var artifactRoot = services.GetRequiredService<DotnetDiagnostics.Core.Artifacts.IArtifactRootProvider>().Root;

        return BuildResult<DumpToolResult>(result, (sb, data) =>
        {
            if (data.Dump is { } dump)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  file  : {dump.FilePath}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  size  : {dump.FileSizeBytes:N0} bytes");
            }
            else if (string.Equals(data.Kind, DumpToolResultKinds.ConfirmationRequired, StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  would write to : {artifactRoot}");
                sb.AppendLine("  re-run with --confirm to write the dump.");
            }
        });
    }

    /// <summary>
    /// The <c>query</c> drill-down command in the one-shot CLI. Drill-down handles are MCP-session
    /// scoped and the one-shot CLI is stateless (per the #286 persistence decision: cheap inline
    /// summaries only, no handle store survives the process), so there is nothing to query in a
    /// follow-up invocation. Returns a structured <c>NotSupported</c> envelope (exit 1) that redirects
    /// the operator to the <c>session</c> REPL — the one place where a collected handle lives long
    /// enough to <c>query --handle &lt;id&gt; --view &lt;view&gt;</c>.
    /// </summary>
    private static async Task<CliCommandResult> GetBytesAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        // Validated before the host was built; --out is guaranteed present and is the destination file.
        var outputPath = options.OutDir!;
        // The CLI runs as the local operator and carries no bearer principal; grant the literal
        // 'module-bytes-read' scope the same way the stdio root accessor does for the MCP server.
        const bool principalAllowsLiteralScope = true;
        // Use the largest chunk the readers permit so a big artifact takes as few re-attaches as possible.
        var maxBytes = FileChunkReader.MaxChunkBytes;

        if (options.Kind == "dump")
        {
            var dumpSource = services.GetRequiredService<IDumpByteSource>();
            // CliHost pointed the artifact root at the dump file's directory, so a relative file name
            // resolves under it (SafeArtifactPath rejects anything outside the root).
            var dumpResult = await ByteMaterializationUseCases.MaterializeDumpBytes(
                dumpSource, principalAllowsLiteralScope,
                Path.GetFileName(options.DumpFile!), outputPath, maxBytes,
                logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
            return WrapMaterialization(dumpResult);
        }

        if (options.Kind == "trace")
        {
            var traceSource = services.GetRequiredService<IDumpByteSource>();
            // CliHost pinned the artifact root at the trace file's directory; a relative file name
            // resolves under it (SafeArtifactPath rejects anything outside the root).
            var traceResult = await ByteMaterializationUseCases.MaterializeTraceBytes(
                traceSource, principalAllowsLiteralScope,
                Path.GetFileName(options.DumpFile!), outputPath, maxBytes,
                logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
            return WrapMaterialization(traceResult);
        }

        var moduleSource = services.GetRequiredService<IModuleByteSource>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var moduleResult = await ByteMaterializationUseCases.MaterializeModuleBytes(
            moduleSource, resolver, principalAllowsLiteralScope,
            options.Mvid!, options.Asset ?? "pe", options.Pid, outputPath, maxBytes,
            logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
        return WrapMaterialization(moduleResult);
    }

    private static CliCommandResult WrapMaterialization(DiagnosticResult<ByteMaterialization> result)
    {
        return BuildResult<ByteMaterialization>(result, static (sb, data) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  asset  : {data.Asset}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  source : {data.SourcePath}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  output : {data.OutputPath}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  size   : {data.TotalBytes:N0} bytes");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  sha256 : {data.Sha256}");
            if (!string.IsNullOrWhiteSpace(data.CompanionPdbPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  pdb    : {data.CompanionPdbPath}");
            }
        });
    }

}

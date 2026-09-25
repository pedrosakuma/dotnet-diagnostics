using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    internal static async Task<CliCommandResult> PersistAsync(
        IServiceProvider services, CliOptions options,
        Func<CancellationToken, Task<CliCommandResult>> collect, CancellationToken cancellationToken)
    {
        CliCommandResult? collected = null;
        try
        {
            var service = CliDurableCaptures.For(services).Get(options.CaptureRoot);
            var kind = options.Command == "inspect-heap" ? "heap-snapshot" : options.Kind!;
            var persisted = await service.CaptureAsync<object>(
                $"{options.Command} {options.Kind ?? (options.Sources.Count > 0 ? options.Sources[0] : "live")}",
                kind, CliCaptureRootProvider.CurrentAccess(), async ct =>
                {
                    collected = await collect(ct).ConfigureAwait(false);
                    return collected.CaptureProjection?.Invoke()
                        ?? throw new InvalidOperationException("This command does not expose a typed diagnostic capture result.");
                }, cancellationToken).ConfigureAwait(false);

            CliCaptureMetadata? metadata = null;
            if (persisted.Capture is { } capture)
            {
                try
                {
                    metadata = await CliDurableCaptures.For(services).DescribeMetadataAsync(options.CaptureRoot, capture, CancellationToken.None).ConfigureAwait(false);
                }
                catch (CaptureStoreException ex)
                {
                    persisted = persisted with { Error = persisted.Error ?? new(ex.Code.ToString(), ex.Message) };
                }
            }
            persisted = CliHintProjection.Project(persisted);
            var result = collected ?? BuildResult(persisted, SerializeQuery);
            var failureNotice = persisted.Error is { } error && !result.IsError
                ? $"{Environment.NewLine}ERROR: {error.Kind}: {error.Message}"
                : string.Empty;
            return result with
            {
                IsError = persisted.IsError,
                Cancelled = persisted.Cancelled,
                Envelope = persisted,
                Human = result.Human + failureNotice,
                RenderHumanForBoundTarget = result.RenderHumanForBoundTarget is { } render
                    ? pid => render(pid) + failureNotice
                    : null,
                Capture = persisted.Capture,
                CaptureViews = metadata?.Views,
                CaptureCompositions = metadata?.Compositions,
                CaptureRecordStreams = metadata?.RecordStreams,
            };
        }
        catch (CaptureStoreException ex)
        {
            return CaptureFailure(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail("Durable capture storage could not be accessed.", "CaptureStorageFailure",
                $"{ex.Message} Check --capture-root and its filesystem permissions.");
        }
    }

    private static async Task<CliCommandResult> CapturesAsync(
        IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var service = CliDurableCaptures.For(services).Get(options.CaptureRoot);
            var access = CliCaptureRootProvider.CurrentAccess();
            switch (options.CaptureAction)
            {
                case "list":
                    var page = await service.ListAsync(access, options.PageSize ?? 100,
                        options.AfterCaptureId, cancellationToken).ConfigureAwait(false);
                    return BuildResult(DiagnosticResult.Ok(page, "Durable captures for the current local OS owner."), SerializeQuery);
                case "show":
                    var info = await service.DescribeAsync(options.CaptureId!, access, cancellationToken).ConfigureAwait(false);
                    var metadata = await CliDurableCaptures.For(services).DescribeMetadataAsync(options.CaptureRoot, info, cancellationToken).ConfigureAwait(false);
                    return BuildResult(DiagnosticResult.Ok(info, $"Capture {info.CaptureId}."), SerializeQuery) with
                    {
                        Capture = info,
                        CaptureViews = metadata.Views,
                        CaptureCompositions = metadata.Compositions,
                        CaptureRecordStreams = metadata.RecordStreams,
                    };
                case "delete":
                    await service.DeleteAsync(options.CaptureId!, access, cancellationToken).ConfigureAwait(false);
                    return BuildResult(DiagnosticResult.Ok(new { options.CaptureId, Deleted = true },
                        $"Deleted capture {options.CaptureId}."), SerializeQuery);
                case "recover":
                    var recovered = await service.RecoverAsync(options.CaptureId!, access, cancellationToken).ConfigureAwait(false);
                    return BuildResult(DiagnosticResult.Ok(recovered,
                        $"Recovered into new derived capture {recovered.CaptureId}; original evidence was not modified."), SerializeQuery);
                default:
                    return Fail("Unknown capture action.", "InvalidArgument", "Use captures list, show, delete, or recover.");
            }
        }
        catch (CaptureStoreException ex)
        {
            return CaptureFailure(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail("Durable capture storage could not be accessed.", "CaptureStorageFailure",
                $"{ex.Message} Check --capture-root and its filesystem permissions.");
        }
    }

    private static async Task<CliCommandResult> QueryCaptureAsync(
        IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var service = CliDurableCaptures.For(services).Get(options.CaptureRoot);
            var access = CliCaptureRootProvider.CurrentAccess();
            if (options.View == "records")
            {
                var page = await service.QueryRecordsAsync(options.CaptureId!,
                    CaptureRecordsQuery(options.ArtifactId!, options), access, cancellationToken).ConfigureAwait(false);
                return BuildResult(DiagnosticResult.Ok(page, "Bounded durable records; use nextAfterRecordId to continue."), SerializeQuery);
            }

            var opened = await service.OpenAsync(options.CaptureId!, options.ArtifactId!, access, cancellationToken).ConfigureAwait(false);
            await service.AuthorizeViewAsync(opened.Handle.Id, options.View!, access, cancellationToken).ConfigureAwait(false);
            var result = await QuerySession(services, options with
            {
                CaptureId = null,
                ArtifactId = null,
                Handle = opened.Handle.Id,
            }, cancellationToken).ConfigureAwait(false);
            return result with
            {
                Capture = opened.Capture,
                CaptureViews = new Dictionary<string, IReadOnlyList<string>>
                {
                    [opened.Artifact.ArtifactId] = opened.SupportedViews,
                },
                CaptureRecordStreams = opened.RecordStream is { } stream
                    ? new Dictionary<string, DotnetDiagnostics.Core.UseCases.DurableCaptureRecordStreamInfo>
                    {
                        [opened.Artifact.ArtifactId] = stream,
                    }
                    : null,
            };
        }
        catch (CaptureStoreException ex)
        {
            return CaptureFailure(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail("Durable capture query failed.", "CaptureStorageFailure",
                $"{ex.Message} Check --capture-root and the capture/artifact IDs.");
        }
    }

    private static CaptureRecordQuery CaptureRecordsQuery(string artifactId, CliOptions options)
        => new(artifactId, ParseRecordTime(options.RecordFrom), ParseRecordTime(options.RecordTo),
            options.ThreadId, options.Categories.Count > 0 ? options.Categories[0] : null,
            options.RecordName, options.AfterRecordId ?? 0, options.PageSize ?? 100);

    private static CliCommandResult CaptureFailure(CaptureStoreException exception)
    {
        var action = exception.Code switch
        {
            CaptureErrorCode.Incomplete => "Use captures recover --capture-id <id> to create a derived package.",
            CaptureErrorCode.NotFound => "Use captures list with the same --capture-root and current OS owner; copy exact capture/artifact IDs.",
            CaptureErrorCode.Forbidden => "Use the owning local OS identity and only supported offline views; historical IDs never allow live attach.",
            CaptureErrorCode.UnsupportedFormat => "Use a compatible CLI version; do not edit the capture package.",
            CaptureErrorCode.CorruptPackage => "Restore a verified backup or explicitly recover interrupted evidence; do not edit package files.",
            _ => "Check --capture-root and captures show --capture-id <id>; keep the original evidence intact.",
        };
        return Fail("Durable capture operation failed.", exception.Code.ToString(), $"{exception.Message} {action}");
    }
}

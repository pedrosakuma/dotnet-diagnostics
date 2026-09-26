using System.Security.Cryptography;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Cli;

internal sealed record CliPortableCaptureOutcome(
    PortableOperationKey Operation,
    PortableExportResult? Export,
    bool OutputPublished,
    PortableImportResult? Import,
    IReadOnlyList<CaptureInfo> Captures,
    PortableFailure? Failure,
    PortableFailure? ReceiptFailure,
    PortableFailure? CleanupFailure);

internal static partial class CliCommands
{
    private const long MaximumPortableFileBytes = 512L * 1024 * 1024;
    internal const string ImportWorkerEnvironment = "DOTNET_DIAGNOSTICS_IMPORT_WORKER";
    internal const string ImportLibraryEnvironment = "DOTNET_DIAGNOSTICS_SQLITE_LIBRARY";

    private static async Task<CliCommandResult> PortableCaptureAsync(
        IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(600));
        cancellationToken = deadline.Token;
        var operation = new PortableOperationKey(options.OperationId ?? Guid.NewGuid().ToString("N"),
            ParseRecordTime(options.RequestedUtc) ?? DateTimeOffset.UtcNow);
        PortableCaptureUseCases? portable = null;
        PortableExportResult? exported = null;
        PortableImportResult? imported = null;
        PortableFailure? failure = null, receiptFailure = null, cleanupFailure = null;
        var captures = new List<CaptureInfo>();
        var cancelled = false;
        var outputPublished = false;
        string? temporary = null;
        var access = CliCaptureRootProvider.CurrentAccess();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = new CliCaptureRootProvider(options.CaptureRoot);
            var worker = options.CaptureAction == "import" ? ConfiguredImportWorker() : null;
            portable = new PortableCaptureUseCases(new SqliteCaptureStore(provider),
                static (_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; },
                importWorker: worker);
            if (options.CaptureAction == "export")
            {
                var destination = PortableFilePath(options.CaptureFile!);
                var managedRoot = Path.Combine(provider.Root, "captures");
                if (IsWithin(destination, managedRoot))
                    throw new CaptureStoreException(CaptureErrorCode.UnsafePath, "OutputInsideCaptureStore");
                if (Path.Exists(destination))
                    throw new CaptureStoreException(CaptureErrorCode.InvalidInput, "OutputAlreadyExists");
                var pendingPath = Path.Combine(Path.GetDirectoryName(destination)!, $".ddcapture-{Guid.NewGuid():N}.pending");
                var selections = options.CaptureEntries.Select(static value =>
                {
                    var separator = value.IndexOf('=');
                    return separator < 0 ? new CaptureExportSelection(value, null)
                        : new CaptureExportSelection(value[..separator], value[(separator + 1)..]);
                }).ToArray();
                var settings = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                    Options = FileOptions.Asynchronous, BufferSize = 64 * 1024,
                };
                if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(pendingPath, settings))
                {
                    temporary = pendingPath;
                    using var output = new PortableOutputStream(file);
                    exported = await portable.ExportAsync(new(operation, selections), output, access, cancellationToken).ConfigureAwait(false);
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                    file.Position = 0;
                    var measured = await MeasurePortableFileAsync(file, cancellationToken).ConfigureAwait(false);
                    if (measured.Bytes != exported.ArchiveBytes || measured.Hash != exported.ArchiveSha256)
                        throw new CaptureStoreException(CaptureErrorCode.CorruptPackage, "ExportOutputIntegrity");
                }
                cancellationToken.ThrowIfCancellationRequested();
                _ = PortableFilePath(destination);
                File.Move(temporary, destination, overwrite: false);
                temporary = null;
                outputPublished = true;
            }
            else if (options.CaptureAction == "import-result")
            {
                imported = await portable.GetImportResultAsync(operation, access, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var source = PortableFilePath(options.CaptureFile!);
                var info = new FileInfo(source);
                if (!info.Exists || info.Length < 22 ||
                    (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                    throw new CaptureStoreException(CaptureErrorCode.InvalidInput, "ExpectedNonemptyBundleFile");
                CheckPortableFileBytes(info.Length);
                await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var measured = await MeasurePortableFileAsync(file, cancellationToken).ConfigureAwait(false);
                file.Position = 0;
                imported = await portable.ImportAsync(new(operation, measured.Bytes, measured.Hash), file, access,
                    static (_, _, _, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is CaptureStoreException or IOException or UnauthorizedAccessException or
                                   OperationCanceledException or ArgumentException or NotSupportedException)
        {
            cancelled = ex is OperationCanceledException;
            failure = PortableFailureFor(ex);
            if (portable is not null && options.CaptureAction is "import" or "import-result")
            {
                try
                {
                    using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    imported = await portable.GetImportResultAsync(operation, access, lookup.Token).ConfigureAwait(false);
                }
                catch (Exception receiptError) when (receiptError is CaptureStoreException or IOException or
                    UnauthorizedAccessException or OperationCanceledException)
                {
                    receiptFailure = PortableFailureFor(receiptError);
                }
            }
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    cleanupFailure = PortableFailureFor(ex);
                }
            }
        }

        if (imported is not null)
        {
            try
            {
                using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var durable = CliDurableCaptures.For(services).Get(options.CaptureRoot);
                foreach (var entry in imported.Entries.Where(static entry => entry.State == PortableEntryState.Published))
                    captures.Add(await durable.DescribeAsync(entry.Mapping!.LocalCaptureId, access, lookup.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is CaptureStoreException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                receiptFailure = PortableFailureFor(ex);
            }
        }
        cancelled |= imported?.Cancelled == true;
        failure ??= imported?.Failure;
        var outcome = new CliPortableCaptureOutcome(operation, exported, outputPublished, imported, captures, failure, receiptFailure, cleanupFailure);
        return RenderPortableCaptureOutcome(outcome, cancelled);
    }

    internal static CliCommandResult RenderPortableCaptureOutcome(CliPortableCaptureOutcome outcome, bool cancelled)
    {
        var failure = outcome.Failure ?? outcome.Import?.Failure ?? outcome.ReceiptFailure ?? outcome.CleanupFailure;
        var isError = failure is not null || outcome.Import?.Complete == false;
        cancelled |= outcome.Import?.Cancelled == true;
        var envelope = new DiagnosticResult<CliPortableCaptureOutcome>(
            $"Operation {outcome.Operation.Id}; requestedUtc={outcome.Operation.RequestedUtc:O}. " +
                (isError ? "Incomplete; inspect mappings and failure details." : "Portable capture operation completed."), [])
        {
            Data = outcome, Cancelled = cancelled,
            Error = isError ? new(failure?.Code.ToString() ?? "Incomplete",
                failure?.Reason == "ImportWorkerUnavailable"
                    ? $"Import requires trusted absolute {ImportWorkerEnvironment} and {ImportLibraryEnvironment} files on a supported Linux host; see the CLI reference."
                    : "Portable capture operation did not fully complete. Inspect per-entry outcomes and retain the operation key; do not assume nothing published.") : null,
        };
        // Human output also retains the complete structured outcome, including partial publication.
        var human = new System.Text.StringBuilder();
        SerializeQuery(human, envelope);
        return new(isError, cancelled, envelope, human.ToString());
    }

    private static PortableCaptureImportWorker ConfiguredImportWorker()
        => ConfiguredImportWorker(Environment.GetEnvironmentVariable(ImportWorkerEnvironment),
            Environment.GetEnvironmentVariable(ImportLibraryEnvironment));

    internal static PortableCaptureImportWorker ConfiguredImportWorker(string? executable, string? library)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(library) ||
            !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(library) ||
            !File.Exists(executable) || !File.Exists(library))
            throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat,
                "ImportWorkerUnavailable: configure trusted absolute DOTNET_DIAGNOSTICS_IMPORT_WORKER and DOTNET_DIAGNOSTICS_SQLITE_LIBRARY files on a supported Linux host.");
        _ = PortableFilePath(executable);
        _ = PortableFilePath(library);
        return new(executable, library);
    }

    private static PortableFailure PortableFailureFor(Exception exception)
    {
        var code = exception is CaptureStoreException store ? store.Code :
            exception is OperationCanceledException ? CaptureErrorCode.Incomplete : CaptureErrorCode.StorageFailure;
        var reason = exception is CaptureStoreException ? exception.Message.Split(':', 2)[0] :
            exception is OperationCanceledException ? "Cancelled" : "LocalFileOperationFailed";
        if (reason.Length > 128 || !reason.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_'))
            reason = code.ToString();
        return new(code, reason, null, exception.Data["PortableLimit"] as string,
            exception.Data["PortableObserved"] is long observed ? observed : null,
            exception.Data["PortableMaximum"] is long maximum ? maximum : null);
    }

    private static string PortableFilePath(string value)
    {
        var full = Path.GetFullPath(value);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget is not null) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CaptureStoreException(CaptureErrorCode.UnsafePath, "LocalFileLink");
        return full;
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static async Task<(long Bytes, string Hash)> MeasurePortableFileAsync(FileStream stream, CancellationToken token)
    {
        CheckPortableFileBytes(stream.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            CheckPortableFileBytes(total);
            hash.AppendData(buffer, 0, read);
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    internal static void CheckPortableFileBytes(long observed)
    {
        if (observed <= MaximumPortableFileBytes) return;
        var error = new CaptureStoreException(CaptureErrorCode.CapacityExceeded, "MaxArchiveBytes");
        error.Data["PortableLimit"] = "MaxArchiveBytes";
        error.Data["PortableObserved"] = observed;
        error.Data["PortableMaximum"] = MaximumPortableFileBytes;
        throw error;
    }

    private sealed class PortableOutputStream(FileStream file) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        public override void Flush() => file.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => file.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            file.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            await file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        private void Check(int count)
        {
            CheckPortableFileBytes(checked(file.Position + count));
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

using System.Buffers.Binary;
using System.Diagnostics;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record SqliteRebuildRequest(string Executable, string SqliteLibrary, string PrivateDirectory,
    long MaxDatabaseBytes, long RemainingVmInstructions, CaptureWorkerLimits Limits)
{
    internal Action<int>? BeforeInput { get; init; }
    internal Action? AfterExit { get; init; }
}

internal static partial class IsolatedCaptureWorker
{
    internal static Task EnsureAvailableAsync(PortableCaptureImportWorker worker, string directory, CancellationToken token)
    {
        worker.Validate();
        return Task.Run(() =>
        {
            var nonce = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(worker.Executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = directory
            };
            start.Environment.Clear();
            foreach (var argument in new[] { nonce, worker.SqliteLibrary, directory, "-", "-", "--available" })
                start.ArgumentList.Add(argument);
            var response = RunProtocol(start, nonce, new(), "GO\n"u8.ToArray(),
                static (source, ct) => ReadBoundedAsync(source, 128, false, ct), token);
            if (response.Result != "AVAILABLE 1\n") throw Unsupported("ImportWorkerUnavailable");
        }, CancellationToken.None);
    }

    internal static Task<long> RebuildValidatedAsync(SqliteRebuildRequest request, Stream validatedFrames,
        CancellationToken cancellationToken)
    {
        request.Limits.Validate();
        if (request.MaxDatabaseBytes is < 65536 or > 256L * 1024 * 1024 ||
            request.RemainingVmInstructions is < 1 or > 200_000_000)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Invalid remaining rebuild resource budget.");
        if (!OperatingSystem.IsLinux() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw Unsupported("WorkerPlatformUnavailable");
        foreach (var path in new[] { request.Executable, request.SqliteLibrary, request.PrivateDirectory })
        {
            if (!Path.IsPathFullyQualified(path) || path.Length > 2048 || path.Contains('\0'))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Rebuild assets require bounded trusted paths.");
            CapturePackage.RejectLinks(path);
        }
        if ((File.GetUnixFileMode(request.PrivateDirectory) &
             (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
            Directory.EnumerateFileSystemEntries(request.PrivateDirectory).Any())
            throw Unsupported("WorkerRebuildStagingNotEmptyOrPrivate");
        return Task.Run(() =>
        {
            var nonce = Guid.NewGuid().ToString("N");
            var database = Path.Combine(request.PrivateDirectory, CapturePackage.Database);
            var start = new ProcessStartInfo(request.Executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = request.PrivateDirectory
            };
            start.Environment.Clear();
            foreach (var argument in new[] { nonce, request.SqliteLibrary, request.PrivateDirectory,
                new Uri(database).AbsoluteUri, database, "--rebuild" }) start.ArgumentList.Add(argument);
            var result = RunProtocol(start, nonce, request.Limits, Send, Receive, cancellationToken,
                request.BeforeInput, request.AfterExit);
            if (Directory.EnumerateFileSystemEntries(request.PrivateDirectory).Any(path => path != database))
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Rebuild.UnexpectedMember");
            using var output = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateSqliteHeader(output, request.MaxDatabaseBytes);
            return result.Result;
        }, CancellationToken.None);

        async Task Send(Stream destination, CancellationToken token)
        {
            var header = new byte[19];
            "GO\n"u8.CopyTo(header);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(3), request.MaxDatabaseBytes);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(11), request.RemainingVmInstructions);
            await destination.WriteAsync(header, token).ConfigureAwait(false);
            var buffer = new byte[PortableBounds.BufferBytes];
            long sent = header.Length;
            while (true)
            {
                var read = await validatedFrames.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                sent = checked(sent + read);
                PortableBounds.Check("WorkerWireBytes", sent, 512L * 1024 * 1024);
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }

        async Task<long> Receive(Stream source, CancellationToken token)
        {
            var header = new byte[4];
            await source.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > 128) throw CorruptAdmission("Rebuild.Frame");
            var bytes = new byte[length];
            await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (bytes[0] == 127)
            {
                var reason = CapturePackage.Utf8.GetString(bytes, 1, bytes.Length - 1);
                if (reason.Length == 0 || reason.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '.'))
                    throw CorruptAdmission("Rebuild.Error");
                if (reason.StartsWith("Limit.", StringComparison.Ordinal)) throw Limit(reason);
                throw CorruptAdmission(reason);
            }
            if (length != 9 || bytes[0] != 6) throw CorruptAdmission("Rebuild.Frame");
            var instructions = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(1));
            if (instructions < 0 || instructions > request.RemainingVmInstructions ||
                await source.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                throw CorruptAdmission("Rebuild.Completion");
            return instructions;
        }
    }
}

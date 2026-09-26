using System.Globalization;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    internal static bool TryValidateCaptures(CliOptions options, out string? error)
    {
        error = null;
        var portable = options.Command == "captures" && options.CaptureAction is "export" or "import" or "import-result";
        if (!portable && (options.CaptureEntries.Count != 0 || options.CaptureFile is not null ||
                          options.OperationId is not null || options.RequestedUtc is not null))
        {
            error = "--entry, --file, --operation-id and --requested-utc require captures export, import, or import-result.";
            return false;
        }
        if (portable && !TryValidatePortableCapture(options, out error)) return false;
        if (options.Persist && options.Command is not ("collect" or "inspect-heap" or "session"))
        {
            error = "--persist requires collect, inspect-heap, or session.";
        }
        else if (options.CaptureRoot is not null && string.IsNullOrWhiteSpace(options.CaptureRoot))
        {
            error = "--capture-root must name a stable directory.";
        }
        else if (options.CaptureRoot is not null
                 && options.Command is not ("collect" or "inspect-heap" or "session" or "captures" or "query"))
        {
            error = "--capture-root requires collect, inspect-heap, session, captures, or query.";
        }
        else if (options.CaptureId is not null && options.Command is not ("captures" or "query"))
        {
            error = "--capture-id requires captures or query.";
        }
        else if (options.ArtifactId is not null && (options.Command != "query" || options.CaptureId is null))
        {
            error = "--artifact-id requires query --capture-id <id>.";
        }
        else if (options.CaptureId is not null && !IsCaptureId(options.CaptureId))
        {
            error = "--capture-id must be an exact lower-case GUID in N format (32 hex characters), not a path or temporary handle.";
        }
        else if (options.ArtifactId is not null && !IsCaptureId(options.ArtifactId))
        {
            error = "--artifact-id must be an exact lower-case GUID in N format (32 hex characters).";
        }
        else if (options.Command == "captures")
        {
            if (!CliCommandCatalog.CaptureActions.Contains(options.CaptureAction, StringComparer.Ordinal))
            {
                error = "captures requires one action: list, show, delete, recover, export, import, or import-result.";
            }
            else if (options.HasPid || options.Handle is not null || options.LatestOfKind is not null || options.View is not null)
            {
                error = "captures does not accept live process, handle, or view selectors.";
            }
            else if (options.CaptureAction == "list" && options.CaptureId is not null)
            {
                error = "captures list does not accept --capture-id; use captures show.";
            }
            else if (!portable && options.CaptureAction != "list" && options.CaptureId is null)
            {
                error = $"captures {options.CaptureAction} requires --capture-id <id>.";
            }
        }
        else if (options.Command == "query" && options.CaptureId is not null)
        {
            if (options.ArtifactId is null || string.IsNullOrWhiteSpace(options.View))
            {
                error = "query --capture-id requires --artifact-id <id> and --view <view|records>.";
            }
            else if (options.Handle is not null || options.LatestOfKind is not null || options.HasPid || options.GcHandle is not null)
            {
                error = "--capture-id cannot be combined with --handle, --latest-of-kind, --gc-handle, or --pid.";
            }
        }

        if (error is not null)
        {
            return false;
        }
        if (options.CaptureRoot is not null)
        {
            try
            {
                _ = Path.GetFullPath(options.CaptureRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"--capture-root is invalid: {ex.Message}";
                return false;
            }
        }

        var records = options.Command == "query" && options.View == "records"
            && (options.CaptureId is not null || options.Handle is not null || options.LatestOfKind is not null);
        var list = options.Command == "captures" && options.CaptureAction == "list";
        if ((options.RecordFrom is not null || options.RecordTo is not null || options.RecordName is not null
             || options.AfterRecordId is not null) && !records)
        {
            error = "--from, --to, --name, and --after-record-id require query --view records with a capture ID or durable handle.";
        }
        else if (options.PageSize is not null && !records && !list)
        {
            error = "--page-size requires captures list or a durable records query.";
        }
        else if (options.AfterCaptureId is not null && (!list || !IsCaptureId(options.AfterCaptureId)))
        {
            error = "--after-capture-id requires captures list and an exact lower-case capture GUID.";
        }
        else if (options.PageSize is { } size && (size < 1 || size > (list ? 100 : 1000)))
        {
            error = $"--page-size must be between 1 and {(list ? 100 : 1000)}.";
        }
        else if (options.AfterRecordId < 0)
        {
            error = "--after-record-id must be nonnegative.";
        }
        else if (records && options.Categories.Count > 1)
        {
            error = "A records query accepts at most one exact --category filter.";
        }
        else if ((options.RecordFrom is not null && !TryRecordTime(options.RecordFrom, out _))
                 || (options.RecordTo is not null && !TryRecordTime(options.RecordTo, out _)))
        {
            error = "--from and --to require ISO-8601 timestamps with an explicit UTC offset.";
        }
        else if (options.RecordFrom is not null && options.RecordTo is not null
                 && ParseRecordTime(options.RecordFrom) > ParseRecordTime(options.RecordTo))
        {
            error = "--from must not be later than --to.";
        }
        return error is null;
    }

    private static bool IsCaptureId(string value)
        => Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;

    private static bool TryValidatePortableCapture(CliOptions options, out string? error)
    {
        error = null;
        if (options.CaptureId is not null || options.Persist || options.OutDir is not null || options.SavePath is not null)
            error = "Portable captures use --entry for export and --file for binary input/output, not --capture-id, --persist, --out or --save.";
        else if (options.CaptureAction == "export" && options.CaptureEntries.Count is < 1 or > 16)
            error = "captures export requires 1-16 explicit --entry <capture-id>[=<label>] selections.";
        else if (options.CaptureAction != "export" && options.CaptureEntries.Count != 0)
            error = "--entry is only supported by captures export.";
        else if (options.CaptureAction == "import-result" && options.CaptureFile is not null)
            error = "captures import-result reads an owner-bound receipt, not --file.";
        else if (options.CaptureAction != "import-result" && (string.IsNullOrWhiteSpace(options.CaptureFile) || options.CaptureFile == "-"))
            error = "captures export/import requires --file <path>; binary stdin/stdout and overwrite are not supported.";
        else if ((options.OperationId is null) != (options.RequestedUtc is null) ||
                 (options.CaptureAction == "import-result" && options.OperationId is null))
            error = "--operation-id and --requested-utc must be supplied together; both are required for import-result.";
        else if (options.OperationId is not null && !IsCaptureId(options.OperationId))
            error = "--operation-id must be an exact lower-case GUID in N format.";
        else if (options.RequestedUtc is not null && !TryRecordTime(options.RequestedUtc, out _))
            error = "--requested-utc requires an ISO-8601 timestamp with an explicit UTC offset; reuse the original timestamp on retry.";
        foreach (var entry in options.CaptureEntries)
        {
            var separator = entry.IndexOf('=');
            var id = separator < 0 ? entry : entry[..separator];
            var label = separator < 0 ? null : entry[(separator + 1)..];
            if (!IsCaptureId(id) || (label is not null && System.Text.Encoding.UTF8.GetByteCount(label) > 256) || label?.Any(char.IsControl) == true)
                error = "--entry requires an exact capture GUID optionally followed by = and a label of at most 256 UTF-8 bytes without control characters.";
        }
        if (error is null && options.CaptureFile is not null)
        {
            try { _ = Path.GetFullPath(options.CaptureFile); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = "--file must name a valid local file path.";
            }
        }
        return error is null;
    }

    private static bool TryRecordTime(string value, out DateTimeOffset timestamp)
        => DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
           && (value.EndsWith('Z') || value.LastIndexOf('+') > 10 || value.LastIndexOf('-') > 10);

    private static DateTimeOffset? ParseRecordTime(string? value)
        => value is not null && TryRecordTime(value, out var timestamp) ? timestamp : null;
}

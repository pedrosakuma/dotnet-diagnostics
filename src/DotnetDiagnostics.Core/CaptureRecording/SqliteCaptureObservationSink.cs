using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>Invocation-owned adapter. Callback admission never serializes artifacts or touches disk.</summary>
internal sealed class SqliteCaptureObservationSink(
    CaptureWriter writer, string artifactId, CaptureStoreOptions options,
    Action<DiagnosticHandle, object>? registered = null) : ICaptureObservationSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HandleLookup> _artifacts = new(StringComparer.Ordinal);
    private bool _overflow;
    private bool _reported;
    private bool _unknown;
    private long _sourceRejected;
    private bool _closed;

    public bool TryAppend(CaptureObservation observation)
    {
        // Let the writer count invalid offers, without copying a hostile/unbounded field collection.
        if (observation.Fields.Count > options.MaxFields)
            return writer.TryAppend(artifactId, null!);
        var fields = new CaptureField[observation.Fields.Count];
        for (var i = 0; i < fields.Length; i++)
        {
            var field = observation.Fields[i];
            fields[i] = field.Kind switch
            {
                CaptureObservationValueKind.Null => new(field.Name, CaptureFieldKind.Null),
                CaptureObservationValueKind.String => new(field.Name, CaptureFieldKind.Text, StringValue: field.Text),
                CaptureObservationValueKind.Integer => new(field.Name, CaptureFieldKind.SignedInteger, Int64Value: field.Integer),
                CaptureObservationValueKind.Number => new(field.Name, CaptureFieldKind.FloatingPoint, DoubleValue: field.Number),
                CaptureObservationValueKind.Boolean => new(field.Name, CaptureFieldKind.Boolean, BooleanValue: field.Boolean),
                _ => new(field.Name, (CaptureFieldKind)(-1)),
            };
        }
        return writer.TryAppend(artifactId, new CaptureRecord(
            observation.Timestamp, observation.ThreadId, observation.Category, observation.Name, Fields: fields));
    }

    public void ReportSourceLoss(string source, long? count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count.GetValueOrDefault());
        lock (_gate)
        {
            if (_closed) return;
            _reported = true;
            if (count is null || count > long.MaxValue - _sourceRejected) _unknown = true;
            else _sourceRejected += count.Value;
        }
    }

    public void ArtifactRegistered(DiagnosticHandle handle, object artifact)
    {
        lock (_gate)
        {
            if (_closed) return;
            registered?.Invoke(handle, artifact);
            if (_artifacts.TryGetValue(handle.Id, out var existing))
            {
                // A delegating registration can announce first without metadata, then with it.
                _artifacts[handle.Id] = new(handle with
                {
                    ProducingTool = handle.ProducingTool ?? existing.Handle.ProducingTool,
                }, artifact);
                return;
            }
            if (_artifacts.Count == options.MaxArtifacts) { _overflow = true; return; }
            _artifacts.Add(handle.Id, new(handle, artifact));
        }
    }

    internal (HandleLookup[] Artifacts, bool Overflow) Finish()
    {
        lock (_gate)
        {
            _closed = true;
            writer.SetSourceRejected(_reported && !_unknown ? _sourceRejected : null);
            var artifacts = _artifacts.Values.ToArray();
            _artifacts.Clear();
            return (artifacts, _overflow);
        }
    }
}

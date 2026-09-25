using System.Text;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>Invocation-owned adapter. Callback admission never serializes artifacts or touches disk.</summary>
internal sealed class SqliteCaptureObservationSink : ICaptureObservationSink
{
    private readonly object _gate;
    private readonly CaptureWriter _writer;
    private readonly CaptureStoreOptions _options;
    private readonly SqliteCaptureObservationSink _root;
    private readonly Action<DiagnosticHandle, object>? _registered;
    private readonly Dictionary<string, HandleLookup> _artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqliteCaptureObservationSink> _owners;
    private readonly List<SqliteCaptureObservationSink> _nodes;
    private readonly Dictionary<string, long?> _sources = new(StringComparer.Ordinal);
    private bool _overflow;
    private bool _reported;
    private bool _unknown;
    private long _sourceRejected;
    private long _sourceReportsRejected;
    private long _offered;
    private long _accepted;
    private bool _closed;
    private DiagnosticError? _error;
    private bool _cancelled;
    private object? _result;
    private RejectedChildSink? _rejectedChild;

    internal SqliteCaptureObservationSink(CaptureWriter writer, string artifactId, string kind, string name,
        CaptureStoreOptions options, Action<DiagnosticHandle, object>? registered = null)
    {
        _writer = writer;
        _options = options;
        _registered = registered;
        _gate = new();
        _root = this;
        _owners = new(StringComparer.Ordinal);
        _nodes = [this];
        ArtifactId = artifactId;
        Kind = kind;
        Name = name;
    }

    private SqliteCaptureObservationSink(SqliteCaptureObservationSink parent, string artifactId, string kind, string name)
    {
        _root = parent._root;
        _gate = _root._gate;
        _writer = _root._writer;
        _options = _root._options;
        _registered = _root._registered;
        _owners = _root._owners;
        _nodes = _root._nodes;
        ArtifactId = artifactId;
        Kind = kind;
        Name = name;
        ParentArtifactId = parent.ArtifactId;
    }

    private string ArtifactId { get; }
    private string Kind { get; }
    private string Name { get; }
    private string? ParentArtifactId { get; }

    public bool TryAppend(CaptureObservation observation)
    {
        Interlocked.Increment(ref _offered);
        // Let the writer count invalid offers, without copying a hostile/unbounded field collection.
        if (observation.Fields.Count > _options.MaxFields)
            return _writer.TryAppend(ArtifactId, null!);
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
        var accepted = _writer.TryAppend(ArtifactId, new CaptureRecord(
            observation.Timestamp, observation.ThreadId, observation.Category, observation.Name, Fields: fields));
        if (accepted) Interlocked.Increment(ref _accepted);
        return accepted;
    }

    public void ReportSourceLoss(string source, long? count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count.GetValueOrDefault());
        lock (_gate)
        {
            if (_root._closed) return;
            _reported = true;
            if (count is null || count > long.MaxValue - _sourceRejected) _unknown = true;
            else _sourceRejected += count.Value;
            if (Encoding.UTF8.GetByteCount(source) > 1024 ||
                !_sources.ContainsKey(source) && _sources.Count == 64)
            {
                _sourceReportsRejected++;
                _unknown = true;
                return;
            }
            if (_sources.TryGetValue(source, out var previous))
                _sources[source] = previous is null || count is null || count > long.MaxValue - previous
                    ? null : previous + count;
            else _sources.Add(source, count);
        }
    }

    public void ArtifactRegistered(DiagnosticHandle handle, object artifact)
    {
        lock (_gate)
        {
            if (_root._closed) return;
            _registered?.Invoke(handle, artifact);
            if (_owners.TryGetValue(handle.Id, out var owner))
            {
                var existing = owner._artifacts[handle.Id];
                // A root result can reference a child handle; never move it back into the parent route.
                owner._artifacts[handle.Id] = new(handle with
                {
                    ProducingTool = handle.ProducingTool ?? existing.Handle.ProducingTool,
                }, artifact);
                return;
            }
            // A returned rejected-child handle must not be re-announced into its parent.
            if (ReferenceEquals(this, _root) && _root._overflow) return;
            if (_owners.Count == _options.MaxArtifacts) { _root._overflow = true; return; }
            _owners.Add(handle.Id, this);
            _artifacts.Add(handle.Id, new(handle, artifact));
        }
    }

    public ICaptureObservationSink CreateChild(string kind, string name)
    {
        kind = DurableCaptureKinds.Canonical(kind);
        lock (_gate)
        {
            if (_root._closed) throw new InvalidOperationException("Capture recording has completed.");
            if (_nodes.Count == _options.MaxArtifacts)
            {
                _root._overflow = true;
                return _root._rejectedChild ??= new(_root);
            }
            var id = _writer.AddArtifact(kind, name);
            var child = new SqliteCaptureObservationSink(this, id, kind, name);
            _nodes.Add(child);
            return child;
        }
    }

    private sealed class RejectedChildSink(SqliteCaptureObservationSink root) : IReplayCaptureObservationSink
    {
        public bool TryAppend(CaptureObservation observation)
            => root._writer.TryAppend(root.ArtifactId, null!);

        public ValueTask<bool> AppendReplayAsync(CaptureObservation observation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(TryAppend(observation));
        }

        public void ReportSourceLoss(string source, long? count) { }

        public void ArtifactRegistered(DiagnosticHandle handle, object artifact)
        {
            lock (root._gate)
            {
                if (!root._closed) root._registered?.Invoke(handle, artifact);
            }
        }

        public ICaptureObservationSink CreateChild(string kind, string name)
        {
            lock (root._gate)
            {
                if (root._closed) throw new InvalidOperationException("Capture recording has completed.");
                return this;
            }
        }
    }

    public void ReportCompletion(DiagnosticError? error, bool cancelled, object? data = null)
    {
        lock (_gate)
        {
            if (_root._closed) return;
            _error ??= error;
            _cancelled |= cancelled;
            _result = data;
        }
    }

    internal CaptureRecordingCompletion Finish()
    {
        lock (_gate)
        {
            _root._closed = true;
            var nodes = _nodes.Select(node => new CaptureRecordingNode(
                node.ArtifactId, node.Kind, node.Name, node.ParentArtifactId,
                node._artifacts.Values.ToArray(), node._reported || Interlocked.Read(ref node._offered) != 0,
                Interlocked.Read(ref node._offered),
                Interlocked.Read(ref node._accepted),
                node._reported && !node._unknown ? node._sourceRejected : null,
                new Dictionary<string, long?>(node._sources, StringComparer.Ordinal),
                node._sourceReportsRejected, node._error, node._cancelled, node._result)).ToArray();
            long? aggregate = 0;
            foreach (var node in nodes)
            {
                var hasChildren = nodes.Any(child => child.ParentArtifactId == node.ArtifactId);
                if (hasChildren && node.Offered == 0 && node.Sources.Count == 0 && node.SourceReportsRejected == 0)
                    continue;
                if (node.SourceRejected is not { } count || aggregate is null || count > long.MaxValue - aggregate)
                    aggregate = null;
                else aggregate += count;
            }
            _writer.SetSourceRejected(_root._overflow ? null : aggregate);
            foreach (var node in _nodes)
            {
                node._artifacts.Clear();
                node._result = null;
            }
            _owners.Clear();
            return new(nodes, _root._overflow);
        }
    }
}

internal sealed record CaptureRecordingCompletion(CaptureRecordingNode[] Nodes, bool Overflow);

internal sealed record CaptureRecordingNode(
    string ArtifactId, string Kind, string Name, string? ParentArtifactId,
    HandleLookup[] Artifacts, bool RecordStreamAvailable, long Offered, long Accepted, long? SourceRejected,
    IReadOnlyDictionary<string, long?> Sources, long SourceReportsRejected,
    DiagnosticError? Error, bool Cancelled, object? Result);

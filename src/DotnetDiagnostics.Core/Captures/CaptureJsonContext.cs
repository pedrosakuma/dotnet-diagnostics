using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Captures;

[JsonSerializable(typeof(CaptureManifest))]
[JsonSerializable(typeof(CaptureSeal))]
[JsonSerializable(typeof(CaptureRecordEntry))]
[JsonSerializable(typeof(CaptureRecordPage))]
internal sealed partial class CaptureJsonContext : JsonSerializerContext;

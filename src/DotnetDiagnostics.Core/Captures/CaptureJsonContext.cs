using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Captures;

[JsonSerializable(typeof(CaptureManifest))]
[JsonSerializable(typeof(CaptureSeal))]
internal sealed partial class CaptureJsonContext : JsonSerializerContext;

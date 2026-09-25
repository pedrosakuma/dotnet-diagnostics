using System.Text.Json;
using DotnetDiagnostics.Core.CaptureRecording;

namespace DotnetDiagnostics.Core.Captures;

internal static class PortableCpuRecordValidation
{
    internal static void Validate(long role, Dictionary<string, CaptureField> fields,
        PortableCaptureOptions options, ref long tokens)
    {
        var occurrence = Required("sourceOccurrence", CaptureFieldKind.Boolean);
        if (occurrence.BooleanValue != (role == 2)) throw Invalid();
        if (role == 2)
        {
            if (Required("weight", CaptureFieldKind.SignedInteger).Int64Value <= 0 ||
                Required("sourceSeconds", CaptureFieldKind.FloatingPoint).DoubleValue is null) throw Invalid();
            return;
        }
        if (Required("stackOrder", CaptureFieldKind.Text).StringValue != "leaf-to-root" ||
            Required("stackTruncated", CaptureFieldKind.Boolean).BooleanValue != false ||
            Required("provenance", CaptureFieldKind.Text).StringValue != "interpreted-stack-definition") throw Invalid();
        var text = Required("stack", CaptureFieldKind.Text).StringValue ?? throw Invalid();
        var json = CapturePackage.Utf8.GetBytes(text);
        tokens = checked(tokens + PortableSnapshotImport.CheckJson(json, options.MaxTokensPerSnapshot, 64));
        PortableBounds.Check("MaxTokensPerCapture", tokens, options.MaxTokensPerCapture);
        using var document = JsonDocument.Parse(json);
        var stack = document.RootElement;
        if (stack.ValueKind != JsonValueKind.Array || stack.GetArrayLength() > SamplerObservationProjection.MaximumFrames)
            throw Invalid();
        foreach (var frame in stack.EnumerateArray())
        {
            if (frame.ValueKind != JsonValueKind.Object || frame.EnumerateObject().Count() != 3 ||
                !frame.TryGetProperty("module", out var module) || module.ValueKind != JsonValueKind.String ||
                !frame.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                !frame.TryGetProperty("identity", out var identity)) throw Invalid();
            if (identity.ValueKind != JsonValueKind.Null)
                CaptureArtifactCodec.ValidateMethodIdentity(CapturePackage.Utf8.GetBytes(identity.GetRawText()));
        }

        CaptureField Required(string name, CaptureFieldKind kind) =>
            fields.TryGetValue(name, out var value) && value.Kind == kind ? value : throw Invalid();
    }

    private static CaptureStoreException Invalid() =>
        CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Data.CpuStackRecord: invalid registered stack definition/reference semantics.");
}

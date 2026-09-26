using System.Buffers.Binary;

namespace DotnetDiagnostics.Core.Captures;

public sealed partial class PortableCaptureUseCases
{
    /// <summary>Admits at most 100 portable control/chunk calls per UTC second across cooperating hosts of an existing store.</summary>
    public void AdmitTransferCall()
    {
        try { AdmitTransferCallCore(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CapturePackage.Translate(exception);
        }
    }

    private void AdmitTransferCallCore()
    {
        var root = _store.PortableRoot();
        if (!Directory.Exists(root)) return;
        using var control = _store.PortableCallAdmission(root);
        Span<byte> record = stackalloc byte[16];
        var second = _clock.GetUtcNow().ToUnixTimeSeconds();
        var count = 0;
        if (control.Length != 0)
        {
            if (control.Length != record.Length)
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "TransferCallAdmission: invalid control record.");
            control.ReadExactly(record);
            if (BinaryPrimitives.ReadInt32LittleEndian(record[12..]) != 1)
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "TransferCallAdmission: unsupported control record.");
            if (BinaryPrimitives.ReadInt64LittleEndian(record) == second)
                count = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            if (count is < 0 or > 100)
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "TransferCallAdmission: invalid control count.");
        }
        if (count >= 100) throw CapturePackage.Error(CaptureErrorCode.Busy, "RateLimit");
        BinaryPrimitives.WriteInt64LittleEndian(record, second);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], count + 1);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], 1);
        control.Position = 0;
        control.Write(record);
        control.Flush(flushToDisk: true);
    }
}

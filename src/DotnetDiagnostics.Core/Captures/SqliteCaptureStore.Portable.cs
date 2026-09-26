using System.Diagnostics;

namespace DotnetDiagnostics.Core.Captures;

public sealed partial class SqliteCaptureStore
{
    internal CaptureStoreOptions PortableStoreOptions => _options;
    internal string PortableRoot() => Root(create: false);
    internal string InitializePortableRoot() => Root(create: true);
    internal string PortablePackagePath(string captureId) => PackagePath(captureId);
    internal static FileStream PortableAdmission(string root) => AcquireControl(root, ".admission");
    internal FileStream PortableValidator() => AcquireControl(Root(create: false), ".import-validator");
    internal FileStream PortableWriter()
    {
        using var admission = PortableAdmission(PortableRoot());
        try { return AcquireWriterSlot(PortableRoot()); }
        catch (CaptureStoreException ex) when (ex.Code == CaptureErrorCode.CapacityExceeded)
        {
            throw CapturePackage.Error(CaptureErrorCode.Busy, "Import writer slot is occupied.", ex);
        }
    }

    internal void CheckImportPublication(string captureId, CaptureInfo info, CaptureAccess access)
    {
        CapturePackage.ValidateId(captureId);
        CapturePackage.ValidateAccess(access);
        if (info.OwnerId != access.OwnerId || info.CaptureId != captureId || info.State != CaptureState.Sealed)
            throw CapturePackage.Error(CaptureErrorCode.Forbidden, "Imported capture must be sealed and owned by the current caller.");
        var root = PortableRoot();
        if (Directory.Exists(Path.Combine(root, captureId)) || File.Exists(Path.Combine(root, ".deleted-" + captureId)))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "IdentityCollision: destination capture ID is occupied.");
        PortableBounds.Check("MaxCaptures", CatalogDirectories(root, includeDeleted: true).Count + 1L, _options.MaxCaptures);
        CheckPortableAdmission(root, 0);
    }

    internal CaptureReader ReadTrustedImportStaging(string directory, CaptureManifest manifest)
    {
        CapturePackage.ValidateMembers(directory);
        ValidateSeal(directory, manifest);
        var connection = CapturePackage.Connect(directory, immutable: true);
        try
        {
            CapturePackage.ValidateDatabase(connection, CapturePackage.PortableFormat);
            return new(connection, null, manifest.Info, _options, CapturePackage.PortableFormat);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void CheckPortableAdmission(string root, long reservation)
    {
        var watch = Stopwatch.StartNew();
        long bytes = PortableCaptureStorage.AccountedBytes(root);
        foreach (var directory in CatalogDirectories(root, includeDeleted: true))
        {
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
            bytes = checked(bytes + AccountedPackageBytes(directory));
        }
        foreach (var directory in Directory.EnumerateDirectories(root, ".recovery-*"))
        {
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
            CapturePackage.RejectLinks(directory);
            bytes = checked(bytes + CapturePackage.PackageBytes(directory));
        }
        PortableBounds.Check("MaxStoreBytes", checked(bytes + reservation),
            Math.Min(_options.MaxStoreBytes, PortableBounds.StoreBytes));
        PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
    }
}

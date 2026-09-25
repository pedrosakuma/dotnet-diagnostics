using System.Diagnostics;

namespace DotnetDiagnostics.Core.Captures;

public sealed partial class SqliteCaptureStore
{
    internal CaptureStoreOptions PortableStoreOptions => _options;
    internal string PortableRoot() => Root(create: false);
    internal string PortablePackagePath(string captureId) => PackagePath(captureId);
    internal static FileStream PortableAdmission(string root) => AcquireControl(root, ".admission");

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

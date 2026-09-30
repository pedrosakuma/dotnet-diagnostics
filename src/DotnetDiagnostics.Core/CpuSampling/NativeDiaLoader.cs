using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dia2Lib;

namespace DotnetDiagnostics.Core.CpuSampling;

[SupportedOSPlatform("windows")]
internal static class NativeDiaLoader
{
    // Keep the library loaded for the lifetime of any DIA COM objects in this process.
    private static readonly Lazy<nint> Library = new(() => NativeLibrary.Load(
        Path.Combine(AppContext.BaseDirectory, ArchitectureDirectory, "msdia140.dll")));

    private static string ArchitectureDirectory => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("DIA is not shipped for this process architecture."),
    };

    internal static IDiaDataSource CreateDataSource()
    {
        var getClassObject = Marshal.GetDelegateForFunctionPointer<GetClassObject>(
            NativeLibrary.GetExport(Library.Value, "DllGetClassObject"));
        var classId = new Guid("E6756135-1E65-4D17-8576-610761398C3C");
        var factoryId = typeof(IClassFactory).GUID;
        Marshal.ThrowExceptionForHR(getClassObject(in classId, in factoryId, out var factory));
        try
        {
            var interfaceId = typeof(IDiaDataSource).GUID;
            factory.CreateInstance(null, in interfaceId, out var source);
            return (IDiaDataSource)source;
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(factory);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetClassObject(
        in Guid classId,
        in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IClassFactory factory);

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        void CreateInstance(
            [MarshalAs(UnmanagedType.Interface)] object? outer,
            in Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object instance);

        void LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }
}

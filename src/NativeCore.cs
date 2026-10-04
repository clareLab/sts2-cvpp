using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cvpp;

internal static partial class NativeCore
{
    internal const uint ExpectedAbi = 3;

    static NativeCore() => NativeLibrary.SetDllImportResolver(typeof(NativeCore).Assembly, Resolve);

    internal static uint Initialize()
    {
        uint actual = AbiVersion();
        if (actual != ExpectedAbi)
            throw new InvalidOperationException($"Rust core ABI mismatch: expected {ExpectedAbi}, received {actual}.");
        return actual;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "cvpp_core") return nint.Zero;
        string file = OperatingSystem.IsWindows() ? "cvpp_core.dll"
            : OperatingSystem.IsLinux() ? "libcvpp_core.so"
            : OperatingSystem.IsMacOS() ? "libcvpp_core.dylib"
            : throw new PlatformNotSupportedException();
        string directory = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidOperationException("The mod assembly has no directory.");
        return NativeLibrary.Load(Path.Combine(directory, file));
    }

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint AbiVersion();

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_search_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint SearchCreate(uint limit, ushort depth);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_search_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SearchFree(nint search);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_search_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int SearchNext(SearchHandle search, uint* output, uint capacity);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_search_observe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int SearchObserve(SearchHandle search, long score, uint solution, uint* actions, uint count);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_search_best")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int SearchBest(SearchHandle search, uint* output, uint capacity);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_search_stats")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial SearchStats SearchStats(SearchHandle search);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace cvpp;

internal sealed class PlannerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal PlannerHandle(nint value) : base(true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        NativeCore.PlannerFree(handle);
        return true;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PlannerStats(uint Nodes, uint Simulations, uint Bounded, uint Reserved, long Best, ulong MemoryBytes);

internal sealed class NativePlanner : IDisposable
{
    private readonly PlannerHandle _handle;

    internal NativePlanner(uint nodes, ushort depth)
    {
        if (nodes is 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(nodes));
        if (depth is 0 or > 256) throw new ArgumentOutOfRangeException(nameof(depth));
        NativeCore.Initialize();
        nint pointer = NativeCore.PlannerCreate(nodes, depth);
        if (pointer == 0) throw new InvalidOperationException("Could not create the native planner.");
        _handle = new PlannerHandle(pointer);
    }

    internal PlannerStats Stats => NativeCore.PlannerStats(_handle);

    internal unsafe int Next(Span<uint> path)
    {
        if (path.Length is 0 or > 256) throw new ArgumentOutOfRangeException(nameof(path));
        fixed (uint* output = path)
            return Check(NativeCore.PlannerNext(_handle, output, (uint)path.Length));
    }

    internal unsafe void Observe(int hp, bool terminal, ReadOnlySpan<uint> actions)
    {
        fixed (uint* input = actions)
            Check(NativeCore.PlannerObserve(_handle, hp, terminal ? 1u : 0u, input, checked((uint)actions.Length)));
    }

    private static int Check(int status) => status >= -1 ? status
        : throw new InvalidOperationException($"Native planner rejected the operation: {status}.");

    public void Dispose() => _handle.Dispose();
}

internal static partial class NativeCore
{
    [LibraryImport("cvpp_core", EntryPoint = "cvpp_planner_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint PlannerCreate(uint nodes, ushort depth);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_planner_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void PlannerFree(nint planner);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_planner_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int PlannerNext(PlannerHandle planner, uint* output, uint capacity);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_planner_observe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int PlannerObserve(PlannerHandle planner, int hp, uint terminal, uint* actions, uint count);

    [LibraryImport("cvpp_core", EntryPoint = "cvpp_planner_stats")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PlannerStats PlannerStats(PlannerHandle planner);
}

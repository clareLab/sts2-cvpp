using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace cvpp;

internal sealed class SearchHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SearchHandle(nint value) : base(true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        NativeCore.SearchFree(handle);
        return true;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct SearchStats(uint Allocated, uint Evaluated, uint Bounded, uint BestFound, long BestScore);

internal sealed class NativeSearch : IDisposable
{
    private readonly SearchHandle _handle;

    internal NativeSearch(uint nodeLimit, ushort depthLimit)
    {
        if (nodeLimit is 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(nodeLimit));
        if (depthLimit is 0 or > 64) throw new ArgumentOutOfRangeException(nameof(depthLimit));
        NativeCore.Initialize();
        nint value = NativeCore.SearchCreate(nodeLimit, depthLimit);
        if (value == 0) throw new InvalidOperationException("Native search could not be initialized.");
        _handle = new SearchHandle(value);
    }

    internal SearchStats Stats => NativeCore.SearchStats(_handle);

    internal unsafe int Next(Span<uint> path)
    {
        if (path.IsEmpty || path.Length > 64) throw new ArgumentOutOfRangeException(nameof(path));
        fixed (uint* output = path)
            return Check(NativeCore.SearchNext(_handle, output, (uint)path.Length));
    }

    internal unsafe void Observe(long score, bool solution, ReadOnlySpan<uint> actions)
    {
        fixed (uint* input = actions)
            Check(NativeCore.SearchObserve(_handle, score, solution ? 1u : 0u, input, checked((uint)actions.Length)));
    }

    internal unsafe int Best(Span<uint> path)
    {
        if (path.IsEmpty || path.Length > 64) throw new ArgumentOutOfRangeException(nameof(path));
        fixed (uint* output = path)
            return Check(NativeCore.SearchBest(_handle, output, (uint)path.Length));
    }

    private static int Check(int result) => result >= -1 ? result
        : throw new InvalidOperationException($"Native search rejected the operation: {result}.");

    public void Dispose() => _handle.Dispose();
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace cvpp;

internal sealed partial class SnapshotNative : SafeHandleZeroOrMinusOneIsInvalid
{
    private SnapshotNative(nint value) : base(true) => SetHandle(value);

    internal static unsafe SnapshotNative Capture(ReadOnlySpan<ulong> words, SnapshotNative? parent = null)
    {
        fixed (ulong* data = words)
        {
            nint value = parent == null ? CaptureWords(data, checked((uint)words.Length), 8 * 1024 * 1024)
                : CaptureFrom(parent, data, checked((uint)words.Length), 8 * 1024 * 1024);
            if (value == 0) throw new InvalidOperationException("Snapshot allocation rejected.");
            return new SnapshotNative(value);
        }
    }

    internal unsafe void Restore(Span<ulong> words)
    {
        fixed (ulong* data = words)
            if (RestoreWords(this, data, checked((uint)words.Length)) != 0)
                throw new InvalidOperationException("Snapshot restore rejected.");
    }

    protected override bool ReleaseHandle()
    {
        Free(handle);
        return true;
    }

    [LibraryImport("cvpp_snapshot_probe", EntryPoint = "cvpp_snapshot_capture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial nint CaptureWords(ulong* source, uint count, uint budget);

    [LibraryImport("cvpp_snapshot_probe", EntryPoint = "cvpp_snapshot_capture_from")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial nint CaptureFrom(SnapshotNative parent, ulong* source, uint count, uint budget);

    [LibraryImport("cvpp_snapshot_probe", EntryPoint = "cvpp_snapshot_shared_bytes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong SharedBytes(SnapshotNative snapshot);

    [LibraryImport("cvpp_snapshot_probe", EntryPoint = "cvpp_snapshot_restore")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int RestoreWords(SnapshotNative snapshot, ulong* output, uint count);

    [LibraryImport("cvpp_snapshot_probe", EntryPoint = "cvpp_snapshot_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Free(nint snapshot);

    [LibraryImport("cvpp_snapshot_probe", EntryPoint = "cvpp_snapshot_live_bytes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong LiveBytes();
}

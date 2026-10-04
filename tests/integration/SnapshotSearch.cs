using System.Diagnostics;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal sealed class SnapshotSearch(NativeCombat combat, Func<ValueTask<RunState>> restore) : IDisposable
{
    private SnapshotLoop? _snapshot;
    private RunState? _run;
    private bool _disposed;

    internal uint Restores { get; private set; }
    internal double CaptureMs { get; private set; }
    internal double RestoreMs { get; private set; }
    internal int Bytes => _snapshot?.Graph.Bytes ?? 0;
    internal int References => _snapshot?.Graph.References ?? 0;

    internal async ValueTask<RunState> Restore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_snapshot == null)
        {
            _run = await restore();
            long started = Stopwatch.GetTimestamp();
            _snapshot = new SnapshotLoop(combat, _run);
            CaptureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        else
        {
            long started = Stopwatch.GetTimestamp();
            await _snapshot.Restore(combat);
            RestoreMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Restores++;
        }
        return _run!;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _snapshot?.Dispose();
        _snapshot = null;
        _run = null;
    }
}

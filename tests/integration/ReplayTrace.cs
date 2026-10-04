using System.Diagnostics;

namespace cvpp;

internal sealed class ReplayTrace : IDisposable
{
    internal sealed record Step(int Depth, double Milliseconds, bool Stable);

    internal sealed class Move(uint[] path, Func<bool> stable)
    {
        private long _started = Stopwatch.GetTimestamp();
        public uint[] Path { get; } = path;
        public double? RestoreMs { get; private set; }
        public List<Step> Steps { get; } = [];

        internal void Restored()
        {
            RestoreMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            _started = Stopwatch.GetTimestamp();
        }

        internal void Executed(int depth)
        {
            Steps.Add(new Step(depth, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, stable()));
            _started = Stopwatch.GetTimestamp();
        }
    }

    private static ReplayTrace? _active;
    private readonly Func<bool> _stable;
    internal List<Move> Moves { get; } = [];

    internal ReplayTrace(Func<bool> stable)
    {
        if (_active != null) throw new InvalidOperationException("A replay trace is already active.");
        _active = this;
        _stable = stable;
    }

    internal static Move? Begin(ReadOnlySpan<uint> path)
    {
        if (_active == null) return null;
        var move = new Move(path.ToArray(), _active._stable);
        _active.Moves.Add(move);
        return move;
    }

    public void Dispose()
    {
        if (_active == this) _active = null;
    }
}

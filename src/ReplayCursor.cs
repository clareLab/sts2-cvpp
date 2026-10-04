namespace cvpp;

internal sealed class ReplayCursor<T>(ushort depthLimit, Func<ValueTask<T>> restore, Func<T, uint, ValueTask> execute)
    where T : class
{
    private readonly uint[] _path = new uint[depthLimit];
    private T? _state;
    private int _length;

    internal uint Restores { get; private set; }
    internal uint Actions { get; private set; }

    internal async ValueTask<T> MoveTo(ReadOnlyMemory<uint> path)
    {
        if (path.Length > _path.Length) throw new ArgumentOutOfRangeException(nameof(path));
#if CVPP_SELFTEST
        var trace = ReplayTrace.Begin(path.Span);
#endif
        try
        {
            if (_state == null || path.Length < _length || !path.Span[.._length].SequenceEqual(_path.AsSpan(0, _length)))
            {
                _state = await restore();
                _length = 0;
                Restores++;
#if CVPP_SELFTEST
                trace?.Restored();
#endif
            }
            for (int index = _length; index < path.Length; index++)
            {
                await execute(_state, path.Span[index]);
                Actions++;
#if CVPP_SELFTEST
                trace?.Executed(index + 1);
#endif
            }
            path.Span.CopyTo(_path);
            _length = path.Length;
            return _state;
        }
        catch
        {
            _state = null;
            _length = 0;
            throw;
        }
    }
}

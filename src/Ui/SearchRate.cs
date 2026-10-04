namespace cvpp;

internal sealed class SearchRate
{
    private readonly Queue<(uint Count, double Milliseconds)> _samples = new(16);
    private double _lastMilliseconds;
    private uint _lastCount;
    internal double PerSecond { get; private set; }

    internal void Reset()
    {
        _samples.Clear();
        _samples.Enqueue(default);
        _lastMilliseconds = 0;
        _lastCount = 0;
        PerSecond = 0;
    }

    internal void Observe(uint count, double milliseconds)
    {
        if (milliseconds <= _lastMilliseconds || count < _lastCount) return;
        while (_samples.Count > 1 && milliseconds - _samples.Peek().Milliseconds > 2000) _samples.Dequeue();
        var first = _samples.Count == 0 ? default : _samples.Peek();
        PerSecond = (count - first.Count) * 1000d / (milliseconds - first.Milliseconds);
        if (_samples.Count == 16) _samples.Dequeue();
        _samples.Enqueue((count, milliseconds));
        _lastMilliseconds = milliseconds;
        _lastCount = count;
    }
}

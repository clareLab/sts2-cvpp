namespace cvpp;

internal sealed class ReplayTree<TState, TObservation>(ReplayCursor<TState> cursor, Func<TState, TObservation> observe, uint capacity)
    where TState : class
{
    private sealed class Entry(TObservation observation, uint action = 0, int next = -1)
    {
        internal readonly TObservation Observation = observation;
        internal readonly uint Action = action;
        internal readonly int Next = next;
        internal int Child = -1;
    }

    private readonly List<Entry> _entries = [];
    internal int Count => _entries.Count;

    internal async ValueTask<TObservation> MoveTo(ReadOnlyMemory<uint> path)
    {
        int index = _entries.Count == 0 ? -1 : 0;
        int parent = -1;
        int offset = 0;
        while (index >= 0 && offset < path.Length)
        {
            parent = index;
            uint action = path.Span[offset++];
            index = _entries[index].Child;
            while (index >= 0 && _entries[index].Action != action) index = _entries[index].Next;
        }
        if (index >= 0) return _entries[index].Observation;
        var observation = observe(await cursor.MoveTo(path));
        if (_entries.Count < capacity && offset == path.Length)
        {
            if (parent >= 0)
            {
                var entry = _entries[parent];
                _entries.Add(new Entry(observation, path.Span[^1], entry.Child));
                entry.Child = _entries.Count - 1;
            }
            else if (path.IsEmpty) _entries.Add(new Entry(observation));
        }
        return observation;
    }
}

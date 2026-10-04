namespace cvpp;

internal sealed class SelectionSet<T>(IEnumerable<T> options, int minimum, int maximum)
{
    internal T[] Options { get; } = options.ToArray();
    internal int Minimum { get; } = minimum;
    internal int Maximum { get; } = maximum;
    private T[][]? _selections;

    internal int Count => Selections.Length;
    internal T[] this[uint index] => index < Selections.Length ? Selections[index]
        : throw new ArgumentOutOfRangeException(nameof(index));

    internal T[] Completion => Options.Take(Math.Min(Options.Length, Math.Min(Maximum, Math.Max(1, Minimum)))).ToArray();

    private T[][] Selections => _selections ??= Enumerate();

    private T[][] Enumerate()
    {
        if (Minimum < 0 || Maximum < Minimum || Minimum > Options.Length)
            throw new NotSupportedException("The official selector supplied unsupported selection bounds.");
        if (Minimum == 0 && Maximum == 1)
            throw new NotSupportedException("This selector does not expose whether skipping the choice is legal.");
        int maximum = Math.Min(Maximum, Options.Length);
        if (maximum > 64) throw new NotSupportedException("Selection depth exceeds the search limit.");
        var result = new List<T[]>();
        var selected = new T[maximum];
        var used = new bool[Options.Length];
        void Visit(int depth)
        {
            if (depth >= Minimum)
            {
                if (result.Count == 4096)
                    throw new NotSupportedException("Selection branching exceeds the native action limit.");
                result.Add(selected[..depth]);
            }
            if (depth == maximum) return;
            for (int index = 0; index < Options.Length; index++)
            {
                if (used[index]) continue;
                used[index] = true;
                selected[depth] = Options[index];
                Visit(depth + 1);
                used[index] = false;
            }
        }
        Visit(0);
        return result.ToArray();
    }
}

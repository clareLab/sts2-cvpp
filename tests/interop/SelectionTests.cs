using cvpp;

internal static class SelectionTests
{
    internal static void Run()
    {
        int[] options = [10, 20, 30];
        var choices = new SelectionSet<int>(options, 2, 2);
        options[0] = 99;
        string[] expected = ["10,20", "10,30", "20,10", "20,30", "30,10", "30,20"];
        if (choices.Count != expected.Length || Enumerable.Range(0, choices.Count)
            .Any(i => string.Join(",", choices[(uint)i]) != expected[i]))
            throw new InvalidOperationException("Ordered selections lost alternatives or retained mutable input.");
        var optional = new SelectionSet<int>([1, 2], 0, 2);
        if (optional.Count != 5 || optional[0].Length != 0)
            throw new InvalidOperationException("Optional multi-selection lost the empty choice.");
        Reject<ArgumentOutOfRangeException>(() => _ = choices[6]);
        Reject<NotSupportedException>(() => _ = new SelectionSet<int>([1], 0, 1).Count);
        Reject<NotSupportedException>(() => _ = new SelectionSet<int>([1], 2, 2).Count);
        Reject<NotSupportedException>(() => _ = new SelectionSet<int>([1], -1, 1).Count);
        Reject<NotSupportedException>(() => _ = new SelectionSet<int>(Enumerable.Range(0, 8), 8, 8).Count);
        Reject<NotSupportedException>(() => _ = new SelectionSet<int>(Enumerable.Range(0, 65), 65, 65).Count);
        Console.WriteLine("PASS ordered selections, optional choices, immutable input and branching limits");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

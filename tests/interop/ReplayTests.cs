using cvpp;

internal static class ReplayTests
{
    internal static async Task Run()
    {
        bool rejectRestore = false;
        var cursor = new ReplayCursor<State>(4, async () =>
        {
            await Task.Yield();
            if (rejectRestore) throw new InvalidOperationException("restore");
            return new State();
        }, async (state, action) =>
        {
            Advance(state, action);
            await Task.Yield();
            if (action == 99) throw new InvalidOperationException("action");
        });
        uint[][] paths = [[], [1], [1, 2], [1, 2], [1, 3], [4], [4, 5, 6], []];
        foreach (var path in paths) Check(await cursor.MoveTo(path), path);
        if (cursor.Restores != 4 || cursor.Actions != 7)
            throw new InvalidOperationException("Replay did not reuse exact prefixes or reset divergent routes.");

        try
        {
            await cursor.MoveTo(new uint[] { 7, 99 });
            throw new InvalidOperationException("Missing action failure.");
        }
        catch (InvalidOperationException error) when (error.Message == "action") { }
        Check(await cursor.MoveTo(new uint[] { 7 }), [7]);
        rejectRestore = true;
        try
        {
            await cursor.MoveTo(new uint[] { 8 });
            throw new InvalidOperationException("Missing restore failure.");
        }
        catch (InvalidOperationException error) when (error.Message == "restore") { }
        rejectRestore = false;
        Check(await cursor.MoveTo(new uint[] { 7, 8 }), [7, 8]);

        try
        {
            await cursor.MoveTo(new uint[] { 1, 2, 3, 4, 5 });
            throw new InvalidOperationException("Missing depth failure.");
        }
        catch (ArgumentOutOfRangeException) { }
        Check(await cursor.MoveTo(new uint[] { 7, 8, 9 }), [7, 8, 9]);
        Console.WriteLine("PASS replay prefix reuse, asynchronous execution and recovery after partial failures");
    }

    private static void Check(State actual, uint[] path)
    {
        var expected = new State();
        foreach (uint action in path) Advance(expected, action);
        if (actual.Rng != expected.Rng || actual.Score != expected.Score)
            throw new InvalidOperationException("Reused replay differs from independent execution.");
    }

    private static void Advance(State state, uint action)
    {
        state.Rng = unchecked(state.Rng * 1_664_525 + 1_013_904_223 + action);
        state.Score += state.Rng % 100;
    }

    private sealed class State
    {
        internal uint Rng { get; set; } = 42;
        internal long Score { get; set; }
    }
}

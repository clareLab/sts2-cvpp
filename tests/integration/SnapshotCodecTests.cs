namespace cvpp;

internal static class SnapshotCodecTests
{
    internal static object Run()
    {
        var offset = Array.CreateInstance(typeof(int), [2, 3], [-1, 2]);
        offset.SetValue(int.MinValue, -1, 2);
        offset.SetValue(int.MaxValue, 0, 4);
        var vector = Array.CreateInstance(typeof(long), [2], [5]);
        vector.SetValue(long.MinValue, 5);
        vector.SetValue(long.MaxValue, 6);
        object[] cycle = new object[2];
        cycle[0] = cycle;
        cycle[1] = new List<object> { cycle };
        object[] roots =
        [
            new sbyte[] { sbyte.MinValue, -1, 0, sbyte.MaxValue },
            new byte[] { 0, byte.MaxValue },
            new short[] { short.MinValue, -1, 0, short.MaxValue },
            new ushort[] { 0, ushort.MaxValue },
            new int[] { int.MinValue, -1, 0, int.MaxValue },
            new uint[] { 0, uint.MaxValue },
            new long[] { long.MinValue, -1, 0, long.MaxValue },
            new ulong[] { 0, ulong.MaxValue },
            new char[] { char.MinValue, 'a', char.MaxValue },
            new[] { false, true },
            new[] { 0f, -0f, float.NegativeInfinity, float.PositiveInfinity, BitConverter.UInt32BitsToSingle(0x7fc01234) },
            new[] { 0d, -0d, double.NegativeInfinity, double.PositiveInfinity, BitConverter.UInt64BitsToDouble(0x7ff8000000001234) },
            new long?[] { null, long.MinValue, 42, long.MaxValue },
            new DayOfWeek?[] { null, DayOfWeek.Monday, (DayOfWeek)(-1) },
            new[] { decimal.MinValue, decimal.MaxValue, -1.25m },
            new[] { Guid.Empty, Guid.Parse("dc24dd24-bf36-42f1-bf77-c71cc765afe0") },
            new[] { (23, "reference", (long?)null), (-1, "other", (long?)42) },
            offset,
            vector,
            cycle
        ];
        using var reference = new SnapshotGraph(null, false, roots);
        using var compiled = new SnapshotGraph(null, roots);
        ulong[] expected = reference.Words();
        if (!compiled.Words().SequenceEqual(expected)) throw new InvalidOperationException("Compiled snapshot encoding differs from reflection.");
        foreach (Array array in roots.OfType<Array>()) Array.Clear(array);
        compiled.Restore();
        using var restored = new SnapshotGraph(null, false, roots);
        if (!restored.Words().SequenceEqual(expected) || !ReferenceEquals(cycle[0], cycle)
            || !ReferenceEquals(((List<object>)cycle[1])[0], cycle))
            throw new InvalidOperationException("Compiled snapshot restoration changed scalar bits, array bounds or reference identities.");
        return new { roots = roots.Length, words = expected.Length, reflection_equivalent = true, restored_bits_and_identities = true };
    }
}

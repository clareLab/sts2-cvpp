using cvpp;
using System.Buffers.Binary;

internal static class WireTests
{
    internal static async Task Run()
    {
        await using var stream = new MemoryStream();
        var request = new WorkerMessage("solve", "first", new SolveRequest(new CombatPosition([1, 2], [3], "state"), new SolveOptions()));
        await Wire.Write(stream, request);
        var preview = new CombatPlan([new PlanStep(0, "End turn", "turn", 1, "a", "b", HpDelta: -2)], 65, 1);
        await Wire.Write(stream, new WorkerMessage("progress", "first", Progress: new SolveProgress(4, 20, 65, 100, preview, 4096)));
        stream.Position = 0;
        var read = await Wire.Read(stream);
        var update = (await Wire.Read(stream)).Progress;
        if (read.Id != "first" || read.Request?.Position.Root is not [1, 2] || update?.BestHp != 65
            || update.Plan?.Steps.Single().HpDelta != -2 || update.MemoryBytes != 4096)
            throw new InvalidOperationException("Worker message framing or serialization changed.");
        await Reject<EndOfStreamException>(() => Wire.Read(stream));
        stream.SetLength(0);
        stream.Position = 0;
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        await stream.WriteAsync(header);
        stream.Position = 0;
        await Reject<InvalidDataException>(() => Wire.Read(stream));
        stream.SetLength(0);
        stream.Position = 0;
        await Wire.Write(stream, new WorkerMessage("ready", Abi: 0));
        stream.Position = 0;
        await Reject<InvalidDataException>(() => Wire.Read(stream));
        new SolveOptions(0, MemoryMiB: 0).Validate();
        new SolveOptions(37, MemoryMiB: 1536).Validate();
        Console.WriteLine("PASS worker framing, request roundtrip, truncated input, size and version limits");
    }

    private static async Task Reject<T>(Func<ValueTask<WorkerMessage>> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

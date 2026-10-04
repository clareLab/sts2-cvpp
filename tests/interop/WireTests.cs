using cvpp;
using System.Buffers.Binary;

internal static class WireTests
{
    internal static async Task Run()
    {
        await using var stream = new MemoryStream();
        var request = new WorkerMessage("solve", "first", new SolveRequest(new CombatPosition([1, 2], [3], "state"), new SolveOptions()));
        await Wire.Write(stream, request);
        await Wire.Write(stream, new WorkerMessage("progress", "first", Progress: new SolveProgress(4, 20, 65, 100)));
        stream.Position = 0;
        var read = await Wire.Read(stream);
        if (read.Id != "first" || read.Request?.Position.Root is not [1, 2] || (await Wire.Read(stream)).Progress?.BestHp != 65)
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
        Console.WriteLine("PASS worker framing, request roundtrip, truncated input, size and version limits");
    }

    private static async Task Reject<T>(Func<ValueTask<WorkerMessage>> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

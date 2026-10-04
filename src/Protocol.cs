using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace cvpp;

internal sealed partial record CombatPosition(byte[] Root, byte[] History, string State);
internal sealed record SolveRequest(CombatPosition Position, SolveOptions Options, uint[]? Incumbent = null);
internal sealed record WorkerMod(string Id, string Path);
internal sealed record WorkerSetup(WorkerMod[] Mods, string Compatibility);
internal sealed record WorkerMessage(string Kind, string Id = "", SolveRequest? Request = null,
    SolveProgress? Progress = null, SolveResult? Result = null, string? Error = null, uint Abi = NativeCore.ExpectedAbi, string? Compatibility = null, int? ProcessId = null);

internal static class Wire
{
    private const int Limit = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static async ValueTask Write(Stream stream, WorkerMessage message, CancellationToken token = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > Limit) throw new InvalidDataException("Worker message exceeds the size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async ValueTask<WorkerMessage> Read(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > Limit) throw new InvalidDataException("Invalid worker message size.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<WorkerMessage>(data, Json)
            ?? throw new InvalidDataException("Empty worker message.");
        if (message.Abi != NativeCore.ExpectedAbi) throw new InvalidDataException("Worker version differs from the mod.");
        return message;
    }
}

using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace cvpp;

internal sealed class CombatCheckpoint
{
    private readonly byte[] _bytes;
    internal int Length => _bytes.Length;
    internal string Digest => Convert.ToHexString(SHA256.HashData(_bytes));
    internal byte[] Export() => _bytes.ToArray();

    internal static CombatCheckpoint Import(byte[] bytes)
    {
        var reader = new PacketReader();
        reader.Reset(bytes);
        return new CombatCheckpoint(reader.Read<CombatReplay>());
    }

    internal CombatCheckpoint(CombatReplay replay)
    {
        Validate(replay);
        if (replay.events.Count != 0 || replay.checksumData.Count != 0)
            throw new NotSupportedException("A checkpoint must contain a room-start state without replay events.");
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(replay);
        writer.ZeroByteRemainder();
        _bytes = writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    internal CombatReplay Read()
    {
        var reader = new PacketReader();
        reader.Reset(_bytes);
        var replay = reader.Read<CombatReplay>();
        Validate(replay);
        return replay;
    }

    private static void Validate(CombatReplay replay)
    {
        var release = ReleaseInfoManager.Instance.ReleaseInfo
            ?? throw new NotSupportedException("The installed game does not report its build identity.");
        if (replay.version != release.Version || replay.gitCommit != release.Commit
            || replay.modelIdHash != ModelIdSerializationCache.Hash)
            throw new NotSupportedException("Checkpoint game build or model identities differ from this worker.");
        if (replay.serializableRun.Players.Count != 1)
            throw new NotSupportedException("Combat search currently requires a single player.");
    }
}

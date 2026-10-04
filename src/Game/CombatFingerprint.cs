using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class CombatFingerprint
{
    internal static string Capture(RunState run, SelectionSet<CardModel>? selection = null)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(NetFullCombatState.FromRun(run, null));
        foreach (var player in run.Players) writer.Write(player.PlayerRng.ToSerializable());
        var combat = (run.CurrentRoom as CombatRoom)?.CombatState
            ?? throw new InvalidOperationException("State capture requires a combat room.");
        writer.WriteInt(combat.RoundNumber);
        writer.WriteEnum(combat.CurrentSide);
        foreach (var creature in combat.Creatures)
        {
            if (creature.Monster is not { } monster) continue;
            writer.WriteBool(monster.Rng != null);
            if (monster.Rng != null) writer.Write(monster.Rng.ToSerializable());
            writer.WriteString(monster.NextMove.Id);
            var moves = monster.MoveStateMachine?.StateLog;
            writer.WriteInt(moves?.Count ?? 0);
            if (moves != null)
                foreach (var move in moves) writer.WriteString(move.Id);
        }
        writer.WriteBool(selection != null);
        if (selection is { } pending)
        {
            writer.WriteInt(pending.Minimum);
            writer.WriteInt(pending.Maximum);
            writer.WriteInt(pending.Options.Length);
            foreach (var card in pending.Options) writer.Write(card.ToSerializable());
        }
        writer.ZeroByteRemainder();
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
    }
}

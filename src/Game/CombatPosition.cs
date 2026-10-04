using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace cvpp;

internal sealed partial record CombatPosition
{
    internal static async Task<CombatPosition> Capture()
    {
        if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
        var manager = RunManager.Instance;
        var run = manager.DebugOnlyGetState() ?? throw new InvalidOperationException("No active run.");
        if (!NativeCombat.IsStable(run)) throw new InvalidOperationException("Wait for a player decision.");
        string path = ProjectSettings.GlobalizePath("user://cvpp-current.mcr");
        File.Delete(path);
        manager.CombatReplayWriter.WriteReplay(path, false);
        var reader = new PacketReader();
        reader.Reset(File.ReadAllBytes(path));
        var replay = reader.Read<CombatReplay>();
        var saved = SaveManager.Instance.LoadRunSave();
        if (!saved.Success || saved.SaveData == null) throw new InvalidOperationException("The combat-start save is unavailable.");
        replay.serializableRun = saved.SaveData;
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.WriteList(replay.events.Where(e => e.eventType is CombatReplayEventType.GameAction or CombatReplayEventType.PlayerChoice).ToList());
        writer.ZeroByteRemainder();
        var history = writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
        replay.events.Clear();
        replay.checksumData.Clear();
        return new CombatPosition(new CombatCheckpoint(replay).Export(), history, CombatFingerprint.Capture(run));
    }

    internal async ValueTask<RunState> Restore(NativeCombat combat, CombatCheckpoint? checkpoint = null)
    {
        var run = await combat.Restore(checkpoint ?? CombatCheckpoint.Import(Root));
        var reader = new PacketReader();
        reader.Reset(History);
        var events = reader.ReadList<CombatReplayEvent>();
        var choices = new Queue<CombatReplayEvent>(events.Where(e => e.eventType == CombatReplayEventType.PlayerChoice));
        async ValueTask ResolveChoices()
        {
            while (combat.HasChoice)
            {
                if (!choices.TryDequeue(out var choice)) throw new InvalidOperationException("The recorded choice is missing.");
                if (choice.choiceId != combat.ChoiceId) throw new InvalidOperationException("The recorded choice is out of sequence.");
                var player = run.Players.Single();
                var result = PlayerChoiceResult.FromNetData(player, run, choice.playerChoiceResult!.Value);
                await combat.CompleteRecorded(run, result);
            }
        }
        await ResolveChoices();
        foreach (var evt in events.Where(e => e.eventType == CombatReplayEventType.GameAction))
        {
            if (combat.Finished) throw new InvalidOperationException("Replay ended before the current position.");
            await combat.ExecuteRecorded(run, evt.action!.ToGameAction(run.Players.Single()));
            await ResolveChoices();
        }
        if (choices.Count != 0 || CombatFingerprint.Capture(run) != State)
            throw new InvalidOperationException("The replay differs from the live combat. No route will be executed.");
        return run;
    }
}

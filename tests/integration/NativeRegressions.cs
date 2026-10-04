using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace cvpp;

internal static class NativeRegressions
{
    internal static async Task<object> Run()
    {
        await using var combat = new NativeCombat();
        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "SILENT");
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], "CVPP-REGRESSION-001", GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await combat.Until(() => combat.Stable(run), "regression combat");
        var original = await combat.CaptureRoomStart();
        var replay = original.Read();
        var player = replay.serializableRun.Players.Single();
        player.Deck = [Card<Survivor>(), Card<Prepared>(1), Card<BladeDance>(), Card<Reflex>(), Card<StrikeSilent>(), Card<DefendSilent>()];
        player.CurrentHp = player.MaxHp = 120;
        player.Potions = [ModelDb.Potion<FirePotion>().ToMutable().ToSerializable(0), ModelDb.Potion<BlockPotion>().ToMutable().ToSerializable(1)];
        var checkpoint = new CombatCheckpoint(replay);
        string digest = checkpoint.Digest;
        replay.serializableRun.Players.Single().Deck.Clear();
        Require(checkpoint.Read().serializableRun.Players.Single().Deck.Count == 6, "checkpoint owns serialized bytes");
        replay = checkpoint.Read();
        replay.gitCommit = "incompatible";
        Reject<NotSupportedException>(() => _ = new CombatCheckpoint(replay));
        Reject<InvalidOperationException>(() => _ = new NativeCombat());

        string[][] scripts =
        [
            ["SURVIVOR", "SELECT:REFLEX"],
            ["PREPARED", "SELECT:REFLEX,STRIKE_SILENT"],
            ["BLADE_DANCE", "SHIV", "END_TURN", "END_TURN", "END_TURN", "END_TURN"],
            ["FIRE_POTION", "BLOCK_POTION"],
            ["PREPARED", "SELECT:STRIKE_SILENT,REFLEX"]
        ];
        var traces = new List<(uint[] Path, string[] States)>();
        foreach (var script in scripts)
        {
            combat.Mode = CombatExecution.Reference;
            run = await combat.Restore(checkpoint);
            var path = new List<uint>();
            var states = new List<string> { combat.Fingerprint(run) };
            foreach (string step in script)
            {
                uint action = combat.Actions(run).First(token =>
                {
                    string description = combat.Describe(run, token);
                    return description == step || description.StartsWith(step + ":", StringComparison.Ordinal);
                });
                path.Add(action);
                await combat.Execute(run, action);
                states.Add(combat.Fingerprint(run));
                if (step == "SURVIVOR" || step == "PREPARED")
                    Require(combat.HasChoice && !CombatSearch.Evaluate(combat, run, -100, 1).Solution, "pending choice cannot be a solution");
                if (step == "BLADE_DANCE")
                    Require(run.Players.Single().PlayerCombatState!.Hand.Cards.Count(c => c.Id.Entry == "SHIV") == 3, "native generated cards");
                if (step == "BLOCK_POTION")
                    Require(run.Players.Single().GetPotionAtSlotIndex(0) == null
                        && run.Players.Single().GetPotionAtSlotIndex(1) == null, "native potion consumption");
            }
            traces.Add((path.ToArray(), states.ToArray()));
        }
        combat.Mode = CombatExecution.Worker;
        var cursor = new ReplayCursor<RunState>(16, () => combat.Restore(checkpoint), combat.Execute);
        int compared = 0;
        foreach (int index in new[] { 0, 1, 0, 2, 3, 4, 1 })
        {
            var trace = traces[index];
            for (int length = 0; length <= trace.Path.Length; length++)
            {
                run = await cursor.MoveTo(trace.Path.AsMemory(0, length));
                Require(combat.Fingerprint(run) == trace.States[length], "worker prefix matches official reference");
                compared++;
            }
        }

        run = await combat.Restore(checkpoint);
        string before = combat.Fingerprint(run);
        await RejectAsync<InvalidOperationException>(() => combat.Execute(run, 0));
        Require(combat.Fingerprint(run) == before, "illegal action does not mutate state");
        var stale = run;
        run = await combat.Restore(checkpoint);
        Reject<InvalidOperationException>(() => _ = combat.Actions(stale));
        await combat.Execute(run, traces[0].Path[0]);
        await RejectAsync<ArgumentOutOfRangeException>(() => combat.Execute(run, NativeCombat.Selection | 0x3fffffff));
        Require(combat.HasChoice, "invalid selection preserves the pending choice");
        run = await combat.Restore(checkpoint);
        Require(!combat.HasChoice && combat.Fingerprint(run) == before, "restore drains abandoned choice");

        var worker = await CombatSearch.Run(combat, checkpoint, 64, 6, 2, TimeSpan.FromSeconds(30));
        Require(worker.Search.Path != null, "two-turn search retains a completed route");
        run = await combat.Restore(checkpoint);
        int initialTurn = run.Players.Single().PlayerCombatState!.TurnNumber;
        foreach (uint action in worker.Search.Path!) await combat.Execute(run, action);
        var evaluation = CombatSearch.Evaluate(combat, run, initialTurn, 2);
        Require(evaluation.Solution && evaluation.Score == worker.Search.Stats.BestScore, "worker best route reproduces");
        string bestState = combat.Fingerprint(run);
        combat.Mode = CombatExecution.Reference;
        run = await combat.Restore(checkpoint);
        foreach (uint action in worker.Search.Path!) await combat.Execute(run, action);
        Require(combat.Fingerprint(run) == bestState && CombatSearch.Score(combat, run) == evaluation.Score,
            "two-turn solution matches official reference");
        Require(checkpoint.Digest == digest, "checkpoint remains immutable across branches");
        var terminal = new List<object>();
        foreach (bool victory in new[] { true, false })
        {
            replay = checkpoint.Read();
            player = replay.serializableRun.Players.Single();
            player.Deck = victory ? Enumerable.Range(0, 5).Select(_ => Card<Bludgeon>(1)).ToList() : [Card<DefendSilent>()];
            player.Potions.Clear();
            player.CurrentHp = victory ? 120 : 1;
            var ending = new CombatCheckpoint(replay);
            var path = new List<uint>();
            run = await combat.Restore(ending);
            for (int step = 0; step < 12 && !combat.Finished; step++)
            {
                uint action = victory ? combat.Actions(run).FirstOrDefault(t => t != NativeCombat.EndTurn, NativeCombat.EndTurn)
                    : NativeCombat.EndTurn;
                path.Add(action);
                await combat.Execute(run, action);
            }
            Require(combat.Finished && combat.Victory == victory, "official terminal outcome");
            long score = CombatSearch.Score(combat, run);
            Require(victory ? score >= 1_000_000_000_000L : score == long.MinValue, "terminal scoring");
            combat.Mode = CombatExecution.Worker;
            run = await combat.Restore(ending);
            foreach (uint action in path) await combat.Execute(run, action);
            Require(combat.Finished && combat.Victory == victory && CombatSearch.Score(combat, run) == score
                && combat.Actions(run).Length == 0, "worker terminal outcome matches reference");
            terminal.Add(new { victory, score, steps = path.Count });
            combat.Mode = CombatExecution.Reference;
        }
        await combat.DisposeAsync();
        Reject<ObjectDisposedException>(() => _ = combat.Actions(run));
        GD.Print($"[cvpp] REGRESSIONS {compared} reference states, {worker.Search.Stats.Evaluated} two-turn nodes");
        return new { compared_states = compared, search = worker, terminal, checkpoint_bytes = checkpoint.Length };
    }

    private static SerializableCard Card<T>(int upgrade = 0) where T : CardModel => new()
    {
        Id = ModelDb.Card<T>().Id,
        CurrentUpgradeLevel = upgrade
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task RejectAsync<T>(Func<ValueTask> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

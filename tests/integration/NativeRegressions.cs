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
            var position = await CombatPosition.Capture();
            combat.Mode = CombatExecution.Worker;
            run = await position.Restore(combat);
            Require(combat.Fingerprint(run) == states[^1], "current-position replay matches live decisions");
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
            var endingRun = run;
            var exhausted = await HealthSearch.Run(combat, () => ValueTask.FromResult(endingRun), new SolveOptions(0, 16, 8));
            Require(exhausted.StopReason == "exhausted", "completed tree reports exhaustion with unlimited time");
            terminal.Add(new { victory, score, steps = path.Count });
            combat.Mode = CombatExecution.Reference;
        }
        combat.Mode = CombatExecution.Worker;
        var uncached = await HealthSearch.Run(combat, () => combat.Restore(original), new SolveOptions(0, 64, 32), cache: false);
        var cached = await HealthSearch.Run(combat, () => combat.Restore(original), new SolveOptions(0, 64, 32));
        Require(uncached.Stats == cached.Stats && uncached.StopReason == cached.StopReason
            && uncached.Plan?.FinalHp == cached.Plan?.FinalHp
            && (uncached.Plan?.Steps.Select(step => step.Action) ?? []).SequenceEqual(cached.Plan?.Steps.Select(step => step.Action) ?? [])
            && cached.Actions < uncached.Actions, "cached search preserves traversal, score and route with fewer native actions");
        using var stop = new CancellationTokenSource();
        CombatPlan? preview = null;
        run = await combat.Restore(original);
        int startingHp = run.Players[0].Creature.CurrentHp;
        var baseline = await HealthSearch.Run(combat, () => combat.Restore(original), new SolveOptions(0, 256, 96),
            progress => { if (progress.Plan != null) { preview = progress.Plan; stop.Cancel(); } }, stop.Token);
        Require(preview != null && baseline.StopReason == "cancelled" && baseline.Plan == preview,
            "unlimited search streams a verified preview and preserves it on cancellation");
        Require(preview!.Steps.Sum(step => step.HpDelta) == preview.FinalHp - startingHp, "route HP deltas reconcile to final HP");
        var bounded = await HealthSearch.Run(combat, () => combat.Restore(original), new SolveOptions(0, 1, 8));
        Require(bounded.StopReason == "node_or_depth_limit", "bounded tree is not reported as exhausted");
        Require(baseline.Plan is { Steps.Length: > 0 }, "cancellation preserves a complete winning baseline");
        var health = await HealthSearch.Run(combat, () => combat.Restore(original), new SolveOptions(3, 256, 96),
            incumbent: baseline.Plan!.Steps.Select(step => step.Action).ToArray());
        Require(health.Plan is { Steps.Length: > 0 }, "health planner finds a complete winning route");
        Require(health.Plan!.FinalHp >= baseline.Plan.FinalHp, "health optimization preserves the best final HP");
        Require(health.Plan!.Steps.All(step => (step.Action & 0xc0000000) != NativeCombat.Potion), "health planner does not use potions");
        object registry = await RegistryRegressions.Run(combat, original);
        await combat.DisposeAsync();
        Reject<ObjectDisposedException>(() => _ = combat.Actions(run));
        object delayedVictory = await DelayedVictory();
        GD.Print($"[cvpp] REGRESSIONS {compared} reference states, {worker.Search.Stats.Evaluated} two-turn nodes");
        return new
        {
            compared_states = compared,
            search = worker,
            terminal,
            delayedVictory,
            registry,
            baseline_hp = baseline.Plan.FinalHp,
            health,
            cache = new { uncachedActions = uncached.Actions, cachedActions = cached.Actions, simulations = cached.Stats.Simulations },
            checkpoint_bytes = checkpoint.Length
        };
    }

    private static async Task<object> DelayedVictory()
    {
        await using var combat = new NativeCombat();
        var character = ModelDb.AllCharacters.Single(model => model.Id.Entry == "DEFECT");
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], "CVPP-NATIVE-002", GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await combat.Until(() => combat.Stable(run), "delayed victory fixture");
        var checkpoint = await combat.CaptureRoomStart();
        combat.Mode = CombatExecution.Worker;
        var result = await HealthSearch.Run(combat, () => combat.Restore(checkpoint), new SolveOptions(0, 128, 96));
        var plan = result.Plan ?? throw new InvalidOperationException("Delayed victory fixture has no winning route.");
        Require(plan.Steps[^1].Action == NativeCombat.EndTurn, "delayed victory fixture ends with an orb trigger");
        combat.Mode = CombatExecution.Reference;
        run = await combat.Restore(checkpoint);
        foreach (var step in plan.Steps)
        {
            Require(combat.Fingerprint(run) == step.Before, "delayed victory route before action");
            await combat.Execute(run, step.Action);
            if (!combat.Finished) Require(combat.Fingerprint(run) == step.After, "delayed victory route after action");
        }
        Require(combat.Finished && combat.Victory && run.Players[0].Creature.CurrentHp == plan.FinalHp,
            "end-turn victory waits for official completion");
        return new { character = character.Id.Entry, plan.FinalHp, last_action = plan.Steps[^1].Label };
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

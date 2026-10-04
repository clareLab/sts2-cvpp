using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace cvpp;

internal static class NativeBenchmark
{
    private static RunManager Manager => RunManager.Instance;

    internal static async Task<object> Run()
    {
        Engine.MaxFps = 0;
        await using var combat = new NativeCombat();
        var scenarios = new List<object>();
        try
        {
            var ironclad = ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD");
            var cases = new[] { "CVPP-SMOKE-001", "CVPP-NATIVE-002", "CVPP-NATIVE-003" }
                .Select(seed => (Character: ironclad, Seed: seed, Samples: 6, Solve: true))
                .Concat(ModelDb.AllCharacters.Where(c => c != ironclad)
                    .Select(c => (Character: c, Seed: "CVPP-NATIVE-002", Samples: 2, Solve: false)));
            foreach (var scenario in cases)
            {
                await combat.Reset();
                scenarios.Add(await Scenario(combat, scenario.Character, scenario.Seed, scenario.Samples, scenario.Solve));
            }
        }
        finally
        {
            await combat.Reset();
        }
        return new
        {
            game_version = ReleaseInfoManager.Instance.ReleaseInfo?.Version,
            game_commit = ReleaseInfoManager.Instance.ReleaseInfo?.Commit,
            fps_limit = Engine.MaxFps,
            processors = System.Environment.ProcessorCount,
            asset_loader = "synchronous; isolated headless test patch",
            worker_runtime = "official TestMode; no run scene; differential validation required",
            state_check = "native network state, player and monster RNG, round, side, next moves and move history; not a complete state equivalence proof",
            scenarios
        };
    }

    private static async Task<object> Scenario(NativeCombat combat, CharacterModel character, string seed, int sampleCount, bool solve)
    {
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], seed, GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Manager.EnterMapCoord(point.coord);
        await combat.Until(() => combat.Stable(run), "initial combat");
        string root = combat.Fingerprint(run);
        var checkpoint = await combat.CaptureRoomStart();
        string checkpointHash = checkpoint.Digest;
        var samples = new List<Sample>();
        var expected = new Dictionary<bool, string[]>();
        foreach (int backend in new[] { 0, 1, 2, 3 })
            for (int iteration = -2; iteration < sampleCount; iteration++)
            {
                bool nonInteractive = backend != 0;
                combat.Mode = backend >= 2 ? CombatExecution.Worker
                    : nonInteractive ? CombatExecution.NonInteractive : CombatExecution.Reference;
                combat.PumpContinuations = backend == 3;
                bool attack = iteration % 2 == 0;
                long allocated = GC.GetTotalAllocatedBytes();
                var total = Stopwatch.StartNew();
                var timings = new Dictionary<string, double>();
                run = await combat.Restore(checkpoint, timings);
                if (combat.Fingerprint(run) != root) throw new InvalidOperationException("Restored root differs from native combat.");
                var trajectory = new List<string> { root };
                if (attack)
                {
                    var timer = Stopwatch.StartNew();
                    var player = run.Players.Single();
                    var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Type == CardType.Attack && c.CanPlay());
                    var target = player.Creature.CombatState!.HittableEnemies.First(card.IsValidTarget);
                    int previousHp = target.CurrentHp;
                    Manager.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                    await combat.Until(() => combat.Stable(run) && target.CurrentHp < previousHp, "attack");
                    timings["attack_ms"] = timer.Elapsed.TotalMilliseconds;
                    trajectory.Add(combat.Fingerprint(run));
                }
                for (int turn = 0; turn < 2; turn++)
                {
                    var timer = Stopwatch.StartNew();
                    var player = run.Players.Single();
                    int previous = player.PlayerCombatState!.TurnNumber;
                    Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, previous));
                    await combat.Until(() => combat.Stable(run) && player.PlayerCombatState.TurnNumber > previous, "next turn");
                    timings[$"end_turn_{turn + 1}_ms"] = timer.Elapsed.TotalMilliseconds;
                    trajectory.Add(combat.Fingerprint(run));
                }
                double elapsed = total.Elapsed.TotalMilliseconds;
                long managedBytes = GC.GetTotalAllocatedBytes() - allocated;
                if (expected.TryGetValue(attack, out var previousTrajectory))
                {
                    if (!trajectory.SequenceEqual(previousTrajectory))
                        throw new InvalidOperationException("Repeated route diverged after another branch.");
                }
                else expected[attack] = trajectory.ToArray();
                if (checkpoint.Digest != checkpointHash)
                    throw new InvalidOperationException("Checkpoint bytes changed.");
                var sample = new Sample(iteration < 0, nonInteractive, combat.Mode == CombatExecution.Worker,
                    combat.PumpContinuations, attack, elapsed, managedBytes, timings, trajectory);
                samples.Add(sample);
                GD.Print($"[cvpp] BRANCH {character.Id.Entry}/{seed}/{iteration}/{backend}: {elapsed:F1} ms, {managedBytes} managed bytes");
            }
        if (expected[true][^1] == expected[false][^1])
            throw new InvalidOperationException("Different routes produced identical observations.");
        string encounter = run.Players.Single().Creature.CombatState!.Encounter!.Id.Entry;
        object? search = solve ? await Search(combat, checkpoint) : null;
        return new
        {
            seed,
            character = character.Id.Entry,
            encounter,
            checkpoint_bytes = checkpoint.Length,
            samples,
            search
        };
    }

    private static async Task<object> Search(NativeCombat combat, CombatCheckpoint checkpoint)
    {
        combat.Mode = CombatExecution.Worker;
        combat.PumpContinuations = true;
        var expected = new List<(uint[] Path, string State)>();
        SearchResult? referenceResult = null;
        SearchResult? optimizedResult = null;
        long baseline = long.MinValue;
        async Task<object> Trial(bool pump, bool reuse, bool validate)
        {
            combat.PumpContinuations = pump;
            int visited = 0;
            int initialTurn = 0;
            uint restores = 0;
            uint executed = 0;
            var cursor = new ReplayCursor<RunState>(4, () => combat.Restore(checkpoint), combat.Execute);
            long allocated = GC.GetTotalAllocatedBytes();
            var result = await SearchDriver.Run(4096, 4, TimeSpan.FromSeconds(10), async path =>
            {
                RunState state;
                if (reuse) state = await cursor.MoveTo(path);
                else
                {
                    state = await combat.Restore(checkpoint);
                    restores++;
                    for (int index = 0; index < path.Length; index++)
                    {
                        await combat.Execute(state, path.Span[index]);
                        executed++;
                    }
                }
                if (path.IsEmpty) initialTurn = state.Players.Single().PlayerCombatState!.TurnNumber;
                if (validate)
                {
                    string fingerprint = combat.Fingerprint(state);
                    if (referenceResult == null) expected.Add((path.ToArray(), fingerprint));
                    else if (visited >= expected.Count || !path.Span.SequenceEqual(expected[visited].Path)
                        || fingerprint != expected[visited].State)
                        throw new InvalidOperationException("Optimized replay diverged from independent root replay.");
                }
                visited++;
                var evaluation = CombatSearch.Evaluate(combat, state, initialTurn, 1);
                if (path.Length == 1 && path.Span[0] == NativeCombat.EndTurn) baseline = evaluation.Score;
                return evaluation;
            });
            long managedBytes = GC.GetTotalAllocatedBytes() - allocated;
            if (result.Path == null || result.Stats.BestScore < baseline || baseline == long.MinValue)
                throw new InvalidOperationException("Search failed to preserve its native end-turn baseline.");
            if (referenceResult == null) referenceResult = result;
            else if (result.Stats != referenceResult.Stats || !result.Path.SequenceEqual(referenceResult.Path!)
                || result.StopReason != referenceResult.StopReason || (validate && visited != expected.Count))
                throw new InvalidOperationException("Optimized search changed its traversal or result.");
            if (pump && reuse && !validate) optimizedResult = result;
            return new
            {
                pump,
                reuse,
                result.ElapsedMs,
                managed_bytes = managedBytes,
                restores = reuse ? cursor.Restores : restores,
                actions = reuse ? cursor.Actions : executed
            };
        }
        await Trial(false, false, true);
        await Trial(true, true, true);
        await Trial(true, false, false);
        var samples = new List<object>();
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var modes = new[] { (Pump: false, Reuse: false), (Pump: true, Reuse: false), (Pump: true, Reuse: true) };
            if (iteration % 2 != 0) Array.Reverse(modes);
            foreach (var mode in modes) samples.Add(await Trial(mode.Pump, mode.Reuse, false));
        }
        var result = optimizedResult ?? throw new InvalidOperationException("Missing optimized search result.");
        uint[] bestPath = result.Path ?? throw new InvalidOperationException("Missing verified route.");
        combat.PumpContinuations = true;
        var worker = await combat.Restore(checkpoint);
        var route = new List<string>();
        foreach (uint action in bestPath)
        {
            route.Add(combat.Describe(worker, action));
            await combat.Execute(worker, action);
        }
        string workerResult = combat.Fingerprint(worker);
        if (CombatSearch.Score(combat, worker) != result.Stats.BestScore)
            throw new InvalidOperationException("Best route score did not reproduce.");
        combat.Mode = CombatExecution.Reference;
        var reference = await combat.Restore(checkpoint);
        foreach (uint action in bestPath) await combat.Execute(reference, action);
        if (combat.Fingerprint(reference) != workerResult || CombatSearch.Score(combat, reference) != result.Stats.BestScore)
            throw new InvalidOperationException("Search result diverges from normal official execution.");
        GD.Print($"[cvpp] SEARCH {result.Stats.Evaluated} nodes in {result.ElapsedMs:F1} ms: {string.Join(", ", route)}");
        return new
        {
            horizon = "one player turn through enemy response",
            baseline,
            result,
            route,
            verified = true,
            verified_nodes = expected.Count,
            samples
        };
    }

    private sealed record Sample(bool Warmup, bool NonInteractive, bool Worker, bool PumpContinuations, bool Attack, double TotalMs, long ManagedBytes,
        Dictionary<string, double> Stages, List<string> Trajectory);
}

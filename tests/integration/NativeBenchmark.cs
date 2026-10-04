using System.Diagnostics;
using System.Security.Cryptography;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

internal static class NativeBenchmark
{
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    private static RunManager Manager => RunManager.Instance;

    internal static async Task<object> Run()
    {
        Engine.MaxFps = 0;
        WorkerRuntime.Initialize();
        var originalMode = NonInteractiveMode.AutoSlayerCheck;
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
                NonInteractiveMode.AutoSlayerCheck = () => false;
                WorkerRuntime.Active = false;
                if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
                Manager.CleanUp();
                TestMode.IsOn = false;
                scenarios.Add(await Scenario(scenario.Character, scenario.Seed, scenario.Samples, scenario.Solve));
            }
        }
        finally
        {
            NonInteractiveMode.AutoSlayerCheck = originalMode;
            WorkerRuntime.Active = false;
            TestMode.IsOn = false;
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

    private static async Task<object> Scenario(CharacterModel character, string seed, int sampleCount, bool solve)
    {
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], seed, GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Manager.EnterMapCoord(point.coord);
        await Until(() => Stable(run), "initial combat");
        string root = Fingerprint(run);
        string path = ProjectSettings.GlobalizePath("user://cvpp-root.mcr");
        Manager.CombatReplayWriter.WriteReplay(path, false);
        var reader = new PacketReader();
        reader.Reset(File.ReadAllBytes(path));
        var replay = reader.Read<CombatReplay>();
        if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
        var loaded = SaveManager.Instance.LoadRunSave();
        if (!loaded.Success || loaded.SaveData == null)
            throw new InvalidOperationException("Native room-start save is unavailable.");
        replay.serializableRun = loaded.SaveData;
        replay.events.Clear();
        replay.checksumData.Clear();
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(replay);
        writer.ZeroByteRemainder();
        byte[] checkpoint = writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
        string checkpointHash = Convert.ToHexString(SHA256.HashData(checkpoint));
        var samples = new List<Sample>();
        var expected = new Dictionary<bool, string[]>();
        foreach (int backend in new[] { 0, 1, 2 })
            for (int iteration = -2; iteration < sampleCount; iteration++)
            {
                bool nonInteractive = backend != 0;
                WorkerRuntime.Active = backend == 2;
                NonInteractiveMode.AutoSlayerCheck = () => nonInteractive;
                bool attack = iteration % 2 == 0;
                long allocated = GC.GetTotalAllocatedBytes();
                var total = Stopwatch.StartNew();
                var timings = new Dictionary<string, double>();
                run = await Restore(checkpoint, timings);
                if (Fingerprint(run) != root) throw new InvalidOperationException("Restored root differs from native combat.");
                var trajectory = new List<string> { root };
                if (attack)
                {
                    var timer = Stopwatch.StartNew();
                    var player = run.Players.Single();
                    var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Type == CardType.Attack && c.CanPlay());
                    var target = player.Creature.CombatState!.HittableEnemies.First(card.IsValidTarget);
                    int previousHp = target.CurrentHp;
                    Manager.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                    await Until(() => Stable(run) && target.CurrentHp < previousHp, "attack");
                    timings["attack_ms"] = timer.Elapsed.TotalMilliseconds;
                    trajectory.Add(Fingerprint(run));
                }
                for (int turn = 0; turn < 2; turn++)
                {
                    var timer = Stopwatch.StartNew();
                    var player = run.Players.Single();
                    int previous = player.PlayerCombatState!.TurnNumber;
                    Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, previous));
                    await Until(() => Stable(run) && player.PlayerCombatState.TurnNumber > previous, "next turn");
                    timings[$"end_turn_{turn + 1}_ms"] = timer.Elapsed.TotalMilliseconds;
                    trajectory.Add(Fingerprint(run));
                }
                double elapsed = total.Elapsed.TotalMilliseconds;
                long managedBytes = GC.GetTotalAllocatedBytes() - allocated;
                if (expected.TryGetValue(attack, out var previousTrajectory))
                {
                    if (!trajectory.SequenceEqual(previousTrajectory))
                        throw new InvalidOperationException("Repeated route diverged after another branch.");
                }
                else expected[attack] = trajectory.ToArray();
                if (Convert.ToHexString(SHA256.HashData(checkpoint)) != checkpointHash)
                    throw new InvalidOperationException("Checkpoint bytes changed.");
                var sample = new Sample(iteration < 0, nonInteractive, WorkerRuntime.Active, attack, elapsed, managedBytes, timings, trajectory);
                samples.Add(sample);
                GD.Print($"[cvpp] BRANCH {character.Id.Entry}/{seed}/{iteration}/{backend}: {elapsed:F1} ms, {managedBytes} managed bytes");
            }
        if (expected[true][^1] == expected[false][^1])
            throw new InvalidOperationException("Different routes produced identical observations.");
        string encounter = run.Players.Single().Creature.CombatState!.Encounter!.Id.Entry;
        object? search = solve ? await Search(checkpoint) : null;
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

    private static async Task<object> Search(byte[] checkpoint)
    {
        WorkerRuntime.Active = true;
        NonInteractiveMode.AutoSlayerCheck = () => true;
        long baseline = long.MinValue;
        var result = await SearchDriver.Run(4096, 4, TimeSpan.FromSeconds(10), async path =>
        {
            var state = await Restore(checkpoint, new Dictionary<string, double>());
            int initialTurn = state.Players.Single().PlayerCombatState!.TurnNumber;
            for (int index = 0; index < path.Length; index++) await Execute(state, path.Span[index]);
            bool solution = !CombatManager.Instance.IsInProgress
                || state.Players.Single().PlayerCombatState!.TurnNumber > initialTurn;
            long score = Score(state);
            if (path.Length == 1 && path.Span[0] == uint.MaxValue) baseline = score;
            return new BranchEvaluation(score, solution, solution ? [] : Actions(state));
        });
        if (result.Path == null || result.Stats.BestScore < baseline || baseline == long.MinValue)
            throw new InvalidOperationException("Search failed to preserve its native end-turn baseline.");
        var worker = await Restore(checkpoint, new Dictionary<string, double>());
        var route = new List<string>();
        foreach (uint action in result.Path)
        {
            route.Add(Describe(worker, action));
            await Execute(worker, action);
        }
        string workerResult = Fingerprint(worker);
        if (Score(worker) != result.Stats.BestScore)
            throw new InvalidOperationException("Best route score did not reproduce.");
        WorkerRuntime.Active = false;
        NonInteractiveMode.AutoSlayerCheck = () => false;
        var reference = await Restore(checkpoint, new Dictionary<string, double>());
        foreach (uint action in result.Path) await Execute(reference, action);
        if (Fingerprint(reference) != workerResult || Score(reference) != result.Stats.BestScore)
            throw new InvalidOperationException("Search result diverges from normal official execution.");
        GD.Print($"[cvpp] SEARCH {result.Stats.Evaluated} nodes in {result.ElapsedMs:F1} ms: {string.Join(", ", route)}");
        return new { horizon = "one player turn through enemy response", baseline, result, route, verified = true };
    }

    private static uint[] Actions(RunState run)
    {
        var player = run.Players.Single();
        var hand = player.PlayerCombatState!.Hand.Cards;
        var creatures = player.Creature.CombatState!.Creatures;
        var actions = new List<uint> { uint.MaxValue };
        for (int index = 0; index < hand.Count; index++)
        {
            var card = hand[index];
            if (!card.CanPlay()) continue;
            uint token = checked((uint)(index + 1) << 16);
            if (card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly)
            {
                for (int target = 0; target < creatures.Count; target++)
                    if (card.IsValidTarget(creatures[target])) actions.Add(token | checked((uint)(target + 1)));
            }
            else if (card.IsValidTarget(null)) actions.Add(token);
        }
        return actions.ToArray();
    }

    private static (CardModel Card, Creature? Target) Resolve(RunState run, uint token)
    {
        var player = run.Players.Single();
        var hand = player.PlayerCombatState!.Hand.Cards;
        int index = checked((int)(token >> 16)) - 1;
        int targetIndex = checked((int)(token & 0xffff)) - 1;
        var creatures = player.Creature.CombatState!.Creatures;
        if (index < 0 || index >= hand.Count || targetIndex >= creatures.Count)
            throw new InvalidOperationException("Search route references an unavailable card or target.");
        var card = hand[index];
        var target = targetIndex < 0 ? null : creatures[targetIndex];
        if (!card.CanPlay() || !card.IsValidTarget(target))
            throw new InvalidOperationException("Search route contains an illegal action.");
        return (card, target);
    }

    private static string Describe(RunState run, uint token)
    {
        if (token == uint.MaxValue) return "END_TURN";
        var (card, target) = Resolve(run, token);
        return target == null ? card.Id.Entry : $"{card.Id.Entry}:{target.ModelId.Entry}";
    }

    private static async Task Execute(RunState run, uint token)
    {
        var player = run.Players.Single();
        bool Finished() => !CombatManager.Instance.IsInProgress && !Manager.ActionExecutor.IsRunning
            && Manager.ActionQueueSet.IsEmpty;
        if (token == uint.MaxValue)
        {
            int turn = player.PlayerCombatState!.TurnNumber;
            Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await Until(() => Finished() || (Stable(run) && player.PlayerCombatState!.TurnNumber > turn), "search end turn");
        }
        else
        {
            var (card, target) = Resolve(run, token);
            Manager.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
            await Until(() => Finished() || Stable(run), "search card");
        }
    }

    private static long Score(RunState run)
    {
        var player = run.Players.Single();
        if (player.Creature.IsDead) return long.MinValue;
        var combat = (run.CurrentRoom as CombatRoom)?.CombatState
            ?? throw new InvalidOperationException("Search left the combat room.");
        long enemyHp = combat.Enemies.Sum(c => (long)c.CurrentHp);
        return (!CombatManager.Instance.IsInProgress ? 1_000_000_000_000L : 0)
            + player.Creature.CurrentHp * 1_000_000L - enemyHp;
    }

    private static async Task<RunState> Restore(byte[] checkpoint, Dictionary<string, double> timings)
    {
        var timer = Stopwatch.StartNew();
        if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
        Manager.CleanUp();
        if (WorkerRuntime.Active && NRun.Instance != null)
        {
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
        if (WorkerRuntime.Active)
            NGame.Instance!.SetScreenShakeTarget(NGame.Instance.RootSceneContainer.CurrentScene!);
        TestMode.IsOn = WorkerRuntime.Active;
        timings["cleanup_ms"] = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var reader = new PacketReader();
        reader.Reset(checkpoint);
        var replay = reader.Read<CombatReplay>();
        var save = replay.serializableRun;
        var run = RunState.FromSerializable(save);
        timings["deserialize_ms"] = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await Manager.SetUpSavedSingleplayer(run, save);
        if (WorkerRuntime.Active)
        {
            Manager.CombatReplayWriter.IsEnabled = false;
            Manager.ChecksumTracker.IsEnabled = false;
        }
        timings["setup_and_save_ms"] = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        Manager.Launch();
        if (!WorkerRuntime.Active) NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        timings["scene_ms"] = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await Manager.GenerateMap();
        Manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
        Manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
        Manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
        Manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
        timings["map_ms"] = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await Manager.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(save.PreFinishedRoom, run));
        await Until(() => Stable(run), "restored combat");
        timings["enter_combat_ms"] = timer.Elapsed.TotalMilliseconds;
        return run;
    }

    private static bool Stable(RunState run) => CombatManager.Instance.IsInProgress
        && !Manager.NetService.IsGameLoading && !Manager.ActionExecutor.IsRunning
        && Manager.ActionQueueSet.IsEmpty && !CombatManager.Instance.IsStarting
        && !CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo
        && !CombatManager.Instance.PlayerActionsDisabled
        && run.Players.Single().PlayerCombatState?.Phase == PlayerTurnPhase.Play;

    private static string Fingerprint(RunState run)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(NetFullCombatState.FromRun(run, null).Anonymized());
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
        writer.ZeroByteRemainder();
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    private static async Task Until(Func<bool> ready, string stage)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException(stage);
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private sealed record Sample(bool Warmup, bool NonInteractive, bool Worker, bool Attack, double TotalMs, long ManagedBytes,
        Dictionary<string, double> Stages, List<string> Trajectory);
}

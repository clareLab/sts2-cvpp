using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace cvpp;

internal static class SnapshotProbe
{
    internal static async Task<object> Run()
    {
        if (ModManager.GetLoadedMods().Any(mod => mod.manifest?.id != "cvpp"))
            throw new NotSupportedException("The snapshot probe currently requires vanilla gameplay.");
        Engine.MaxFps = 0;
        await using var combat = new NativeCombat();
        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "SILENT");
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], "CVPP-REGRESSION-001", GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await combat.Until(() => combat.Stable(run), "snapshot fixture");
        var replay = (await combat.CaptureRoomStart()).Read();
        var player = replay.serializableRun.Players.Single();
        player.Deck = [Card<Survivor>(), Card<Prepared>(1), Card<BladeDance>(), Card<Reflex>(), Card<StrikeSilent>(), Card<DefendSilent>()];
        player.CurrentHp = player.MaxHp = 120;
        var checkpoint = new CombatCheckpoint(replay);
        combat.Mode = CombatExecution.Worker;
        run = await combat.Restore(checkpoint);
        string initial = combat.Fingerprint(run);
        var watch = Stopwatch.StartNew();
        using var snapshot = new SnapshotLoop(combat, run);
        double captureMs = watch.Elapsed.TotalMilliseconds;
        string[][] scripts =
        [
            ["STRIKE_SILENT", "END_TURN", "END_TURN"],
            ["BLADE_DANCE", "SHIV", "END_TURN", "END_TURN"],
            ["SURVIVOR", "SELECT:REFLEX", "END_TURN"],
            ["PREPARED", "SELECT:REFLEX,STRIKE_SILENT", "END_TURN"],
            ["END_TURN", "END_TURN", "END_TURN", "END_TURN"]
        ];
        var expected = new Dictionary<int, string[]>();
        var samples = new List<object>();
        for (int iteration = 0; iteration < 4; iteration++)
            for (int index = 0; index < scripts.Length; index++)
            {
                long allocated = GC.GetTotalAllocatedBytes(true);
                watch.Restart();
                await snapshot.Restore(combat);
                double restoreMs = watch.Elapsed.TotalMilliseconds;
                Require(combat.Fingerprint(run) == initial, "restored root");
                var states = new List<string>();
                foreach (string step in scripts[index])
                {
                    uint action = combat.Actions(run, includePotions: false).First(token =>
                    {
                        string description = combat.Describe(run, token);
                        return description == step || description.StartsWith(step + ":", StringComparison.Ordinal);
                    });
                    await combat.Execute(run, action);
                    states.Add(combat.Fingerprint(run));
                }
                double totalMs = watch.Elapsed.TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                if (expected.TryGetValue(index, out string[]? previous))
                    Require(states.SequenceEqual(previous), "repeated branch trace");
                else expected[index] = states.ToArray();
                samples.Add(new { iteration, script = index, restore_ms = restoreMs, total_ms = totalMs, allocated_bytes = bytes });
                GD.Print($"[cvpp] SNAPSHOT {iteration}/{index}: restore {restoreMs:F3} ms, total {totalMs:F3} ms");
            }
        foreach (int index in Enumerable.Range(0, scripts.Length))
        {
            run = await combat.Restore(checkpoint);
            var states = new List<string>();
            foreach (string step in scripts[index])
            {
                uint action = combat.Actions(run, includePotions: false).First(token =>
                {
                    string description = combat.Describe(run, token);
                    return description == step || description.StartsWith(step + ":", StringComparison.Ordinal);
                });
                await combat.Execute(run, action);
                states.Add(combat.Fingerprint(run));
            }
            Require(states.SequenceEqual(expected[index]), "independent official replay");
        }
        int objects = snapshot.Graph.Objects;
        int references = snapshot.Graph.References;
        snapshot.Dispose();
        Require(SnapshotNative.LiveBytes() == 0 && snapshot.Graph.Objects == 0 && snapshot.Graph.References == 0,
            "explicit release clears native bytes and managed roots");
        object paired = await Benchmark(combat, checkpoint);
        object guards = await Guards(combat, checkpoint);
        object terminal = await Terminal(combat, checkpoint);
        await combat.Restore(checkpoint);
        await ((SceneTree)Engine.GetMainLoop()).ToSignal((SceneTree)Engine.GetMainLoop(), SceneTree.SignalName.ProcessFrame);
        object registry = await RegistryRegressions.Run(combat, checkpoint);
        object corpus = await Corpus(combat);
        var report = new
        {
            capture_ms = captureMs,
            objects,
            references,
            native_bytes = snapshot.Graph.Bytes,
            boundaries = snapshot.Graph.Boundaries,
            runtime_boundaries = SnapshotGraph.RuntimeBoundaries,
            scripts,
            samples,
            independently_verified = true,
            paired,
            guards,
            terminal,
            registry,
            corpus,
            game_mvid = typeof(RunState).Assembly.ManifestModule.ModuleVersionId,
            checkpoint = checkpoint.Digest,
            scope = "isolated vanilla stable snapshots with terminal rollback and choice replay; prototype only"
        };
        return report;
    }

    private static async Task<object> Corpus(NativeCombat combat)
    {
        var cases = new List<object>();
        foreach (var character in ModelDb.AllCharacters)
        {
            await combat.Reset();
            SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
            var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
                ActModel.GetDefaultList(), [], "CVPP-NATIVE-002", GameMode.Standard);
            var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
            await combat.Until(() => combat.Stable(run), "snapshot corpus combat");
            var checkpoint = await combat.CaptureRoomStart();
            combat.Mode = CombatExecution.Worker;
            run = await combat.Restore(checkpoint);
            uint attack = combat.Actions(run, includePotions: false).First(token => token != NativeCombat.EndTurn
                && combat.Resolve(run, token).Card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack);
            uint[] path = [attack, NativeCombat.EndTurn, NativeCombat.EndTurn];
            var expected = new List<string>();
            using (var snapshot = new SnapshotLoop(combat, run))
            {
                Require(snapshot.Graph.Runs.Length == 1, "snapshot captures only one run");
                for (int iteration = 0; iteration < 4; iteration++)
                {
                    await snapshot.Restore(combat);
                    for (int step = 0; step < path.Length; step++)
                    {
                        await combat.Execute(run, path[step]);
                        string state = combat.Fingerprint(run);
                        if (iteration == 0) expected.Add(state);
                        else Require(expected[step] == state, "character snapshot trajectory");
                    }
                }
            }
            combat.Mode = CombatExecution.Reference;
            run = await combat.Restore(checkpoint);
            for (int step = 0; step < path.Length; step++)
            {
                await combat.Execute(run, path[step]);
                Require(combat.Fingerprint(run) == expected[step], "character official reference trajectory");
            }
            cases.Add(new { character = character.Id.Entry, checkpoint = checkpoint.Digest, compared_states = 15 });
        }
        return cases;
    }

    private static async Task<object> Benchmark(NativeCombat combat, CombatCheckpoint checkpoint)
    {
        string[] prefix = ["BLADE_DANCE", "SHIV", "END_TURN", "END_TURN", "END_TURN"];
        var captures = new List<object>();
        var samples = new List<object>();
        string? expected = null;
        for (int batch = 0; batch < 3; batch++)
        {
            foreach (bool useSnapshot in batch % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var run = await combat.Restore(checkpoint);
                foreach (string step in prefix) await Step(combat, run, step);
                long allocated = GC.GetTotalAllocatedBytes(true);
                long started = Stopwatch.GetTimestamp();
                using var snapshot = useSnapshot ? new SnapshotLoop(combat, run) : null;
                if (snapshot != null)
                    captures.Add(new
                    {
                        batch,
                        capture_ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        allocated_bytes = GC.GetTotalAllocatedBytes(true) - allocated,
                        snapshot.Graph.Objects,
                        snapshot.Graph.References,
                        snapshot.Graph.Types,
                        snapshot.Graph.Runs,
                        native_bytes = snapshot.Graph.Bytes
                    });
                for (int iteration = -1; iteration < 8; iteration++)
                {
                    started = Stopwatch.GetTimestamp();
                    allocated = GC.GetTotalAllocatedBytes(true);
                    if (snapshot != null) await snapshot.Restore(combat);
                    else
                    {
                        run = await combat.Restore(checkpoint);
                        foreach (string step in prefix) await Step(combat, run, step);
                    }
                    double restoreMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long restoreBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    await combat.Execute(run, NativeCombat.EndTurn);
                    double totalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long totalBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    string actual = combat.Fingerprint(run);
                    expected ??= actual;
                    Require(expected == actual, "paired benchmark future trajectory");
                    samples.Add(new
                    {
                        batch,
                        iteration,
                        backend = useSnapshot ? "snapshot" : "root_replay",
                        restore_ms = restoreMs,
                        total_ms = totalMs,
                        restore_allocated_bytes = restoreBytes,
                        total_allocated_bytes = totalBytes
                    });
                }
            }
        }
        Require(SnapshotNative.LiveBytes() == 0, "paired benchmark releases all snapshots");
        return new { prefix, action = "END_TURN", captures, samples, verified = true };
    }

    private static async Task<object> Guards(NativeCombat combat, CombatCheckpoint checkpoint)
    {
        var run = await combat.Restore(checkpoint);
        using var root = new SnapshotLoop(combat, run);
        string initial = combat.Fingerprint(run);
        await Step(combat, run, "BLADE_DANCE");
        using var generated = new SnapshotLoop(combat, run, root);
        ulong sharedBytes = generated.Graph.SharedBytes;
        ulong liveBytes = SnapshotNative.LiveBytes();
        Require(sharedBytes > 0 && liveBytes == (ulong)(root.Graph.Bytes + generated.Graph.Bytes) - sharedBytes,
            "native snapshots share unchanged pages");
        string generatedState = combat.Fingerprint(run);
        for (int index = 0; index < 8; index++)
        {
            await root.Restore(combat);
            Require(combat.Fingerprint(run) == initial, "root checkpoint after generated branch");
            await Step(combat, run, "END_TURN");
            await generated.Restore(combat);
            Require(combat.Fingerprint(run) == generatedState, "generated object identities survive checkpoint switches");
            await Step(combat, run, "SHIV");
            await Step(combat, run, "END_TURN");
        }
        await root.Restore(combat);
        await Step(combat, run, "SURVIVOR");
        Reject(() => new SnapshotLoop(combat, run).Dispose());
        await root.Restore(combat);
        Require(!combat.HasChoice && combat.Fingerprint(run) == initial, "abandoned choice drains before snapshot restore");
        await Step(combat, run, "SURVIVOR");
        await Step(combat, run, "SELECT:REFLEX");
        await root.Restore(combat);
        Require(combat.Fingerprint(run) == initial, "resolved choice restores its original decision");
        for (int turn = 0; turn < 64 && !combat.Finished; turn++)
            await combat.Execute(run, NativeCombat.EndTurn);
        Require(combat.Finished, "terminal guard fixture");
        await root.Restore(combat);
        Require(!combat.Finished && !combat.Victory && combat.Fingerprint(run) == initial, "terminal rollback resets outcome");
        root.Dispose();
        await generated.Restore(combat);
        Require(combat.Fingerprint(run) == generatedState && SnapshotNative.LiveBytes() == (ulong)generated.Graph.Bytes,
            "shared checkpoint survives parent disposal");
        run = await combat.Restore(checkpoint);
        await RejectAsync(() => generated.Restore(combat));
        generated.Dispose();
        root.Dispose();
        await RejectAsync(() => root.Restore(combat));
        Require(SnapshotNative.LiveBytes() == 0, "disposed snapshots hold no native allocation");
        WeakReference abandoned = Abandon(combat, run);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(!abandoned.IsAlive && SnapshotNative.LiveBytes() == 0, "SafeHandle finalization releases abandoned snapshot");
        return new
        {
            checkpoint_switches = 16,
            pending_choice_capture_rejected = true,
            pending_choice_rollback = true,
            terminal_rollback = true,
            stale_session_rejected = true,
            disposed_snapshot_rejected = true,
            native_bytes_after_dispose_and_gc = SnapshotNative.LiveBytes(),
            managed_roots_released = true,
            shared_bytes = sharedBytes,
            combined_native_bytes = liveBytes
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(NativeCombat combat, RunState run) => new(new SnapshotLoop(combat, run));

    private static async Task<object> Terminal(NativeCombat combat, CombatCheckpoint original)
    {
        var cases = new List<object>();
        foreach (bool victory in new[] { false, true })
        {
            var replay = original.Read();
            var player = replay.serializableRun.Players.Single();
            player.Deck = victory ? Enumerable.Range(0, 5).Select(_ => Card<Bludgeon>(1)).ToList() : [Card<DefendSilent>()];
            player.CurrentHp = victory ? 60 : 1;
            player.MaxHp = 120;
            player.Potions.Clear();
            var checkpoint = new CombatCheckpoint(replay);
            combat.Mode = CombatExecution.Worker;
            var run = await combat.Restore(checkpoint);
            string initial = combat.Fingerprint(run);
            string progress = JsonSerializer.Serialize(SaveManager.Instance.Progress.ToSerializable());
            long winTime = RunManager.Instance.WinTime;
            var path = new List<uint>();
            var expected = new List<string>();
            var details = new List<string>();
            string? ending = null;
            using (var snapshot = new SnapshotLoop(combat, run))
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    await snapshot.Restore(combat);
                    Require(!combat.Victory && combat.Fingerprint(run) == initial, "terminal checkpoint root");
                    Require(JsonSerializer.Serialize(SaveManager.Instance.Progress.ToSerializable()) == progress
                        && RunManager.Instance.WinTime == winTime, "terminal rollback restores progression and victory time");
                    for (int step = 0; step < 24 && !combat.Finished; step++)
                    {
                        uint action = iteration > 0 ? path[step] : victory
                            ? combat.Actions(run, includePotions: false).FirstOrDefault(token => token != NativeCombat.EndTurn, NativeCombat.EndTurn)
                            : NativeCombat.EndTurn;
                        await combat.Execute(run, action);
                        string fingerprint = combat.Fingerprint(run);
                        if (iteration == 0)
                        {
                            path.Add(action);
                            expected.Add(fingerprint);
                            details.Add(NetFullCombatState.FromRun(run, null).ToString());
                        }
                        else Require(expected[step] == fingerprint, "repeated terminal trajectory");
                    }
                    Require(combat.Finished && combat.Victory == victory, "terminal checkpoint result");
                    ending ??= TerminalState(run);
                }
            run = await combat.Restore(checkpoint);
            for (int step = 0; step < path.Count; step++)
            {
                await combat.Execute(run, path[step]);
                Require(combat.Fingerprint(run) == expected[step], "terminal snapshot matches independent worker replay");
            }
            combat.Mode = CombatExecution.Reference;
            run = await combat.Restore(checkpoint);
            for (int step = 0; step < path.Count; step++)
            {
                await combat.Execute(run, path[step]);
                Require(combat.Finished ? TerminalState(run) == ending : combat.Fingerprint(run) == expected[step],
                    $"terminal reference {victory}/{step}\nExpected:\n{details[step]}\nActual:\n{NetFullCombatState.FromRun(run, null)}");
            }
            Require(combat.Finished && combat.Victory == victory, "terminal reference outcome");
            cases.Add(new
            {
                victory,
                restores = 8,
                steps = path.Count,
                hp = run.Players[0].Creature.CurrentHp,
                full_worker_trace_matches = true,
                normal_reference_terminal_exclusions = "reward-screen sequence IDs and post-combat reward/shop RNG"
            });
        }
        combat.Mode = CombatExecution.Worker;
        return cases;
    }

    private static string TerminalState(RunState run)
    {
        var state = NetFullCombatState.FromRun(run, null);
        state.nextRewardIds.Clear();
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(state);
        writer.ZeroByteRemainder();
        return Convert.ToHexString(writer.Buffer.AsSpan(0, writer.BytePosition));
    }

    private static async ValueTask Step(NativeCombat combat, RunState run, string step)
    {
        uint action = combat.Actions(run, includePotions: false).First(token =>
        {
            string description = combat.Describe(run, token);
            return description == step || description.StartsWith(step + ":", StringComparison.Ordinal);
        });
        await combat.Execute(run, action);
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe snapshot operation was accepted.");
    }

    private static async ValueTask RejectAsync(Func<ValueTask> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe snapshot operation was accepted.");
    }

    private static SerializableCard Card<T>(int upgrades = 0) where T : CardModel
    {
        var card = ModelDb.Card<T>().ToMutable();
        for (int index = 0; index < upgrades; index++) card.UpgradeInternal();
        return card.ToSerializable();
    }

    private static void Require(bool condition, string stage)
    {
        if (!condition) throw new InvalidOperationException("Snapshot mismatch: " + stage);
    }
}

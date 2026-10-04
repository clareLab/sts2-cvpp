using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace cvpp;

internal static class WorkerBenchmarks
{
    private sealed class Progress(Action<SolveProgress> report) : IProgress<SolveProgress>
    {
        public void Report(SolveProgress value) => report(value);
    }

    internal static async Task<object> Run(string path)
    {
        RunState run;
        if (File.Exists(path))
        {
            var save = JsonSerializer.Deserialize(File.ReadAllText(path), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
            run = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
            await NGame.Instance!.LoadRun(run, save.PreFinishedRoom);
        }
        else
        {
            var character = ModelDb.AllCharacters.Single(model => model.Id.Entry == "SILENT");
            SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
            run = await NGame.Instance!.StartNewSingleplayerRun(character, true, ActModel.GetDefaultList(), [], "CVPP-WORKER-001", GameMode.Standard);
            var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
        }
        await ProductTests.Until(() => SolverController.Ready && !NGame.Instance.Transition.InTransition, "benchmark combat");
        var position = await CombatPosition.Capture();
        var setup = WorkerEnvironment.Capture();
        await using var worker = new WorkerClient(OS.GetExecutablePath(), Path.GetDirectoryName(typeof(Entry).Assembly.Location)!,
            ProjectSettings.GlobalizePath("user://cvpp-workers"), setup);
        var trials = new List<object>();
        var options = OS.GetCmdlineArgs().Contains("--cvpp-fixed-work")
            ? new SolveOptions(0, Nodes: 128, MemoryMiB: 4096) : new SolveOptions(5, MemoryMiB: 4096);
        CombatPlan? plan = null;
        for (int trial = 0; trial < 3; trial++)
        {
            var clock = Stopwatch.StartNew();
            double firstProgressMs = 0;
            long peakMemory = 0;
            var progress = new Progress(value =>
            {
                if (firstProgressMs == 0) firstProgressMs = clock.Elapsed.TotalMilliseconds;
                peakMemory = Math.Max(peakMemory, value.MemoryBytes);
            });
            var result = await worker.Solve(new SolveRequest(position, options), progress, default);
            plan = result.Plan ?? throw new InvalidOperationException("Benchmark found no winning route.");
            if (CombatFingerprint.Capture(run) != position.State) throw new InvalidOperationException("Benchmark changed the live combat.");
            trials.Add(new
            {
                trial,
                wallMs = clock.Elapsed.TotalMilliseconds,
                firstProgressMs,
                peakMemory,
                result.ElapsedMs,
                result.Restores,
                result.Actions,
                simulations = result.Stats.Simulations,
                perSecond = result.Stats.Simulations * 1000 / result.ElapsedMs,
                plan.FinalHp
            });
        }
        await using (var combat = new NativeCombat(live: true))
        {
            foreach (var step in plan!.Steps)
            {
                if (combat.Fingerprint(run) != step.Before) throw new InvalidOperationException("Benchmark route diverged before execution.");
                await combat.Execute(run, step.Action);
                if (!combat.Finished && combat.Fingerprint(run) != step.After) throw new InvalidOperationException("Benchmark route diverged after execution.");
            }
            if (!combat.Victory || run.Players[0].Creature.CurrentHp != plan.FinalHp) throw new InvalidOperationException("Benchmark route did not reproduce.");
        }
        Engine.MaxFps = 0;
        var samples = new List<object>();
        string saveDirectory = ProjectSettings.GlobalizePath(SaveManager.Instance.GetProfileScopedPath("saves"));
        var saved = Directory.GetFiles(saveDirectory, "*.save").ToDictionary(file => file, File.ReadAllBytes);
        await using (var combat = new NativeCombat { Mode = CombatExecution.Worker })
        {
            for (int sample = 0; sample < 4; sample++)
            {
                var stages = new Dictionary<string, double>();
                run = await combat.Restore(CombatCheckpoint.Import(position.Root), stages);
                foreach (var step in plan.Steps)
                {
                    var clock = Stopwatch.StartNew();
                    await combat.Execute(run, step.Action);
                    string stage = combat.Finished ? "victory_ms" : step.Kind + "_ms";
                    stages[stage] = stages.GetValueOrDefault(stage) + clock.Elapsed.TotalMilliseconds;
                }
                samples.Add(stages);
                if (!combat.Victory || run.Players[0].Creature.CurrentHp != plan.FinalHp)
                    throw new InvalidOperationException("Profiled replay changed the winning result.");
            }
        }
        if (saved.Count != Directory.GetFiles(saveDirectory, "*.save").Length
            || saved.Any(entry => !File.ReadAllBytes(entry.Key).AsSpan().SequenceEqual(entry.Value)))
            throw new InvalidOperationException("Headless replay wrote run or progress saves.");
        return new
        {
            mods = setup.Mods.Select(mod => mod.Id).ToArray(),
            options,
            trials,
            samples,
            verified = true,
            savesUnchanged = true,
            snapshot = System.Environment.GetEnvironmentVariable("CVPP_SNAPSHOT_PROBE") == "1"
        };
    }
}

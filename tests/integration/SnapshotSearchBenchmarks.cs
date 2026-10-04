using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class SnapshotSearchBenchmarks
{
    internal static async Task<object> Capture(NativeCombat combat, CombatCheckpoint checkpoint)
    {
        combat.Mode = CombatExecution.Worker;
        var run = await combat.Restore(checkpoint);
        ulong[]? expected = null;
        var samples = new List<object>();
        for (int iteration = -1; iteration < 8; iteration++)
            foreach (bool compiled in iteration % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                long allocated = GC.GetTotalAllocatedBytes(true);
                long started = Stopwatch.GetTimestamp();
                using var snapshot = new SnapshotLoop(combat, run, compiled: compiled);
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                ulong[] words = snapshot.Graph.Words();
                expected ??= words;
                if (!words.SequenceEqual(expected)) throw new InvalidOperationException("Compiled game snapshot differs from reflection.");
                if (iteration >= 0) samples.Add(new { iteration, compiled, milliseconds, allocated_bytes = bytes });
            }
        return new { samples, identical_native_words = true };
    }

    internal static async Task<object> Lifecycle(NativeCombat combat, CombatCheckpoint checkpoint)
    {
        combat.Mode = CombatExecution.Worker;
        using var root = new SnapshotSearch(combat, () => combat.Restore(checkpoint));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pause = new SearchPause();
        var waiting = new TaskCompletionSource<SolveProgress>();
        SolveProgress? frozen = null;
        bool requested = false;
        var task = HealthSearch.Run(combat, root.Restore, new SolveOptions(0, 1_000_000, 96), progress =>
        {
            if (progress.Plan != null && !requested) { requested = true; pause.Set(true); }
            if (progress.Paused) { frozen = progress; waiting.TrySetResult(progress); }
            else if (frozen != null && progress.Simulations > frozen.Simulations) cancellation.Cancel();
        }, cancellation.Token, pause: pause);
        try
        {
            var before = await waiting.Task.WaitAsync(cancellation.Token);
            ulong bytes = SnapshotNative.LiveBytes();
            uint restores = root.Restores;
            await Task.Delay(1200, cancellation.Token);
            if (task.IsCompleted || root.Restores != restores || SnapshotNative.LiveBytes() != bytes
                || frozen!.Simulations != before.Simulations || frozen.ElapsedMs != before.ElapsedMs)
                throw new InvalidOperationException("Paused snapshot search changed its retained state.");
            pause.Set(false);
            var result = await task;
            if (result.StopReason != "cancelled" || result.Stats.Simulations <= before.Simulations || result.Plan?.FinalHp < before.BestHp)
                throw new InvalidOperationException("Snapshot resume or cancellation lost search progress.");
            root.Dispose();
            if (SnapshotNative.LiveBytes() != 0 || root.Bytes != 0 || root.References != 0)
                throw new InvalidOperationException("Cancelled search retained its snapshot.");
            return new { paused_simulations = before.Simulations, resumed_simulations = result.Stats.Simulations, native_bytes_after_dispose = SnapshotNative.LiveBytes() };
        }
        finally { cancellation.Cancel(); await task; }
    }

    internal static async Task<object> Run(NativeCombat combat, CombatCheckpoint checkpoint, uint[]? prefix = null)
    {
        prefix ??= [];
        async ValueTask<RunState> Restore()
        {
            var run = await combat.Restore(checkpoint);
            foreach (uint action in prefix) await combat.Execute(run, action);
            return run;
        }
        var options = new SolveOptions(0, Nodes: 128, Depth: 96);
        var trials = new List<object>();
        SolveResult? expected = null;
        string? expectedTrace = null;
        using var process = Process.GetCurrentProcess();
        for (int round = 0; round < 3; round++)
            foreach (string backend in round % 2 == 0 ? new[] { "root_replay", "snapshot" } : new[] { "snapshot", "root_replay" })
            {
                combat.Mode = CombatExecution.Worker;
                await Restore();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocated = GC.GetTotalAllocatedBytes(true);
                long managed = GC.GetTotalMemory(false);
                int[] collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                double cpu = process.TotalProcessorTime.TotalMilliseconds;
                long started = Stopwatch.GetTimestamp();
                using var root = backend == "snapshot" ? new SnapshotSearch(combat, Restore) : null;
                Func<ValueTask<RunState>> restore = root == null ? Restore : root.Restore;
                using var trace = new SearchTrace();
                var result = await HealthSearch.Run(combat, restore, options);
                double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                process.Refresh();
                string fingerprint = trace.Finish();
                expectedTrace ??= fingerprint;
                if (fingerprint != expectedTrace) throw new InvalidOperationException("Snapshot rollout sequence or scores differ from independent root replay.");
                if (expected == null) expected = result;
                else if (result.Stats != expected.Stats || result.StopReason != expected.StopReason
                    || result.Plan?.FinalHp != expected.Plan?.FinalHp
                    || !(result.Plan?.Steps ?? []).SequenceEqual(expected.Plan?.Steps ?? []))
                    throw new InvalidOperationException("Snapshot search differs from independent root replay.");
                var sample = new
                {
                    round,
                    backend,
                    trace = fingerprint,
                    wall_ms = wallMs,
                    cpu_ms = process.TotalProcessorTime.TotalMilliseconds - cpu,
                    allocated_bytes = bytes,
                    managed_growth_bytes = GC.GetTotalMemory(false) - managed,
                    resident_bytes = process.WorkingSet64,
                    collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - collections[index]).ToArray(),
                    result.Stats.Simulations,
                    result.Stats.Nodes,
                    result.Restores,
                    result.Actions,
                    result.StopReason,
                    hp = result.Plan?.FinalHp,
                    per_second = result.Stats.Simulations * 1000 / wallMs,
                    capture_ms = root?.CaptureMs ?? 0,
                    snapshot_restore_ms = root?.RestoreMs ?? 0,
                    native_bytes = root?.Bytes ?? 0,
                    managed_references = root?.References ?? 0
                };
                trials.Add(sample);
                root?.Dispose();
                if (SnapshotNative.LiveBytes() != 0) throw new InvalidOperationException("Search retained native snapshots after disposal.");
                GD.Print($"[cvpp] SNAPSHOT SEARCH {sample.backend}: {wallMs:F2} ms, {result.Stats.Simulations} simulations, {sample.hp} HP");
            }
        if (expected?.Plan is { } plan)
        {
            combat.Mode = CombatExecution.Reference;
            var run = await Restore();
            foreach (var step in plan.Steps)
            {
                if (combat.Fingerprint(run) != step.Before) throw new InvalidOperationException("Snapshot route diverged before official execution.");
                await combat.Execute(run, step.Action);
                if (!combat.Finished && combat.Fingerprint(run) != step.After)
                    throw new InvalidOperationException("Snapshot route diverged after official execution.");
            }
            if (!combat.Finished || !combat.Victory || run.Players[0].Creature.CurrentHp != plan.FinalHp)
                throw new InvalidOperationException($"Snapshot route did not reproduce its winning HP: finished={combat.Finished}, victory={combat.Victory}, hp={run.Players[0].Creature.CurrentHp}/{plan.FinalHp}, last={plan.Steps[^1].Label}.");
        }
        combat.Mode = CombatExecution.Worker;
        return new { options, prefix, trials, checkpoint = checkpoint.Digest, identical_search_and_route = true, official_execution_verified = expected?.Plan != null };
    }
}

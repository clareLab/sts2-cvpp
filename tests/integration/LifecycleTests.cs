using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class LifecycleTests
{
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    private static string Cache => ProjectSettings.GlobalizePath("user://cvpp-workers");

    internal static async Task Faults(CombatPosition position)
    {
        string directory = ProjectSettings.GlobalizePath("user://cvpp-faults");
        string stale = Path.Combine(directory, "cvpp-stale");
        string active = Path.Combine(directory, "cvpp-active");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(stale, ".cvpp-worker"), "");
        using var lease = new FileStream(Path.Combine(active, ".cvpp-worker"), FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.None);
        var setup = WorkerEnvironment.Capture();
        var request = new SolveRequest(position, new SolveOptions(5));
        await using (var worker = new WorkerClient(OS.GetExecutablePath(), Path.Combine(directory, "missing"), directory, setup))
        {
            try { await worker.Solve(request, null, default); throw new InvalidOperationException("Missing package was accepted."); }
            catch (DirectoryNotFoundException) { }
        }
        if (Directory.Exists(stale) || !Directory.Exists(active)) throw new InvalidOperationException("Stale cleanup did not respect active ownership.");
        lease.Dispose();
        Directory.Delete(active, recursive: true);
        Empty(directory);
        await using (var worker = new WorkerClient(OS.GetExecutablePath(), Path.GetDirectoryName(typeof(Entry).Assembly.Location)!, directory, setup))
        {
            var solve = worker.Solve(request, null, default);
            await Task.Delay(10);
            var first = worker.DisposeAsync().AsTask();
            var second = worker.DisposeAsync().AsTask();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
            try { await solve; throw new InvalidOperationException("Disposed search continued."); }
            catch (OperationCanceledException) { }
        }
        Empty(directory);
        await using (var worker = new WorkerClient(OS.GetExecutablePath(), Path.GetDirectoryName(typeof(Entry).Assembly.Location)!, directory, setup))
        {
            var limited = await worker.Solve(request with { Options = new SolveOptions(0, MemoryMiB: 64) }, null, default);
            if (limited.StopReason != "memory_limit" || worker.ProcessId != null)
                throw new InvalidOperationException("Startup memory budget did not retire the worker.");
        }
        Empty(directory);
    }

    internal static async Task Exit(string mode)
    {
        SolverController.Seconds = 60;
        SolverController.Solve();
        await ProductTests.Until(() => mode == "startup" ? SolverController.WorkerPid != null
            : SolverController.Progress != null || !SolverController.Busy, "exit probe", 100);
        int worker = SolverController.WorkerPid ?? throw new InvalidOperationException(SolverController.Error ?? "Missing worker.");
        string sandbox = Directory.GetDirectories(Cache, "cvpp-*").Single();
        string path = ProjectSettings.GlobalizePath("user://cvpp-exit.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { parent = System.Environment.ProcessId, worker, sandbox }));
        File.Move(path + ".tmp", path);
        if (mode == "menu")
        {
            RunManager.Instance.CleanUp();
            await ProductTests.Until(() => !SolverController.Busy && SolverController.Plan == null, "leave active search");
            await Released(worker);
        }
        if (mode is "normal" or "menu") Tree.Quit();
        await new TaskCompletionSource().Task;
    }

    internal static async Task Released(int pid)
    {
        await ProductTests.Until(() => SolverController.WorkerReleased, "worker retirement", 15);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) throw new InvalidOperationException("Retired worker is still running.");
        }
        catch (ArgumentException) { }
        Empty(Cache);
    }

    internal static void Empty(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateDirectories(directory, "cvpp-*").Any())
            throw new InvalidOperationException("Worker sandbox survived cleanup: " + directory);
    }

    internal static async Task<object> Repeat(RunState run, int pid)
    {
        int guardian = int.Parse(File.ReadAllText($"/proc/{pid}/stat").Split(") ")[1].Split(' ')[1]);
        using var supervisor = Process.GetProcessById(guardian);
        var supervisorCpu = supervisor.TotalProcessorTime;
        var residentBytes = new List<long>();
        var handles = new List<int>();
        for (int cycle = 0; cycle < 5; cycle++)
        {
            var plan = SolverController.Plan ?? throw new InvalidOperationException("Missing incumbent.");
            string before = CombatFingerprint.Capture(run);
            SolverController.Seconds = 1;
            SolverController.Solve();
            await ProductTests.Until(() => !SolverController.Busy, "repeated search", 15);
            if (SolverController.Error != null || SolverController.Plan is not { } next || next.FinalHp < plan.FinalHp
                || SolverController.WorkerPid != pid || CombatFingerprint.Capture(run) != before)
                throw new InvalidOperationException(SolverController.Error ?? "Repeated search did not preserve worker, route and live state.");
            using var process = Process.GetProcessById(pid);
            if (process.PriorityClass != ProcessPriorityClass.BelowNormal) throw new InvalidOperationException("Worker priority was not lowered.");
            residentBytes.Add(process.WorkingSet64);
            handles.Add(Directory.GetFileSystemEntries($"/proc/{pid}/fd").Length);
        }
        if (handles[^1] > handles[0] + 4) throw new InvalidOperationException("Worker handle count grew across repeated solves.");
        supervisor.Refresh();
        return new { residentBytes, handles, supervisorBytes = supervisor.WorkingSet64, supervisorCpuMs = (supervisor.TotalProcessorTime - supervisorCpu).TotalMilliseconds };
    }
}

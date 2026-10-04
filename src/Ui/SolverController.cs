using System.Diagnostics;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace cvpp;

internal enum ExecutionRange { Step, Turn, Combat }

internal static class SolverController
{
    private static WorkerClient? _worker;
    private static WorkerClient? _retiring;
    private static Task _release = Task.CompletedTask;
    private static CancellationTokenSource? _cancel;
    private static Task? _operation;
    private static readonly Stopwatch Clock = new();
    private static readonly SearchRate Rate = new();
    private static bool _installed;
    private static bool _initialized;
    private static RunState? _run;
    private static object? _room;
    private static int _generation;
    private static ulong _idleSince;
    internal static ulong IdleMilliseconds { get; set; } = 120_000;
    internal static int? WorkerPid => _worker?.ProcessId;
    internal static bool WorkerReleased => _worker == null && _release.IsCompleted;
    internal static bool Busy => _operation is { IsCompleted: false };
    internal static bool Executing { get; private set; }
    internal static bool Resetting { get; private set; }
    internal static ExecutionRange? ActiveRange { get; private set; }
    internal static string Status { get; private set; } = "Ready";
    internal static string? Error { get; private set; }
    internal static SolveProgress? Progress { get; private set; }
    internal static CombatPlan? Plan { get; private set; }
    internal static CombatPlan? Preview { get; private set; }
    internal static string? StopReason { get; private set; }
    internal static int Step { get; private set; }
    internal static int Seconds { get; set; } = 15;
    internal static int SearchSeconds { get; private set; }
    internal static int MemoryMiB { get; set; } = 2048;
    internal static double Elapsed => Clock.Elapsed.TotalSeconds;
    internal static double SimulationsPerSecond => Busy && !Executing && !Resetting ? Rate.PerSecond : 0;
    internal static bool Ready => RunManager.Instance.DebugOnlyGetState() is { } run && run.Players.Count == 1
        && RunManager.Instance.NetService?.Type == NetGameType.Singleplayer && NativeCombat.IsStable(run);

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        new Harmony("clarelab.cvpp.input").CreateClassProcessor(typeof(SolverInputPatch)).Patch();
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += Tick;
        tree.Root.TreeExiting += Shutdown;
    }

    private static void Tick()
    {
        try
        {
            if (!_installed && NGame.Instance?.MainMenu != null && SaveManager.Instance.IsProfileInitialized)
            {
                SolverHud.Install();
                _installed = true;
            }
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run != _run || run?.CurrentRoom != _room)
            {
                Stop();
                _generation++;
                ReleaseWorker();
                _run = run;
                _room = run?.CurrentRoom;
                Plan = Preview = null;
                StopReason = null;
                Step = 0;
                Progress = null;
                Rate.Reset();
                Status = "Ready";
                Error = null;
            }
            if (!Busy)
            {
                _operation = null;
                _cancel?.Dispose();
                _cancel = null;
                if (!CombatManager.Instance.IsInProgress || Time.GetTicksMsec() - _idleSince >= IdleMilliseconds) ReleaseWorker();
            }
            SolverHud.Tick();
        }
        catch (Exception error)
        {
            Error = error.Message;
            GD.PrintErr("[cvpp] UI: " + error);
            Stop();
            _generation++;
            ReleaseWorker();
            Plan = Preview = null;
            Progress = null;
            Rate.Reset();
            ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= Tick;
            SolverHud.Disable();
        }
    }

    internal static void Solve(bool takeOver = false) => Launch(async () =>
    {
        if (!Ready) throw new InvalidOperationException("Wait for your turn and finish any open card selection.");
        int generation = _generation;
        var previous = Plan?.Steps.Skip(Step).ToArray();
        Progress = null;
        Rate.Reset();
        Preview = null;
        StopReason = null;
        SearchSeconds = Seconds;
        Status = "Loading";
        var position = await CombatPosition.Capture();
        await _release;
        if (generation != _generation) return;
        _cancel!.Token.ThrowIfCancellationRequested();
        uint[]? incumbent = previous is { Length: > 0 } && previous[0].Before == position.State
            ? previous.Select(step => step.Action).ToArray() : null;
        if (incumbent == null) { Plan = null; Step = 0; }
        _worker ??= new WorkerClient(OS.GetExecutablePath(), Path.GetDirectoryName(typeof(Entry).Assembly.Location)!,
            ProjectSettings.GlobalizePath("user://cvpp-workers"), WorkerEnvironment.Capture());
        bool acceptingProgress = true;
        var updates = new Progress<SolveProgress>(progress =>
        {
            if (!acceptingProgress || generation != _generation) return;
            Progress = progress;
            Rate.Observe(progress.Simulations, progress.ElapsedMs);
            if (progress.Plan is { } preview && (Preview == null || preview.FinalHp > Preview.FinalHp
                || (preview.FinalHp == Preview.FinalHp && preview.Steps.Length < Preview.Steps.Length))) Preview = preview;
            Status = "Searching";
        });
        SolveResult result;
        try { result = await _worker.Solve(new SolveRequest(position, new SolveOptions(SearchSeconds, Nodes: 1_000_000, Depth: 256, MemoryMiB: MemoryMiB), incumbent), updates, _cancel!.Token); }
        finally { acceptingProgress = false; }
        if (generation != _generation) return;
        if (!Ready || CombatFingerprint.Capture(RunManager.Instance.DebugOnlyGetState()!) != position.State)
        {
            Plan = Preview = null;
            Step = 0;
            Status = "Combat changed";
            return;
        }
        Plan = Preview = result.Plan;
        if (result.StopReason != "memory_limit")
            Progress = new SolveProgress(result.Stats.Simulations, result.Stats.Nodes, result.Plan?.FinalHp,
                result.ElapsedMs, result.Plan, Progress?.MemoryBytes ?? 0);
        Step = 0;
        StopReason = result.StopReason;
        Status = result.StopReason switch
        {
            "time_limit" => "Time limit",
            "memory_limit" => "Memory limit",
            "node_or_depth_limit" => "Search limit",
            "exhausted" => "Exhausted",
            "cancelled" => "Paused",
            _ => "Stopped"
        };
        if (result.StopReason == "memory_limit") ReleaseWorker();
        if (takeOver && Plan != null && !_cancel.IsCancellationRequested) await Execute(ExecutionRange.Combat);
    }, takeOver ? ExecutionRange.Combat : null);

    internal static void Play(ExecutionRange range)
    {
        if (Plan == null)
        {
            if (range == ExecutionRange.Combat) Solve(takeOver: true);
            return;
        }
        Launch(() => Execute(range), range);
    }

    private static async Task Execute(ExecutionRange range)
    {
        var plan = Plan ?? throw new InvalidOperationException("Solve this combat first.");
        var run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("The combat has ended.");
        if (Step >= plan.Steps.Length) return;
        if (!Ready || CombatFingerprint.Capture(run) != plan.Steps[Step].Before)
        {
            Plan = null;
            throw new InvalidOperationException("Combat changed. Solve again before executing.");
        }
        Executing = true;
        try
        {
            await using var combat = new NativeCombat(live: true);
            int initialTurn = plan.Steps[Step].Turn;
            int startingStep = Step;
            while (Step < plan.Steps.Length)
            {
                var action = plan.Steps[Step];
                if (!combat.HasChoice && (_cancel!.IsCancellationRequested
                    || (range == ExecutionRange.Step && Step > startingStep)
                    || (range == ExecutionRange.Turn && action.Turn != initialTurn))) break;
                if (combat.Fingerprint(run) != action.Before) throw new InvalidOperationException("Combat changed. Execution stopped.");
                Status = action.Label;
                await combat.Execute(run, action.Action);
                bool matches = combat.Finished
                    ? Step == plan.Steps.Length - 1 && combat.Victory && run.Players.Single().Creature.CurrentHp == plan.FinalHp
                    : combat.Fingerprint(run) == action.After;
                if (!matches) throw new InvalidOperationException("The result differs from the route. Execution stopped.");
                Step++;
            }
            Status = Step == plan.Steps.Length ? "Victory" : "Paused";
        }
        catch { Plan = null; throw; }
        finally { Executing = false; }
    }

    private static void Launch(Func<Task> action, ExecutionRange? range = null)
    {
        if (Busy) return;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        ActiveRange = range;
        Error = null;
        Clock.Restart();
        _operation = Run(action, _generation);
    }

    private static async Task Run(Func<Task> action, int generation)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (generation == _generation) Status = "Paused"; }
        catch (Exception error)
        {
            if (generation == _generation) { Status = "Error"; Error = error.Message; }
            GD.PrintErr("[cvpp] " + error);
        }
        finally { Clock.Stop(); _idleSince = Time.GetTicksMsec(); }
    }

    internal static void Stop() => _cancel?.Cancel();

    internal static void Reset()
    {
        if (Resetting) return;
        Stop();
        _generation++;
        Resetting = true;
        _operation = Clear(_operation);
    }

    private static async Task Clear(Task? pending)
    {
        try
        {
            ReleaseWorker();
            if (pending != null) await pending;
            await _release;
        }
        finally
        {
            Plan = Preview = null;
            Progress = null;
            Rate.Reset();
            StopReason = null;
            Error = null;
            Step = 0;
            Status = "Ready";
            ActiveRange = null;
            Clock.Reset();
            Resetting = false;
        }
    }

    internal static bool Input(InputEvent input)
    {
        SolverHud.Pointer(input);
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.F10) { SolverHud.Toggle(); return true; }
            if (key.Keycode == Key.Escape && Busy) { Stop(); return true; }
            if (key.Keycode == Key.Escape && SolverHud.Close()) return true;
        }
        return Executing && input is InputEventKey or InputEventJoypadButton or InputEventJoypadMotion;
    }

    private static void Shutdown()
    {
        Stop();
        _worker?.Abort();
        _retiring?.Abort();
    }

    private static void ReleaseWorker()
    {
        if (_worker == null) return;
        var worker = _worker;
        _worker = null;
        _retiring = worker;
        _release = Task.Run(async () =>
        {
            try { await worker.DisposeAsync(); }
            catch (Exception error) { GD.PrintErr("[cvpp] Worker cleanup: " + error); }
            finally { _retiring = null; }
        });
    }
}

[HarmonyPatch]
internal static class SolverInputPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
    [
        AccessTools.Method(typeof(NGame), "_Input"),
        AccessTools.Method(typeof(NHotkeyManager), "_UnhandledInput")
    ];

    private static bool Prefix([HarmonyArgument(0)] InputEvent inputEvent)
    {
        if (SolverHud.Editing && inputEvent is InputEventKey { Keycode: not Key.Escape and not Key.F10 }) return false;
        if (!SolverController.Input(inputEvent)) return true;
        ((SceneTree)Engine.GetMainLoop()).Root.SetInputAsHandled();
        return false;
    }
}

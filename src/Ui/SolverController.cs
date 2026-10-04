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
    private static CancellationTokenSource? _cancel;
    private static Task? _operation;
    private static readonly Stopwatch Clock = new();
    private static bool _installed;
    private static bool _initialized;
    private static RunState? _run;
    private static object? _room;
    internal static bool Busy => _operation is { IsCompleted: false };
    internal static bool Executing { get; private set; }
    internal static string Status { get; private set; } = "Ready";
    internal static string? Error { get; private set; }
    internal static SolveProgress? Progress { get; private set; }
    internal static CombatPlan? Plan { get; private set; }
    internal static int Step { get; private set; }
    internal static int Seconds { get; set; } = 15;
    internal static double Elapsed => Clock.Elapsed.TotalSeconds;
    internal static bool Ready => RunManager.Instance.DebugOnlyGetState() is { } run && run.Players.Count == 1
        && RunManager.Instance.NetService.Type == NetGameType.Singleplayer && NativeCombat.IsStable(run);

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
                _run = run;
                _room = run?.CurrentRoom;
                Plan = null;
                Step = 0;
                Progress = null;
                Status = "Ready";
                Error = null;
            }
            SolverHud.Tick();
        }
        catch (Exception error)
        {
            Error = error.Message;
            GD.PrintErr("[cvpp] UI: " + error);
            ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= Tick;
            SolverHud.Disable();
        }
    }

    internal static void Solve(bool takeOver = false) => Launch(async () =>
    {
        if (!Ready) throw new InvalidOperationException("Wait for your turn and finish any open card selection.");
        var previous = Plan?.Steps.Skip(Step).ToArray();
        Progress = null;
        Status = "Starting solver";
        var position = await CombatPosition.Capture();
        uint[]? incumbent = previous is { Length: > 0 } && previous[0].Before == position.State
            ? previous.Select(step => step.Action).ToArray() : null;
        if (incumbent == null) { Plan = null; Step = 0; }
        _worker ??= new WorkerClient(OS.GetExecutablePath(), Path.GetDirectoryName(typeof(Entry).Assembly.Location)!,
            ProjectSettings.GlobalizePath("user://cvpp-workers"), WorkerEnvironment.Capture());
        var result = await _worker.Solve(new SolveRequest(position, new SolveOptions(Seconds), incumbent), progress =>
        {
            Progress = progress;
            Status = "Searching";
        }, _cancel!.Token);
        if (!Ready || CombatFingerprint.Capture(RunManager.Instance.DebugOnlyGetState()!) != position.State)
        {
            Plan = null;
            Step = 0;
            Status = "Combat changed · solve again";
            return;
        }
        Plan = result.Plan;
        Step = 0;
        Status = Plan == null ? "No winning route · try more time" : "Winning route";
        if (takeOver && Plan != null && !_cancel.IsCancellationRequested) await Execute(ExecutionRange.Combat);
    });

    internal static void Play(ExecutionRange range)
    {
        if (Plan == null)
        {
            if (range == ExecutionRange.Combat) Solve(takeOver: true);
            return;
        }
        Launch(() => Execute(range));
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
            Status = Step == plan.Steps.Length ? $"Victory · {run.Players.Single().Creature.CurrentHp} HP" : "Paused";
        }
        catch { Plan = null; throw; }
        finally { Executing = false; }
    }

    private static void Launch(Func<Task> action)
    {
        if (Busy) return;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        Error = null;
        Clock.Restart();
        _operation = Run(action);
    }

    private static async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status = "Stopped"; }
        catch (Exception error)
        {
            Status = "Unable to continue";
            Error = error.Message;
            GD.PrintErr("[cvpp] " + error);
        }
        finally { Clock.Stop(); }
    }

    internal static void Stop() => _cancel?.Cancel();

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
        if (_worker != null) Task.Run(async () => await _worker.DisposeAsync()).GetAwaiter().GetResult();
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
        if (!SolverController.Input(inputEvent)) return true;
        ((SceneTree)Engine.GetMainLoop()).Root.SetInputAsHandled();
        return false;
    }
}

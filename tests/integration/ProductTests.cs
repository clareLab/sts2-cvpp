using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace cvpp;

internal static class ProductTests
{
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    internal static async Task<object> Run(string characterId = "IRONCLAD", string seed = "CVPP-SMOKE-001")
    {
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == characterId);
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], seed, GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await Until(() => NativeCombat.IsStable(run), "product combat");
        await Until(() => Tree.Root.FindChild("CvppToolbar", true, false) is Control { } toolbar && toolbar.IsVisibleInTree(), "automatic toolbar installation");
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui")) RenderingServer.RenderLoopEnabled = false;
        string? exit = OS.GetCmdlineArgs().FirstOrDefault(argument => argument.StartsWith("--cvpp-exit="));
        if (exit != null) await LifecycleTests.Exit(exit.Split('=')[1]);
        if (characterId == "IRONCLAD") await LifecycleTests.Faults(await CombatPosition.Capture());
        string before = CombatFingerprint.Capture(run);
        SolverController.Seconds = 5;
        var timing = Stopwatch.StartNew();
        SolverController.Solve();
        await Until(() => SolverController.Preview != null || !SolverController.Busy, "live preview", 100);
        if (!SolverController.Busy || SolverController.Preview is not { } preview
            || preview.Steps.Sum(action => action.HpDelta) != preview.FinalHp - run.Players[0].Creature.CurrentHp)
            throw new InvalidOperationException("A complete route with HP deltas was not published during search.");
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui"))
        {
            Click("CvppRouteToggle");
            await Screenshot("searching");
            if (Tree.Root.FindChild("CvppStep", true, false) is not Button { Disabled: true })
                throw new InvalidOperationException("Search preview was executable before the search stopped.");
            SolverHud.Close();
        }
        await Until(() => !SolverController.Busy, "worker solve", 100);
        if (SolverController.StopReason != "time_limit" || SolverController.Status != "Time limit")
            throw new InvalidOperationException("Timed search did not report its stopping reason.");
        var plan = SolverController.Plan ?? throw new InvalidOperationException(SolverController.Error ?? SolverController.Status);
        double firstSolveMs = timing.Elapsed.TotalMilliseconds;
        int worker = SolverController.WorkerPid ?? throw new InvalidOperationException("Worker was not retained during combat.");
        if (CombatFingerprint.Capture(run) != before) throw new InvalidOperationException("Searching mutated the live game.");
        SolverHud.Tick();
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui"))
        {
            await Screenshot("toolbar");
            Click("CvppRouteToggle");
            await Screenshot("route");
            Click("CvppSettings");
            Click("CvppTime30");
            if (SolverController.Seconds != 30 || Tree.Root.FindChild("CvppRoute", true, false) is not Control { Visible: false })
                throw new InvalidOperationException("Settings flyout interaction failed.");
            await HudTests.Run();
            await Screenshot("settings");
            SolverHud.Close();
            RenderingServer.RenderLoopEnabled = false;
        }
        SolverController.Play(ExecutionRange.Step);
        await Until(() => !SolverController.Busy, "single step");
        if (SolverController.Error != null || SolverController.Step == 0 || SolverController.Plan == null)
            throw new InvalidOperationException(SolverController.Error ?? "Single step failed.");
        int step = SolverController.Step;
        SolverController.Play(ExecutionRange.Turn);
        await Until(() => !SolverController.Busy, "play turn");
        if (SolverController.Error != null || SolverController.Step <= step || SolverController.Plan == null)
            throw new InvalidOperationException(SolverController.Error ?? "Turn execution failed.");
        before = CombatFingerprint.Capture(run);
        int previousHp = plan.FinalHp;
        SolverController.Seconds = 0;
        timing.Restart();
        SolverController.Solve();
        await Until(() => SolverController.Progress?.BestHp != null || !SolverController.Busy, "warm worker", 30);
        SolverController.Stop();
        await Until(() => !SolverController.Busy, "cancel search", 15);
        plan = SolverController.Plan ?? throw new InvalidOperationException(SolverController.Error ?? "Cancellation discarded the winning route.");
        if (plan.FinalHp < previousHp) throw new InvalidOperationException("Searching again lost a better route.");
        double cancelledSolveMs = timing.Elapsed.TotalMilliseconds;
        if (SolverController.Error != null || CombatFingerprint.Capture(run) != before)
            throw new InvalidOperationException("Solving the current turn mutated the live game.");
        object? memory = characterId == "IRONCLAD" ? await LifecycleTests.Repeat(run, worker) : null;
        plan = SolverController.Plan!;
        if (characterId == "IRONCLAD")
        {
            SolverController.IdleMilliseconds = 1;
            try { await LifecycleTests.Released(worker); }
            finally { SolverController.IdleMilliseconds = 120_000; }
            if (SolverController.Plan != plan) throw new InvalidOperationException("Idle retirement discarded the route.");
        }
        SolverController.Play(ExecutionRange.Combat);
        await Until(() => !SolverController.Busy, "take over", 60);
        if (SolverController.Error != null || CombatManager.Instance.IsInProgress || run.Players.Single().Creature.CurrentHp != plan.FinalHp)
            throw new InvalidOperationException(SolverController.Error ?? "Takeover did not reproduce the winning route.");
        await LifecycleTests.Released(worker);
        RunManager.Instance.CleanUp();
        await Until(() => SolverController.Plan == null, "clear ended run");
        if (Tree.Root.FindChild("CvppSteps", true, false) is not Godot.Tree steps || steps.GetRoot() != null)
            throw new InvalidOperationException("Hidden route retained tree items.");
        GD.Print($"[cvpp] PRODUCT {plan.FinalHp} HP, {plan.Steps.Length} steps; step, turn, cancellation, current-position replay and takeover verified");
        return new { characterId, plan.FinalHp, steps = plan.Steps.Length, plan.Turns, firstSolveMs, cancelledSolveMs, memory };
    }

    internal static async Task Until(Func<bool> ready, string stage, int seconds = 30)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(stage);
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        } while (!ready());
    }

    private static void Click(string name) => (Tree.Root.FindChild(name, true, false) as Button
        ?? throw new InvalidOperationException("Missing control: " + name)).EmitSignal(BaseButton.SignalName.Pressed);

    private static async Task Screenshot(string name)
    {
        SolverHud.Tick();
        for (int frame = 0; frame < 3; frame++) await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        RenderingServer.RenderLoopEnabled = true;
        await Tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = Tree.Root.GetTexture().GetImage();
        image.SavePng(ProjectSettings.GlobalizePath("user://cvpp-ui-" + name + ".png"));
        RenderingServer.RenderLoopEnabled = false;
    }
}

using System.Diagnostics;
using System.Globalization;
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
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui"))
        {
            SolverHud.Tick();
            if (Tree.Root.FindChild("CvppProgress", true, false) is not ProgressBar { Indeterminate: true }
                || SolverController.Status != "Loading") throw new InvalidOperationException("Worker startup has no loading feedback.");
            await Task.Delay(300);
            await Screenshot("loading");
        }
        await Until(() => SolverController.Preview != null || !SolverController.Busy, "live preview", 100);
        if (!SolverController.Busy || SolverController.Preview is not { } preview
            || preview.Steps.Sum(action => action.HpDelta) != preview.FinalHp - run.Players[0].Creature.CurrentHp)
            throw new InvalidOperationException("A complete route with HP deltas was not published during search.");
        SolverHud.Tick();
        var toolbar = (Control)Tree.Root.FindChild("CvppToolbar", true, false);
        if (SolverController.Progress is not { Simulations: > 0 } progress || SolverController.SimulationsPerSecond <= 0
            || Tree.Root.FindChild("CvppExplored", true, false) is not Label explored
            || explored.Text != progress.Simulations.ToString("N0", CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Search throughput is missing from the toolbar.");
        Vector2 searchingSize = toolbar.Size;
        if (Tree.Root.FindChild("CvppSolve", true, false) is not Button { Disabled: false, ButtonPressed: true } solveButton
            || !solveButton.GetNode<TextureRect>("Pause").Visible || solveButton.GetNode<TextureRect>("Icon").Visible)
            throw new InvalidOperationException("Solve did not become the pause control during search.");
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
        await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        if (toolbar.Size != searchingSize) throw new InvalidOperationException("Toolbar size changed when search stopped.");
        if (SolverController.SimulationsPerSecond != 0 || SolverController.Progress?.Simulations < progress.Simulations
            || Tree.Root.FindChild("CvppSpeed", true, false) is not Label { Text: "0/s" })
            throw new InvalidOperationException("Stopped search lost its count or retained an active speed.");
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
        Click("CvppStep");
        await Until(() => !SolverController.Busy, "single step");
        if (SolverController.Error != null || SolverController.Step == 0 || SolverController.Plan == null)
            throw new InvalidOperationException(SolverController.Error ?? "Single step failed.");
        int step = SolverController.Step;
        Click("CvppTurn");
        await Until(() => !SolverController.Busy, "play turn");
        if (SolverController.Error != null || SolverController.Step <= step || SolverController.Plan == null)
            throw new InvalidOperationException(SolverController.Error ?? "Turn execution failed.");
        before = CombatFingerprint.Capture(run);
        int previousHp = plan.FinalHp;
        SolverController.Seconds = 0;
        timing.Restart();
        Click("CvppSolve");
        await Until(() => SolverController.Progress?.BestHp != null || !SolverController.Busy, "warm worker", 30);
        SolverHud.Tick();
        Click("CvppSolve");
        await Until(() => !SolverController.Busy, "cancel search", 15);
        plan = SolverController.Plan ?? throw new InvalidOperationException(SolverController.Error ?? "Cancellation discarded the winning route.");
        if (plan.FinalHp < previousHp) throw new InvalidOperationException("Searching again lost a better route.");
        double cancelledSolveMs = timing.Elapsed.TotalMilliseconds;
        if (SolverController.Error != null || CombatFingerprint.Capture(run) != before)
            throw new InvalidOperationException("Solving the current turn mutated the live game.");
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui")) await Screenshot("paused");
        if (characterId == "IRONCLAD")
        {
            Click("CvppSolve");
            await Until(() => SolverController.Preview != null || !SolverController.Busy, "reset active search", 30);
            if (!SolverController.Busy) throw new InvalidOperationException("Reset probe ended before reset.");
            await Reset(run);
            await LifecycleTests.Released(worker);
            Click("CvppSolve");
            await Until(() => SolverController.Preview != null || !SolverController.Busy, "solve after reset", 100);
            if (SolverController.Busy) Click("CvppSolve");
            await Until(() => !SolverController.Busy, "pause after reset", 15);
            if (SolverController.Plan == null || SolverController.Error != null)
                throw new InvalidOperationException(SolverController.Error ?? "Solve after reset failed.");
            worker = SolverController.WorkerPid ?? throw new InvalidOperationException("Reset did not allow a new worker.");
        }
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
        SolverHud.Tick();
        var auto = (Button)Tree.Root.FindChild("CvppAuto", true, false);
        if (auto.Disabled || !auto.ButtonPressed || !auto.GetNode<TextureRect>("Pause").Visible)
            throw new InvalidOperationException("Takeover did not become the pause control during execution.");
        Click("CvppAuto");
        await Until(() => !SolverController.Busy, "pause takeover", 30);
        if (SolverController.Error != null || SolverController.Plan != plan)
            throw new InvalidOperationException(SolverController.Error ?? "Pausing takeover discarded the route.");
        if (CombatManager.Instance.IsInProgress) Click("CvppAuto");
        await Until(() => !SolverController.Busy, "take over", 60);
        if (SolverController.Error != null || CombatManager.Instance.IsInProgress || run.Players.Single().Creature.CurrentHp != plan.FinalHp)
            throw new InvalidOperationException(SolverController.Error ?? "Takeover did not reproduce the winning route.");
        await LifecycleTests.Released(worker);
        if (characterId == "IRONCLAD") await Reset(run);
        RunManager.Instance.CleanUp();
        await Until(() => SolverController.Plan == null, "clear ended run");
        if (Tree.Root.FindChild("CvppSteps", true, false) is not Godot.Tree steps || steps.GetRoot() != null)
            throw new InvalidOperationException("Hidden route retained tree items.");
        GD.Print($"[cvpp] PRODUCT {plan.FinalHp} HP, {plan.Steps.Length} steps; step, turn, cancellation, current-position replay and takeover verified");
        return new { characterId, plan.FinalHp, steps = plan.Steps.Length, plan.Turns, firstSolveMs, cancelledSolveMs, memory };
    }

    private static async Task Reset(RunState run)
    {
        string before = CombatFingerprint.Capture(run);
        int seconds = SolverController.Seconds;
        int memory = SolverController.MemoryMiB;
        Click("CvppReset");
        await Until(() => !SolverController.Busy, "reset completed route", 15);
        if (SolverController.Plan != null || SolverController.Preview != null || SolverController.Progress != null
            || SolverController.Step != 0 || SolverController.Status != "Ready" || SolverController.Elapsed != 0
            || SolverController.SimulationsPerSecond != 0
            || SolverController.Seconds != seconds || SolverController.MemoryMiB != memory
            || !SolverController.WorkerReleased || CombatFingerprint.Capture(run) != before)
            throw new InvalidOperationException("Reset did not clear results while preserving the live state and settings.");
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
        if (name is "toolbar" or "searching" or "loading")
        {
            var bounds = ((Control)Tree.Root.FindChild("CvppToolbar", true, false)).GetGlobalRect();
            var scale = (Vector2)image.GetSize() / Tree.Root.GetVisibleRect().Size;
            using var detail = image.GetRegion(new Rect2I((Vector2I)(bounds.Position * scale).Floor(), (Vector2I)(bounds.Size * scale).Ceil()));
            detail.SavePng(ProjectSettings.GlobalizePath("user://cvpp-ui-" + name + "-detail.png"));
        }
        RenderingServer.RenderLoopEnabled = false;
    }
}

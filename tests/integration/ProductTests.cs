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
        SolverController.Initialize();
        SolverHud.Install();
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == characterId);
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], seed, GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await Until(() => NativeCombat.IsStable(run), "product combat");
        string before = CombatFingerprint.Capture(run);
        SolverController.Seconds = 5;
        var timing = Stopwatch.StartNew();
        SolverController.Solve();
        await Until(() => !SolverController.Busy, "worker solve", 100);
        var plan = SolverController.Plan ?? throw new InvalidOperationException(SolverController.Error ?? SolverController.Status);
        double firstSolveMs = timing.Elapsed.TotalMilliseconds;
        if (CombatFingerprint.Capture(run) != before) throw new InvalidOperationException("Searching mutated the live game.");
        SolverHud.Tick();
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui"))
        {
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            await Tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = Tree.Root.GetTexture().GetImage();
            image.SavePng(ProjectSettings.GlobalizePath("user://cvpp-ui.png"));
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
        SolverController.Seconds = 60;
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
        SolverController.Play(ExecutionRange.Combat);
        await Until(() => !SolverController.Busy, "take over", 60);
        if (SolverController.Error != null || CombatManager.Instance.IsInProgress || run.Players.Single().Creature.CurrentHp != plan.FinalHp)
            throw new InvalidOperationException(SolverController.Error ?? "Takeover did not reproduce the winning route.");
        GD.Print($"[cvpp] PRODUCT {plan.FinalHp} HP, {plan.Steps.Length} steps; step, turn, cancellation, current-position replay and takeover verified");
        return new { characterId, plan.FinalHp, steps = plan.Steps.Length, plan.Turns, firstSolveMs, cancelledSolveMs };
    }

    private static async Task Until(Func<bool> ready, string stage, int seconds = 30)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(stage);
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        } while (!ready());
    }
}

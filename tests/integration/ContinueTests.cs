using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace cvpp;

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IncrementNumReloads))]
internal static class ContinueTests
{
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    private static readonly TaskCompletionSource Gate = new();

    private static void Postfix(ref Task __result) => __result = Delay(__result);

    private static async Task Delay(Task saved)
    {
        await saved;
        await Gate.Task;
    }

    internal static async Task Run(string path)
    {
        var manager = RunManager.Instance;
        if (manager.IsInProgress || manager.NetService != null) throw new InvalidOperationException("Continue regression requires a cold process.");
        var save = JsonSerializer.Deserialize(File.ReadAllText(path), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
        var run = RunState.FromSerializable(save);
        var patch = new Harmony("clarelab.cvpp.continue-test");
        patch.CreateClassProcessor(typeof(ContinueTests)).Patch();
        var setup = manager.SetUpSavedSingleplayer(run, save);
        try
        {
            for (int frame = 0; frame < 4; frame++) await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            if (setup.IsCompleted || manager.NetService != null || SolverController.Ready || NativeCombat.IsStable(run) || SolverController.Error != null)
                throw new InvalidOperationException("Loading a save did not preserve the UI's unready state.");
        }
        finally
        {
            Gate.TrySetResult();
            patch.UnpatchAll(patch.Id);
        }
        await setup;
        await NGame.Instance!.LoadRun(run, save.PreFinishedRoom);
        await ProductTests.Until(() => SolverController.Ready && !NGame.Instance.Transition.InTransition, "continued combat decision");
        await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        if (SolverController.Error != null || !SolverController.Ready
            || Tree.Root.FindChild("Cvpp", true, false) is not CanvasLayer { Visible: true }
            || Tree.Root.FindChild("CvppToolbar", true, false) is not Control toolbar || !toolbar.IsVisibleInTree()
            || Tree.Root.FindChild("CvppSolve", true, false) is not Button { Disabled: false })
            throw new InvalidOperationException(SolverController.Error ?? "Toolbar did not appear after continuing the saved combat.");
        if (OS.GetCmdlineArgs().Contains("--cvpp-ui"))
        {
            await Tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = Tree.Root.GetTexture().GetImage();
            image.SavePng(ProjectSettings.GlobalizePath("user://cvpp-ui-continue.png"));
        }
        manager.CleanUp();
    }
}

using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

internal static class SelfTests
{
    private static readonly List<string> Passed = [];
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    internal static void Initialize()
    {
        if (!OS.GetCmdlineArgs().Contains("--cvpp-selftest")) return;
        if ((DisplayServer.GetName() != "headless" && !OS.GetCmdlineArgs().Contains("--cvpp-ui"))
            || !File.Exists(ProjectSettings.GlobalizePath("user://.cvpp-test-sandbox")))
            throw new InvalidOperationException("An isolated headless test sandbox is required.");
        new Harmony("clarelab.cvpp.selftest").CreateClassProcessor(typeof(HeadlessAssets)).Patch();
        new Harmony("clarelab.cvpp.selftest.presentation").CreateClassProcessor(typeof(HeadlessPresentation)).Patch();
        SolverController.Initialize();
        _ = Run();
    }

    private static async Task Run()
    {
        string? failure = null;
        object? benchmark = null;
        object? regressions = null;
        object? product = null;
        try
        {
            await Until(() => NGame.Instance != null && SaveManager.Instance.IsProfileInitialized, "startup");
            await NGame.Instance!.GameStartupComplete;
            await Until(() => !NGame.Instance.Transition.InTransition, "main menu");
            if (DisplayServer.GetName() == "headless")
                await NAssetLoader.Instance.LoadInTheBackground(PreloadManager.Cache.CreateSession("cvpp-startup", []))
                    .WaitAsync(TimeSpan.FromSeconds(30));
            Check(!TestMode.IsOn, "official game rules");
            Check(NativeCore.Initialize() == NativeCore.ExpectedAbi, "Rust loaded inside Godot");
            SaveManager.Instance.SetFtuesEnabled(false);
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            string continueSave = ProjectSettings.GlobalizePath("user://cvpp-fixture.save");
            if (File.Exists(continueSave))
            {
                await ContinueTests.Run(continueSave);
                Check(true, "cold continue keeps controls disabled until ready and displays toolbar in combat");
            }
            string fixture = ProjectSettings.GlobalizePath("user://cvpp-fixture.mcr");
            if (File.Exists(fixture))
            {
                var reader = new PacketReader();
                reader.Reset(File.ReadAllBytes(fixture));
                var replay = reader.Read<CombatReplay>();
                string save = ProjectSettings.GlobalizePath("user://cvpp-fixture.save");
                if (File.Exists(save)) replay.serializableRun = JsonSerializer.Deserialize(File.ReadAllText(save), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
                replay.events.Clear();
                replay.checksumData.Clear();
                var checkpoint = new CombatCheckpoint(replay);
                await using var combat = new NativeCombat { Mode = CombatExecution.Worker };
                var result = await HealthSearch.Run(combat, () => combat.Restore(checkpoint), new SolveOptions(5));
                Check(result.Plan != null, "external replay decodes and produces a verified winning route");
            }
            if (!OS.GetCmdlineArgs().Contains("--cvpp-ui") && !OS.GetCmdlineArgs().Contains("--cvpp-product-only"))
            {
                if (OS.GetCmdlineArgs().Contains("--cvpp-benchmark"))
                {
                    benchmark = await NativeBenchmark.Run();
                    Check(true, "native branch replay benchmark");
                }
                else await Smoke();
                regressions = await NativeRegressions.Run();
                Check(true, "native choices, potions, generated cards, cross-turn replay and bounded search");
            }
            product = OS.GetCmdlineArgs().Contains("--cvpp-ui") ? await ProductTests.Run()
                : new[] { await ProductTests.Run(), await ProductTests.Run("SILENT", "CVPP-REGRESSION-001") };
            Check(true, "isolated worker, live state preservation, step, turn and takeover");
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        var report = new { success = failure == null, passed = Passed, benchmark, regressions, product, error = failure };
        string json = JsonSerializer.Serialize(report);
        File.WriteAllText(ProjectSettings.GlobalizePath("user://cvpp-selftest.json"), json);
        GD.Print("[cvpp] SELFTEST " + json);
        Tree.Quit(failure == null ? 0 : 1);
    }

    private static async Task Smoke()
    {
        await using var combat = new NativeCombat();
        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD");
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], "CVPP-SMOKE-001", GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        var player = run.Players.Single();
        await combat.Until(() => combat.Stable(run), "combat start");
        Check(CombatManager.Instance.IsInProgress, "official combat started");
        string initial = combat.Fingerprint(run);
        Check(combat.Fingerprint(run) == initial, "state capture preserves native RNG and state");
        var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Type == CardType.Attack && c.CanPlay());
        var enemy = player.Creature.CombatState!.HittableEnemies.First(card.IsValidTarget);
        int enemyHp = enemy.CurrentHp;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, enemy));
        await combat.Until(() => combat.Stable(run) && enemy.CurrentHp < enemyHp, "native card action");
        Check(combat.Fingerprint(run) != initial, "native action changes combat state");
        int turn = player.PlayerCombatState.TurnNumber;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await combat.Until(() => combat.Stable(run) && player.PlayerCombatState.TurnNumber > turn, "native next turn");
        Check(CombatManager.Instance.IsInProgress, "native enemy and next-turn phases completed");
    }

    private static async Task Until(Func<bool> ready, string stage)
    {
        var timer = Stopwatch.StartNew();
        int stableFrames = 0;
        while (stableFrames < 3)
        {
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException(stage);
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            stableFrames = ready() ? stableFrames + 1 : 0;
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Passed.Add(label);
    }
}

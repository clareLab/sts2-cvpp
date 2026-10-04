using System.Diagnostics;
using System.Security.Cryptography;
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
        if (DisplayServer.GetName() != "headless"
            || !File.Exists(ProjectSettings.GlobalizePath("user://.cvpp-test-sandbox")))
            throw new InvalidOperationException("An isolated headless test sandbox is required.");
        new Harmony("clarelab.cvpp.selftest").CreateClassProcessor(typeof(HeadlessAssets)).Patch();
        _ = Run();
    }

    private static async Task Run()
    {
        string? failure = null;
        object? benchmark = null;
        try
        {
            await Until(() => NGame.Instance != null && SaveManager.Instance.IsProfileInitialized, "startup");
            await NGame.Instance!.GameStartupComplete;
            await Until(() => !NGame.Instance.Transition.InTransition, "main menu");
            await NAssetLoader.Instance.LoadInTheBackground(PreloadManager.Cache.CreateSession("cvpp-startup", []))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Check(!TestMode.IsOn, "official game rules");
            Check(NativeCore.Initialize() == NativeCore.ExpectedAbi, "Rust loaded inside Godot");
            SaveManager.Instance.SetFtuesEnabled(false);
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            if (OS.GetCmdlineArgs().Contains("--cvpp-benchmark"))
            {
                benchmark = await NativeBenchmark.Run();
                Check(true, "native branch replay benchmark");
            }
            else await Smoke();
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        var report = new { success = failure == null, passed = Passed, benchmark, error = failure };
        string json = JsonSerializer.Serialize(report);
        File.WriteAllText(ProjectSettings.GlobalizePath("user://cvpp-selftest.json"), json);
        GD.Print("[cvpp] SELFTEST " + json);
        Tree.Quit(failure == null ? 0 : 1);
    }

    private static async Task Smoke()
    {
        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD");
        SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).TotalLosses = 2;
        var run = await NGame.Instance!.StartNewSingleplayerRun(character, true,
            ActModel.GetDefaultList(), [], "CVPP-SMOKE-001", GameMode.Standard);
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        var player = run.Players.Single();
        bool Stable() => !RunManager.Instance.ActionExecutor.IsRunning
            && RunManager.Instance.ActionQueueSet.IsEmpty
            && !CombatManager.Instance.IsStarting
            && !CombatManager.Instance.PlayerActionsDisabled
            && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play;
        await Until(Stable, "combat start");
        Check(CombatManager.Instance.IsInProgress, "official combat started");
        string initial = Fingerprint(run);
        Check(Fingerprint(run) == initial, "state capture preserves native RNG and state");
        var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Type == CardType.Attack && c.CanPlay());
        var enemy = player.Creature.CombatState!.HittableEnemies.First(card.IsValidTarget);
        int enemyHp = enemy.CurrentHp;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, enemy));
        await Until(() => Stable() && enemy.CurrentHp < enemyHp, "native card action");
        Check(Fingerprint(run) != initial, "native action changes combat state");
        int turn = player.PlayerCombatState.TurnNumber;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await Until(() => Stable() && player.PlayerCombatState.TurnNumber > turn, "native next turn");
        Check(CombatManager.Instance.IsInProgress, "native enemy and next-turn phases completed");
    }

    private static string Fingerprint(RunState run)
    {
        var writer = new PacketWriter();
        writer.Write(NetFullCombatState.FromRun(run, null));
        foreach (var player in run.Players) writer.Write(player.PlayerRng.ToSerializable());
        writer.ZeroByteRemainder();
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
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

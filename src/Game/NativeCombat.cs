using System.Diagnostics;
using System.Security.Cryptography;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

internal enum CombatExecution { Reference, NonInteractive, Worker }

internal sealed class NativeCombat : IAsyncDisposable
{
    internal const uint EndTurn = uint.MaxValue;
    internal const uint Potion = 0x40000000;
    internal const uint Selection = 0x80000000;
    private static NativeCombat? _owner;
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    private static RunManager Manager => RunManager.Instance;
    private readonly int _threadId = System.Environment.CurrentManagedThreadId;
    private readonly bool _originalTestMode = TestMode.IsOn;
    private readonly Func<bool> _originalNonInteractive = NonInteractiveMode.AutoSlayerCheck;
    private bool _disposed;
    private readonly CombatChoices _choices = new();
    private IDisposable? _selector;
    private Exception? _failure;

    internal CombatExecution Mode { get; set; } = CombatExecution.Reference;
    internal bool PumpContinuations { get; set; } = true;
    internal bool HasChoice => _choices.Pending != null;
    internal bool Victory { get; private set; }
    private bool AtChoice => HasChoice && !Manager.ActionExecutor.IsRunning;

    internal NativeCombat()
    {
        if (DisplayServer.GetName() != "headless"
            || !File.Exists(ProjectSettings.GlobalizePath("user://.cvpp-test-sandbox"))
            || SynchronizationContext.Current != Dispatcher.SynchronizationContext)
            throw new InvalidOperationException("Native combat requires an isolated Godot headless host.");
        if (_owner != null) throw new InvalidOperationException("A native combat session already owns this process.");
        _owner = this;
    }

    private void EnsureOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owner != this || System.Environment.CurrentManagedThreadId != _threadId
            || SynchronizationContext.Current != Dispatcher.SynchronizationContext)
            throw new InvalidOperationException("Native combat requires its owning Godot thread and context.");
    }

    private void Pump()
    {
        EnsureOwner();
        if (Mode == CombatExecution.Worker && PumpContinuations)
            Dispatcher.SynchronizationContext.ExecutePendingContinuations();
    }

    private void Check(RunState run)
    {
        EnsureOwner();
        if (!ReferenceEquals(run, Manager.DebugOnlyGetState()))
            throw new InvalidOperationException("The run no longer belongs to this native combat session.");
    }

    private void AfterAction(GameAction action) => _failure ??= action.Exception;
    private void Won(CombatRoom room) => Victory = true;

    private async ValueTask CleanUp()
    {
        try
        {
            while (HasChoice && _failure == null)
            {
                _choices.FinishAbandoned();
                await Until(() => AtChoice || Finished || Stable(Manager.DebugOnlyGetState()!), "finishing abandoned selection");
            }
            if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
        }
        finally
        {
            _selector?.Dispose();
            _selector = null;
            CombatManager.Instance.CombatWon -= Won;
            if (Manager.IsInProgress) Manager.ActionExecutor.AfterActionExecuted -= AfterAction;
            Manager.CleanUp();
            _failure = null;
            Victory = false;
        }
    }

    internal async ValueTask Reset()
    {
        EnsureOwner();
        await CleanUp();
        TestMode.IsOn = false;
        NonInteractiveMode.AutoSlayerCheck = static () => false;
        Mode = CombatExecution.Reference;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        EnsureOwner();
        try { await Reset(); }
        finally
        {
            TestMode.IsOn = _originalTestMode;
            NonInteractiveMode.AutoSlayerCheck = _originalNonInteractive;
            _owner = null;
            _disposed = true;
        }
    }

    internal uint[] Actions(RunState run)
    {
        Check(run);
        if (_choices.Pending is { } pending)
            return Enumerable.Range(0, pending.Count).Select(index => Selection | (uint)index).ToArray();
        if (Finished) return [];
        if (!Stable(run)) throw new InvalidOperationException("Combat has not reached a decision boundary.");
        var player = run.Players.Single();
        var hand = player.PlayerCombatState!.Hand.Cards;
        var creatures = player.Creature.CombatState!.Creatures;
        if (hand.Count >= 0x4000 || player.PotionSlots.Count >= 0x4000 || creatures.Count >= 0xffff)
            throw new NotSupportedException("Combat indices exceed the action encoding limits.");
        var actions = new List<uint> { uint.MaxValue };
        for (int index = 0; index < hand.Count; index++)
        {
            var card = hand[index];
            if (!card.CanPlay()) continue;
            uint token = checked((uint)(index + 1) << 16);
            if (card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer)
            {
                for (int target = 0; target < creatures.Count; target++)
                    if (card.IsValidTarget(creatures[target])) actions.Add(token | checked((uint)(target + 1)));
            }
            else if (card.IsValidTarget(null)) actions.Add(token);
        }
        for (int index = 0; index < player.PotionSlots.Count; index++)
        {
            var potion = player.GetPotionAtSlotIndex(index);
            if (potion == null || !CanUse(potion)) continue;
            uint token = Potion | ((uint)(index + 1) << 16);
            if (potion.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer or TargetType.Self)
            {
                for (int target = 0; target < creatures.Count; target++)
                    if (potion.IsValidTarget(creatures[target])) actions.Add(token | (uint)(target + 1));
            }
            else if (potion.IsValidTarget(null)) actions.Add(token);
        }
        if (actions.Count > 4096) throw new NotSupportedException("Combat branching exceeds the native action limit.");
        return actions.ToArray();
    }

    private static bool CanUse(PotionModel potion) => !potion.IsQueued && potion.Owner.CanUseOrRemovePotions
        && potion.Usage is PotionUsage.AnyTime or PotionUsage.CombatOnly && potion.PassesCustomUsabilityCheck;

    private static Creature? Target(RunState run, uint token)
    {
        int index = (int)(token & 0xffff) - 1;
        var creatures = run.Players.Single().Creature.CombatState!.Creatures;
        if (index >= creatures.Count) throw new InvalidOperationException("Unavailable action target.");
        return index < 0 ? null : creatures[index];
    }

    private static (PotionModel Potion, Creature? Target) ResolvePotion(RunState run, uint token)
    {
        int index = (int)((token & 0x3fff0000) >> 16) - 1;
        var player = run.Players.Single();
        if (index < 0 || index >= player.PotionSlots.Count)
            throw new InvalidOperationException("Unavailable potion slot.");
        var potion = player.GetPotionAtSlotIndex(index);
        var target = Target(run, token);
        if (potion == null || !CanUse(potion) || !potion.IsValidTarget(target))
            throw new InvalidOperationException("Illegal potion action.");
        return (potion, target);
    }

    private (CardModel Card, Creature? Target) Resolve(RunState run, uint token)
    {
        Check(run);
        var player = run.Players.Single();
        var hand = player.PlayerCombatState!.Hand.Cards;
        int index = checked((int)(token >> 16)) - 1;
        int targetIndex = checked((int)(token & 0xffff)) - 1;
        var creatures = player.Creature.CombatState!.Creatures;
        if (index < 0 || index >= hand.Count || targetIndex >= creatures.Count)
            throw new InvalidOperationException("Search route references an unavailable card or target.");
        var card = hand[index];
        var target = targetIndex < 0 ? null : creatures[targetIndex];
        if (!card.CanPlay() || !card.IsValidTarget(target))
            throw new InvalidOperationException("Search route contains an illegal action.");
        return (card, target);
    }

    internal string Describe(RunState run, uint token)
    {
        Check(run);
        if (token == uint.MaxValue) return "END_TURN";
        if ((token & 0xc0000000) == Selection)
            return "SELECT:" + string.Join(",", (_choices.Pending?[token & 0x3fffffff]
                ?? throw new InvalidOperationException("No selection is pending.")).Select(card => card.Id.Entry));
        if ((token & 0xc0000000) == Potion)
        {
            var (potion, creature) = ResolvePotion(run, token);
            return creature == null ? potion.Id.Entry : $"{potion.Id.Entry}:{creature.ModelId.Entry}";
        }
        var (card, target) = Resolve(run, token);
        return target == null ? card.Id.Entry : $"{card.Id.Entry}:{target.ModelId.Entry}";
    }

    internal async ValueTask Execute(RunState run, uint token)
    {
        Check(run);
        var player = run.Players.Single();
        if (HasChoice)
        {
            if ((token & 0xc0000000) != Selection) throw new InvalidOperationException("A selection must be resolved first.");
            _choices.Complete(token & 0x3fffffff);
            await Until(() => AtChoice || Finished || Stable(run), "card selection");
            return;
        }
        if (!Stable(run)) throw new InvalidOperationException("Combat is not ready for an action.");
        if (token == uint.MaxValue)
        {
            int turn = player.PlayerCombatState!.TurnNumber;
            Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await Until(() => AtChoice || Finished || (Stable(run) && player.PlayerCombatState!.TurnNumber > turn), "search end turn");
        }
        else if ((token & 0xc0000000) == 0)
        {
            var (card, target) = Resolve(run, token);
            Manager.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
            await Until(() => AtChoice || Finished || Stable(run), "search card");
        }
        else if ((token & 0xc0000000) == Potion)
        {
            var (potion, target) = ResolvePotion(run, token);
            potion.EnqueueManualUse(target);
            await Until(() => AtChoice || Finished || Stable(run), "search potion");
        }
        else throw new InvalidOperationException("Unknown action token.");
    }

    internal async ValueTask<RunState> Restore(CombatCheckpoint checkpoint, Dictionary<string, double>? timings = null)
    {
        EnsureOwner();
        long started = Stopwatch.GetTimestamp();
        void Mark(string stage)
        {
            if (timings == null) return;
            long now = Stopwatch.GetTimestamp();
            timings[stage] = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
            started = now;
        }
        await CleanUp();
        NonInteractiveMode.AutoSlayerCheck = Mode == CombatExecution.Reference ? static () => false : static () => true;
        if (Mode == CombatExecution.Worker && NRun.Instance != null)
        {
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
        if (Mode == CombatExecution.Worker)
            NGame.Instance!.SetScreenShakeTarget(NGame.Instance.RootSceneContainer.CurrentScene!);
        TestMode.IsOn = Mode == CombatExecution.Worker;
        Mark("cleanup_ms");
        var replay = checkpoint.Read();
        var save = replay.serializableRun;
        var run = RunState.FromSerializable(save);
        Mark("deserialize_ms");
        await Manager.SetUpSavedSingleplayer(run, save);
        _selector = CardSelectCmd.PushSelector(_choices, localOnly: true);
        Manager.ActionExecutor.AfterActionExecuted += AfterAction;
        CombatManager.Instance.CombatWon += Won;
        if (Mode == CombatExecution.Worker)
        {
            Manager.CombatReplayWriter.IsEnabled = false;
            Manager.ChecksumTracker.IsEnabled = false;
        }
        Mark("setup_and_save_ms");
        Manager.Launch();
        if (Mode != CombatExecution.Worker) NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        Mark("scene_ms");
        await Manager.GenerateMap();
        Manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
        Manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
        Manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
        Manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
        Mark("map_ms");
        await Manager.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(save.PreFinishedRoom, run));
        await Until(() => AtChoice || Finished || Stable(run), "restored combat");
        Mark("enter_combat_ms");
        return run;
    }

    internal async ValueTask<CombatCheckpoint> CaptureRoomStart()
    {
        EnsureOwner();
        var run = Manager.DebugOnlyGetState();
        if (Mode != CombatExecution.Reference || run == null || !Stable(run))
            throw new InvalidOperationException("Room-start capture requires a stable reference combat.");
        var manager = Manager;
        if (SaveManager.Instance.CurrentRunSaveTask is { } task) await task;
        string path = ProjectSettings.GlobalizePath("user://cvpp-root.mcr");
        manager.CombatReplayWriter.WriteReplay(path, false);
        var reader = new PacketReader();
        reader.Reset(File.ReadAllBytes(path));
        var replay = reader.Read<CombatReplay>();
        if (replay.events.Any(e => e.eventType == CombatReplayEventType.GameAction
            || e.eventType == CombatReplayEventType.PlayerChoice))
            throw new NotSupportedException("Room-start capture cannot discard player actions or choices.");
        var loaded = SaveManager.Instance.LoadRunSave();
        if (!loaded.Success || loaded.SaveData == null)
            throw new InvalidOperationException("Native room-start save is unavailable.");
        replay.serializableRun = loaded.SaveData;
        replay.events.Clear();
        replay.checksumData.Clear();
        return new CombatCheckpoint(replay);
    }

    internal bool Finished => !CombatManager.Instance.IsInProgress && !Manager.ActionExecutor.IsRunning
        && Manager.ActionQueueSet.IsEmpty;

    internal bool Stable(RunState run) => !HasChoice && CombatManager.Instance.IsInProgress
        && !Manager.NetService.IsGameLoading && !Manager.ActionExecutor.IsRunning
        && Manager.ActionQueueSet.IsEmpty && !CombatManager.Instance.IsStarting
        && !CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo
        && !CombatManager.Instance.PlayerActionsDisabled
        && run.Players.Single().PlayerCombatState?.Phase == PlayerTurnPhase.Play;

    internal string Fingerprint(RunState run)
    {
        Check(run);
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(NetFullCombatState.FromRun(run, null).Anonymized());
        foreach (var player in run.Players) writer.Write(player.PlayerRng.ToSerializable());
        var combat = (run.CurrentRoom as CombatRoom)?.CombatState
            ?? throw new InvalidOperationException("State capture requires a combat room.");
        writer.WriteInt(combat.RoundNumber);
        writer.WriteEnum(combat.CurrentSide);
        foreach (var creature in combat.Creatures)
        {
            if (creature.Monster is not { } monster) continue;
            writer.WriteBool(monster.Rng != null);
            if (monster.Rng != null) writer.Write(monster.Rng.ToSerializable());
            writer.WriteString(monster.NextMove.Id);
            var moves = monster.MoveStateMachine?.StateLog;
            writer.WriteInt(moves?.Count ?? 0);
            if (moves != null)
                foreach (var move in moves) writer.WriteString(move.Id);
        }
        writer.WriteBool(HasChoice);
        if (_choices.Pending is { } pending)
        {
            writer.WriteInt(pending.Minimum);
            writer.WriteInt(pending.Maximum);
            writer.WriteInt(pending.Options.Length);
            foreach (var card in pending.Options) writer.Write(card.ToSerializable());
        }
        writer.ZeroByteRemainder();
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    internal async ValueTask Until(Func<bool> ready, string stage)
    {
        EnsureOwner();
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (_failure != null) throw new InvalidOperationException("Official action execution failed.", _failure);
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException(stage);
            Pump();
            if (ready()) break;
            await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
        if (_failure != null) throw new InvalidOperationException("Official action execution failed.", _failure);
    }
}

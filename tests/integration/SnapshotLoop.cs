using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal sealed class SnapshotLoop : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo Turn = typeof(CombatManager).GetField("_turnState", Flags)!;
    private static readonly FieldInfo Loop = typeof(CombatManager).GetField("_turnLoopTask", Flags)!;
    private static readonly Type TurnType = Turn.FieldType;
    private static readonly FieldInfo Cancellation = TurnType.GetField("_cts", Flags)!;
    private static readonly MethodInfo Cancel = TurnType.GetMethod("Cancel", Flags)!;
    private static readonly MethodInfo AwaitEnd = typeof(CombatManager).GetMethod("AwaitTurnEndAndSwitchSides", Flags)!;
    private static readonly MethodInfo StartTurn = typeof(CombatManager).GetMethod("StartTurn", Flags)!;
    private RunState? _run;
    private object? _turn;
    private readonly SnapshotGraph _graph;
    private bool _disposed;

    internal SnapshotGraph Graph => _graph;

    internal SnapshotLoop(NativeCombat combat, RunState run)
    {
        if (combat.Mode != CombatExecution.Worker || !combat.Stable(run))
            throw new InvalidOperationException("Snapshot requires an idle worker decision.");
        _run = run;
        _turn = Turn.GetValue(CombatManager.Instance)!;
        var manager = RunManager.Instance;
        _graph = new SnapshotGraph(run, CombatManager.Instance, manager.ActionQueueSet,
            manager.ActionQueueSynchronizer, manager.PlayerChoiceSynchronizer, manager.ActionExecutor, NetCombatCardDb.Instance);
        if (_graph.Runs.Length != 1)
        {
            _graph.Dispose();
            throw new InvalidOperationException("Snapshot graph contains an abandoned run.");
        }
    }

    internal async ValueTask Restore(NativeCombat combat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run == null || _turn == null || combat.Mode != CombatExecution.Worker || !ReferenceEquals(_run, RunManager.Instance.DebugOnlyGetState())
            || !combat.Stable(_run) || !ReferenceEquals(_turn, Turn.GetValue(CombatManager.Instance)))
            throw new InvalidOperationException("Snapshot restore requires its original idle worker combat.");
        var previous = (Task)Loop.GetValue(CombatManager.Instance)!;
        var cancellation = (CancellationTokenSource)Cancellation.GetValue(_turn)!;
        Cancel.Invoke(_turn, null);
        await combat.Until(() => previous.IsCompleted, "snapshot turn-loop cancellation");
        if (previous.IsFaulted) throw new InvalidOperationException("Previous turn loop failed.", previous.Exception);
        cancellation.Dispose();
        _graph.Restore();
        Cancellation.SetValue(_turn, new CancellationTokenSource());
        foreach (string name in new[] { "EndTurnSignalSource", "BeginEnemyTurnSignalSource" })
        {
            PropertyInfo property = TurnType.GetProperty(name, Flags)!;
            property.SetValue(_turn, Activator.CreateInstance(property.PropertyType));
        }
        Loop.SetValue(CombatManager.Instance, Resume(_turn));
        if (!combat.Stable(_run)) throw new InvalidOperationException("Restored combat is not ready.");
    }

    private static async Task Resume(object turn)
    {
        var manager = CombatManager.Instance;
        object? action = await Switch(turn);
        while (manager.IsInProgress && ReferenceEquals(turn, Turn.GetValue(manager)))
        {
            var state = (CombatState)TurnType.GetProperty("State", Flags)!.GetValue(turn)!;
            bool player = state.CurrentSide == CombatSide.Player;
            await (Task)StartTurn.Invoke(manager, [turn, player ? null : action])!;
            action = player ? await Switch(turn) : null;
        }
    }

    private static async Task<object?> Switch(object turn)
    {
        var task = (Task)AwaitEnd.Invoke(CombatManager.Instance, [turn])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _graph.Dispose();
        _run = null;
        _turn = null;
    }
}

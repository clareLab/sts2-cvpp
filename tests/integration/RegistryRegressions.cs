using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class RegistryRegressions
{
    internal static async Task<object> Run(NativeCombat combat, CombatCheckpoint checkpoint)
    {
        combat.Mode = CombatExecution.Worker;
        var abandoned = new List<WeakReference>();
        for (int index = 0; index < 12; index++)
        {
            abandoned.Add(await Restore(combat, checkpoint));
            CheckSubscriptions();
        }
        await combat.Restore(checkpoint);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int[] alive = abandoned.Select((reference, index) => (reference, index)).Where(pair => pair.reference.IsAlive).Select(pair => pair.index).ToArray();
        if (alive.Length != 0)
            throw new InvalidOperationException("Abandoned replay runs remain reachable: " + string.Join(",", alive));
        return new { repeated_restores = abandoned.Count, abandoned_runs_alive = 0, subscriptions_bounded = true };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSubscriptions()
    {
        FieldInfo field = typeof(NetCombatCardDb).GetField("_subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var run = RunManager.Instance.DebugOnlyGetState()!;
        int expected = run.Players.Sum(player => player.PlayerCombatState!.AllPiles.Count());
        if (((ICollection)field.GetValue(NetCombatCardDb.Instance)!).Count != expected)
            throw new InvalidOperationException("Card registry retains subscriptions from an abandoned combat.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> Restore(NativeCombat combat, CombatCheckpoint checkpoint)
        => new(await combat.Restore(checkpoint));
}

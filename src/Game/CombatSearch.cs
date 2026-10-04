using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal sealed record CombatSearchResult(SearchResult Search, uint Restores, uint Actions);

internal static class CombatSearch
{
    internal static async Task<CombatSearchResult> Run(NativeCombat combat, CombatCheckpoint checkpoint,
        uint nodeLimit, ushort depthLimit, int turns, TimeSpan timeLimit, CancellationToken cancellationToken = default)
    {
        if (turns is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(turns));
        var cursor = new ReplayCursor<RunState>(depthLimit, () => combat.Restore(checkpoint), combat.Execute);
        int initialTurn = 0;
        var result = await SearchDriver.Run(nodeLimit, depthLimit, timeLimit, async path =>
        {
            var run = await cursor.MoveTo(path);
            if (path.IsEmpty) initialTurn = run.Players.Single().PlayerCombatState!.TurnNumber;
            return Evaluate(combat, run, initialTurn, turns);
        }, cancellationToken);
        return new CombatSearchResult(result, cursor.Restores, cursor.Actions);
    }

    internal static BranchEvaluation Evaluate(NativeCombat combat, RunState run, int initialTurn, int turns)
    {
        bool solution = !combat.HasChoice && (combat.Finished
            || run.Players.Single().PlayerCombatState!.TurnNumber - initialTurn >= turns);
        return new BranchEvaluation(Score(combat, run), solution, solution ? [] : combat.Actions(run));
    }

    internal static long Score(NativeCombat combat, RunState run)
    {
        var player = run.Players.Single();
        if (player.Creature.IsDead) return long.MinValue;
        var state = (run.CurrentRoom as CombatRoom)?.CombatState
            ?? throw new InvalidOperationException("Search left the combat room.");
        long enemyHp = state.Enemies.Sum(c => (long)c.CurrentHp);
        return (combat.Victory ? 1_000_000_000_000L : 0) + player.Creature.CurrentHp * 1_000_000L - enemyHp;
    }
}

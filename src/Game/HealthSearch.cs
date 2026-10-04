using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class HealthSearch
{
    internal static async Task<SolveResult> Run(NativeCombat combat, Func<ValueTask<RunState>> restore,
        SolveOptions options, Action<SolveProgress>? progress = null, CancellationToken cancellationToken = default, uint[]? incumbent = null)
    {
        options.Validate();
        using var planner = new NativePlanner(options.Nodes, options.Depth);
        var cursor = new ReplayCursor<RunState>(options.Depth, restore, combat.Execute);
        var random = new Random(options.Seed);
        var buffer = new uint[options.Depth];
        var timer = Stopwatch.StartNew();
        uint[]? best = null;
        int bestHp = -1;
        string reason = "exhausted";
        long lastProgress = 0;
        bool Expired() => cancellationToken.IsCancellationRequested || timer.Elapsed.TotalSeconds >= options.Seconds;
        if (incumbent is { Length: > 0 })
        {
            if (incumbent.Length > options.Depth || incumbent.Any(action => (action & 0xc0000000) == NativeCombat.Potion))
                throw new InvalidDataException("The previous route exceeds the search limits.");
            var state = await cursor.MoveTo(incumbent);
            if (!combat.Finished || !combat.Victory) throw new InvalidDataException("The previous route no longer wins.");
            best = (uint[])incumbent.Clone();
            bestHp = state.Players.Single().Creature.CurrentHp;
            progress?.Invoke(new SolveProgress(0, 1, bestHp, timer.Elapsed.TotalMilliseconds));
        }
        while (!Expired())
        {
            int length = planner.Next(buffer);
            if (length < 0) break;
            var state = await cursor.MoveTo(buffer.AsMemory(0, length));
            bool terminal = combat.Finished;
            uint[] actions = terminal ? [] : Ordered(combat, state);
            int steps = length;
            while (!combat.Finished && steps < options.Depth && !Expired())
            {
                var available = steps == length ? actions : Ordered(combat, state);
                if (available.Length == 0) break;
                int index = planner.Stats.Simulations == 0 || random.NextDouble() < .8 ? 0 : random.Next(available.Length);
                buffer[steps++] = available[index];
                state = await cursor.MoveTo(buffer.AsMemory(0, steps));
            }
            int hp = combat.Finished && combat.Victory ? state.Players.Single().Creature.CurrentHp : -1;
            bool improved = hp >= 0 && (hp > bestHp || (hp == bestHp && (best == null || steps < best.Length)));
            if (improved)
            {
                bestHp = hp;
                best = buffer[..steps];
            }
            planner.Observe(hp < 0 ? 0 : checked(hp + 1), terminal, actions);
            if (timer.ElapsedMilliseconds - lastProgress >= 250 || improved)
            {
                var stats = planner.Stats;
                progress?.Invoke(new SolveProgress(stats.Simulations, stats.Nodes, bestHp < 0 ? null : bestHp, timer.Elapsed.TotalMilliseconds));
                lastProgress = timer.ElapsedMilliseconds;
            }
        }
        if (cancellationToken.IsCancellationRequested) reason = "cancelled";
        else if (timer.Elapsed.TotalSeconds >= options.Seconds) reason = "time_limit";
        else if (planner.Stats.Bounded != 0) reason = "node_or_depth_limit";
        CombatPlan? plan = null;
        if (best != null)
        {
            var run = await restore();
            int initialTurn = run.Players.Single().PlayerCombatState?.TurnNumber ?? 0;
            var route = new List<PlanStep>();
            foreach (uint token in best)
            {
                string before = combat.Fingerprint(run);
                string label = combat.Label(run, token);
                string kind = token == NativeCombat.EndTurn ? "turn" : (token & 0xc0000000) == NativeCombat.Selection ? "choice" : "card";
                int turn = run.Players.Single().PlayerCombatState?.TurnNumber ?? initialTurn;
                await combat.Execute(run, token);
                route.Add(new PlanStep(token, label, kind, turn, before, combat.Fingerprint(run)));
            }
            if (!combat.Finished || !combat.Victory || run.Players.Single().Creature.CurrentHp != bestHp)
                throw new InvalidOperationException("The winning route did not reproduce.");
            plan = new CombatPlan(route.ToArray(), bestHp, route.Count == 0 ? 0 : route[^1].Turn - initialTurn + 1);
        }
        return new SolveResult(plan, planner.Stats, timer.Elapsed.TotalMilliseconds, reason, cursor.Restores, cursor.Actions);
    }

    private static uint[] Ordered(NativeCombat combat, RunState run)
    {
        var actions = combat.Actions(run, includePotions: false);
        if (combat.HasChoice) return actions;
        var player = run.Players.Single();
        var state = player.Creature.CombatState!;
        decimal incoming = state.HittableEnemies.Sum(enemy => enemy.Monster?.NextMove?.Intents
            .OfType<AttackIntent>().Sum(intent => intent.GetSingleDamage(state.Allies, enemy) * intent.Repeats) ?? 0);
        decimal needed = Math.Max(0, incoming - player.Creature.Block);
        double Value(uint action)
        {
            if (action == NativeCombat.EndTurn) return -100;
            var (card, target) = combat.Resolve(run, action);
            var vars = card.DynamicVars.Clone(card);
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, vars);
            decimal damage = vars.Values.Where(v => v.Name == "Damage").Sum(v => v.PreviewValue);
            decimal block = vars.Values.Where(v => v.Name == "Block").Sum(v => v.PreviewValue);
            double value = card.Type switch { CardType.Attack => 8, CardType.Power => 6, _ => 2 };
            value += (double)Math.Min(block, needed) * 1.5;
            value += (double)damage;
            if (target != null && damage >= target.CurrentHp + target.Block) value += 50;
            if (card.Type == CardType.Skill && block > 0 && needed == 0) value -= 20;
            return value / Math.Max(1, card.EnergyCost.GetResolved());
        }
        return actions.OrderByDescending(Value).ThenBy(action => action).ToArray();
    }
}

using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class HealthSearch
{
    private readonly record struct Observation(bool Terminal, int Hp, uint[] Actions);

    internal static async Task<SolveResult> Run(NativeCombat combat, Func<ValueTask<RunState>> restore,
        SolveOptions options, Action<SolveProgress>? progress = null, CancellationToken cancellationToken = default, uint[]? incumbent = null,
        bool cache = true, SearchPause? pause = null)
    {
        options.Validate();
        using var planner = new NativePlanner(options.Nodes, options.Depth);
        var cursor = new ReplayCursor<RunState>(options.Depth, restore, combat.Execute);
        var tree = new ReplayTree<RunState, Observation>(cursor, state => new Observation(combat.Finished,
            combat.Finished && combat.Victory ? state.Players[0].Creature.CurrentHp : -1,
            combat.Finished ? [] : Ordered(combat, state)), cache ? (uint)Math.Min(1_000_000UL, (ulong)options.Nodes * options.Depth) : 0);
        var random = new Random(options.Seed);
        var buffer = new uint[options.Depth];
        var timer = Stopwatch.StartNew();
        uint[]? best = null;
        CombatPlan? plan = null;
        int bestHp = -1;
        string reason = "exhausted";
        long lastProgress = -250;
        bool changed = false;
        bool Expired() => cancellationToken.IsCancellationRequested || (options.Seconds > 0 && timer.Elapsed.TotalSeconds >= options.Seconds);
        async Task Publish(bool paused = false)
        {
            if (changed && best != null)
            {
                plan = await Describe(combat, cursor, best, bestHp);
                changed = false;
            }
            if (paused) timer.Stop();
            var stats = planner.Stats;
            progress?.Invoke(new SolveProgress(stats.Simulations, stats.Nodes, plan?.FinalHp, timer.Elapsed.TotalMilliseconds, plan, Paused: paused));
            lastProgress = timer.ElapsedMilliseconds;
        }
        async ValueTask WaitIfPaused()
        {
            if (pause?.Paused != true) return;
            try
            {
                while (pause.Paused && !cancellationToken.IsCancellationRequested)
                {
                    await Publish(paused: true);
                    await pause.Wait(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            finally { timer.Start(); }
            await Publish();
        }
        await WaitIfPaused();
        if (incumbent is { Length: > 0 })
        {
            if (incumbent.Length > options.Depth || incumbent.Any(action => (action & 0xc0000000) == NativeCombat.Potion))
                throw new InvalidDataException("The previous route exceeds the search limits.");
            var state = await cursor.MoveTo(incumbent);
            if (!combat.Finished || !combat.Victory) throw new InvalidDataException("The previous route no longer wins.");
            best = (uint[])incumbent.Clone();
            bestHp = state.Players[0].Creature.CurrentHp;
            changed = true;
            await Publish();
        }
        while (!Expired())
        {
            await WaitIfPaused();
            if (Expired()) break;
            int length = planner.Next(buffer);
            if (length < 0) break;
            var observation = await tree.MoveTo(buffer.AsMemory(0, length));
            bool terminal = observation.Terminal;
            uint[] actions = observation.Actions;
            int steps = length;
            bool greedy = planner.Stats.Simulations == 0;
            while (!observation.Terminal && steps < options.Depth && !Expired())
            {
                await WaitIfPaused();
                if (Expired()) break;
                var available = observation.Actions;
                if (available.Length == 0) break;
                int index = greedy || random.NextDouble() < .8 ? 0 : random.Next(available.Length);
                buffer[steps++] = available[index];
                observation = await tree.MoveTo(buffer.AsMemory(0, steps));
                if (timer.ElapsedMilliseconds - lastProgress >= 250 && !changed) await Publish();
            }
            int hp = observation.Hp;
            if (hp >= 0 && (hp > bestHp || (hp == bestHp && (best == null || steps < best.Length))))
            {
                bestHp = hp;
                best = buffer[..steps];
                changed = true;
            }
            planner.Observe(hp < 0 ? 0 : checked(hp + 1), terminal, actions);
            if (plan == null && changed || timer.ElapsedMilliseconds - lastProgress >= 250) await Publish();
        }
        if (cancellationToken.IsCancellationRequested) reason = "cancelled";
        else if (options.Seconds > 0 && timer.Elapsed.TotalSeconds >= options.Seconds) reason = "time_limit";
        else if (planner.Stats.Bounded != 0) reason = "node_or_depth_limit";
        if (changed) await Publish();
        return new SolveResult(plan, planner.Stats, timer.Elapsed.TotalMilliseconds, reason, cursor.Restores, cursor.Actions);
    }

    private static async Task<CombatPlan> Describe(NativeCombat combat, ReplayCursor<RunState> cursor, uint[] path, int hp)
    {
        var run = await cursor.MoveTo(ReadOnlyMemory<uint>.Empty);
        int initialTurn = run.Players[0].PlayerCombatState?.TurnNumber ?? 0;
        var route = new PlanStep[path.Length];
        for (int index = 0; index < path.Length; index++)
        {
            uint token = path[index];
            int beforeHp = run.Players[0].Creature.CurrentHp;
            string before = combat.Fingerprint(run);
            string label = combat.Label(run, token);
            string? portrait = combat.Portrait(run, token);
            string kind = token == NativeCombat.EndTurn ? "turn" : (token & 0xc0000000) == NativeCombat.Selection ? "choice" : "card";
            int turn = run.Players[0].PlayerCombatState?.TurnNumber ?? initialTurn;
            run = await cursor.MoveTo(path.AsMemory(0, index + 1));
            route[index] = new PlanStep(token, label, kind, turn, before, combat.Fingerprint(run), portrait, run.Players[0].Creature.CurrentHp - beforeHp);
        }
        if (!combat.Finished || !combat.Victory || run.Players[0].Creature.CurrentHp != hp)
            throw new InvalidOperationException("The winning route did not reproduce.");
        return new CombatPlan(route, hp, route.Length == 0 ? 0 : route[^1].Turn - initialTurn + 1);
    }

    private static uint[] Ordered(NativeCombat combat, RunState run)
    {
        var actions = combat.Actions(run, includePotions: false);
        if (combat.HasChoice || actions.Length <= 1) return actions;
        var player = run.Players.Single();
        var state = player.Creature.CombatState!;
        decimal incoming = state.HittableEnemies.Sum(enemy => enemy.Monster?.NextMove?.Intents
            .OfType<AttackIntent>().Sum(intent => intent.GetSingleDamage(state.Allies, enemy) * intent.Repeats) ?? 0);
        decimal needed = Math.Max(0, incoming - player.Creature.Block);
        double Value(uint action)
        {
            if (action == NativeCombat.EndTurn) return -100;
            var (card, target) = combat.Resolve(run, action, validate: false);
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

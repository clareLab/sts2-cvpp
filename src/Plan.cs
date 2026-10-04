namespace cvpp;

internal sealed record PlanStep(uint Action, string Label, string Kind, int Turn, string Before, string After, string? Portrait = null);
internal sealed record CombatPlan(PlanStep[] Steps, int FinalHp, int Turns);
internal sealed record SolveProgress(uint Simulations, uint Nodes, int? BestHp, double ElapsedMs);
internal sealed record SolveResult(CombatPlan? Plan, PlannerStats Stats, double ElapsedMs, string StopReason, uint Restores, uint Actions);
internal sealed record SolveOptions(int Seconds = 15, uint Nodes = 4096, ushort Depth = 128, int Seed = 1)
{
    internal void Validate()
    {
        if (Seconds is < 1 or > 300 || Nodes is 0 or > 1_000_000 || Depth is < 8 or > 256)
            throw new ArgumentOutOfRangeException(nameof(Seconds), "Unsupported search budget.");
    }
}

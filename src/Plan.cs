namespace cvpp;

internal sealed record PlanStep(uint Action, string Label, string Kind, int Turn, string Before, string After, string? Portrait = null, int HpDelta = 0);
internal sealed record CombatPlan(PlanStep[] Steps, int FinalHp, int Turns);
internal sealed record SolveProgress(uint Simulations, uint Nodes, int? BestHp, double ElapsedMs, CombatPlan? Plan = null, long MemoryBytes = 0, bool Paused = false);
internal sealed record SolveResult(CombatPlan? Plan, PlannerStats Stats, double ElapsedMs, string StopReason, uint Restores, uint Actions);
internal sealed record SolveOptions(int Seconds = 15, uint Nodes = 4096, ushort Depth = 128, int Seed = 1, int MemoryMiB = 2048)
{
    internal void Validate()
    {
        if (Seconds is < 0 or > 86_400 || MemoryMiB is < 0 or > 1_048_576 || Nodes is 0 or > 1_000_000 || Depth is < 8 or > 256)
            throw new ArgumentOutOfRangeException(nameof(Seconds), "Unsupported search budget.");
    }
}

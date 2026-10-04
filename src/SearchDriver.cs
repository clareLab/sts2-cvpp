using System.Diagnostics;

namespace cvpp;

internal readonly record struct BranchEvaluation(long Score, bool Solution, uint[] Actions);
internal sealed record SearchResult(uint[]? Path, SearchStats Stats, double ElapsedMs, string StopReason);

internal static class SearchDriver
{
    internal static async Task<SearchResult> Run(uint nodeLimit, ushort depthLimit, TimeSpan timeLimit,
        Func<ReadOnlyMemory<uint>, Task<BranchEvaluation>> evaluate, CancellationToken cancellationToken = default)
    {
        if (timeLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeLimit));
        using var search = new NativeSearch(nodeLimit, depthLimit);
        var path = new uint[depthLimit];
        var timer = Stopwatch.StartNew();
        string stopReason = "exhausted";
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopReason = "cancelled";
                break;
            }
            if (timer.Elapsed >= timeLimit)
            {
                stopReason = "time_limit";
                break;
            }
            int length = search.Next(path);
            if (length < 0) break;
            var result = await evaluate(path.AsMemory(0, length));
            search.Observe(result.Score, result.Solution, result.Actions);
        }
        var stats = search.Stats;
        if (stopReason == "exhausted" && stats.Bounded != 0) stopReason = "node_or_depth_limit";
        int bestLength = search.Best(path);
        return new SearchResult(bestLength < 0 ? null : path[..bestLength], stats, timer.Elapsed.TotalMilliseconds, stopReason);
    }
}

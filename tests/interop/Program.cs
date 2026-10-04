using cvpp;
using System.Diagnostics;

uint abi = NativeCore.Initialize();
if (abi != NativeCore.ExpectedAbi) throw new InvalidOperationException("Native ABI verification failed.");
Console.WriteLine($"PASS C# to Rust native ABI {abi}");

using var search = new NativeSearch(16, 4);
uint[] path = new uint[4];
while (search.Next(path) is >= 0 and var length)
{
    switch (length, path[0])
    {
        case (0, _): search.Observe(0, false, [10, 20]); break;
        case (1, 10): search.Observe(0, false, [11, 12]); break;
        case (1, 20): search.Observe(7, true, []); break;
        case (2, 10): search.Observe(path[1] == 12 ? 12 : -5, true, []); break;
        default: throw new InvalidOperationException("Unexpected native route.");
    }
}
if (search.Best(path) != 2 || !path.AsSpan(0, 2).SequenceEqual([10u, 12u])
    || search.Stats with { MemoryBytes = 0 } != new SearchStats(5, 5, 0, 1, 12, 0)
    || search.Stats.MemoryBytes is < 512 or > 4096)
    throw new InvalidOperationException("Native search result or ABI layout differs.");
using var limited = new NativeSearch(2, 2);
if (limited.Next(path) != 0) throw new InvalidOperationException("Missing root.");
limited.Observe(0, false, [1, 2]);
if (limited.Next(path) != 1) throw new InvalidOperationException("Missing candidate.");
limited.Observe(-10, true, []);
if (limited.Next(path) != -1 || limited.Stats.Bounded != 1 || limited.Stats.BestScore != -10)
    throw new InvalidOperationException("Budget discarded the completed result.");
Console.WriteLine("PASS native search, budget, negative scores and struct layout");

using var cancellation = new CancellationTokenSource();
var interrupted = await SearchDriver.Run(8, 4, TimeSpan.FromSeconds(1), route =>
{
    if (route.Length == 0) return ValueTask.FromResult(new BranchEvaluation(0, false, [1, 2]));
    cancellation.Cancel();
    return ValueTask.FromResult(new BranchEvaluation(7, true, []));
}, cancellation.Token);
if (interrupted.StopReason != "cancelled" || interrupted.Path is not [1] || interrupted.Stats.BestScore != 7)
    throw new InvalidOperationException("Cancellation discarded a completed solution.");
Console.WriteLine("PASS cancellation preserves the best completed route");
await ReplayTests.Run();
SelectionTests.Run();
using (var planner = new NativePlanner(32, 8))
{
    var plan = new uint[8];
    while (planner.Next(plan) is >= 0 and var depth)
        planner.Observe(depth == 2 ? checked((int)(plan[0] + plan[1])) : 0, depth == 2, depth == 2 ? [] : [1, 2]);
    if (planner.Stats.Simulations != 7 || planner.Stats.Best != 4 || planner.Stats.Bounded != 0)
        throw new InvalidOperationException("Native planner lost branches or rewards.");
}
Console.WriteLine("PASS native planner traversal and reward backpropagation");
await WireTests.Run();
RateTests.Run();

if (args.Contains("--benchmark"))
{
    for (int iteration = 0; iteration < 4; iteration++)
    {
        using var tree = new NativeSearch(8191, 12);
        var route = new uint[12];
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        while (tree.Next(route) is >= 0 and var length)
        {
            long score = 0;
            for (int index = 0; index < length; index++) score = score * 2 + route[index];
            tree.Observe(score, length == 12, length == 12 ? [] : [0, 1]);
        }
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (tree.Stats.Evaluated != 8191 || tree.Stats.BestScore != 4095 || tree.Best(route) != 12)
            throw new InvalidOperationException("Native benchmark lost nodes or returned the wrong route.");
        if (iteration > 0)
            Console.WriteLine($"BENCH {tree.Stats.Evaluated} nodes, {elapsed:F3} ms, {allocated} hot-loop managed bytes, {tree.Stats.MemoryBytes} native bytes");
    }
}

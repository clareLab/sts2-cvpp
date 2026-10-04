using cvpp;

internal static class RateTests
{
    internal static void Run()
    {
        var rate = new SearchRate();
        rate.Reset();
        rate.Observe(10, 250);
        if (rate.PerSecond != 40) throw new InvalidOperationException("First search rate is incorrect.");
        for (int sample = 2; sample <= 20; sample++) rate.Observe((uint)(sample * 10), sample * 250);
        if (rate.PerSecond != 40) throw new InvalidOperationException("Sustained search rate is incorrect.");
        for (int sample = 21; sample <= 30; sample++) rate.Observe(200, sample * 250);
        if (rate.PerSecond != 0) throw new InvalidOperationException("Search rate retained old work after a stall.");
        rate.Observe(201, 7750);
        double latest = rate.PerSecond;
        rate.Observe(201, 7750);
        rate.Observe(0, 8000);
        if (latest <= 0 || rate.PerSecond != latest) throw new InvalidOperationException("Duplicate progress changed the search rate.");
        rate.Reset();
        if (rate.PerSecond != 0) throw new InvalidOperationException("Reset retained the previous search rate.");
        rate.Observe(1, 1000);
        if (rate.PerSecond != 1) throw new InvalidOperationException("A new search retained previous samples.");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 1001; sample <= 2000; sample++) rate.Observe((uint)sample - 999, sample);
        if (rate.PerSecond != 1000 || GC.GetAllocatedBytesForCurrentThread() != allocated)
            throw new InvalidOperationException("Dense progress updates changed the search rate or allocated memory.");
        Console.WriteLine("PASS search throughput, stalls, duplicate progress and reset");
    }
}

using Godot;

namespace cvpp;

internal static class WorkerRuntime
{
    private static int _threadId;

    internal static bool Active { get; set; }
    internal static bool PumpContinuations { get; set; }

    internal static void Initialize()
    {
        if (DisplayServer.GetName() != "headless"
            || !File.Exists(ProjectSettings.GlobalizePath("user://.cvpp-test-sandbox")))
            throw new InvalidOperationException("Worker requires an isolated headless process.");
        _threadId = System.Environment.CurrentManagedThreadId;
    }

    internal static void Pump()
    {
        if (!Active || !PumpContinuations) return;
        if (System.Environment.CurrentManagedThreadId != _threadId
            || SynchronizationContext.Current != Dispatcher.SynchronizationContext)
            throw new InvalidOperationException("Worker continuations require the Godot main thread and context.");
        Dispatcher.SynchronizationContext.ExecutePendingContinuations();
    }
}

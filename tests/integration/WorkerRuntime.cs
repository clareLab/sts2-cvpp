using Godot;

namespace cvpp;

internal static class WorkerRuntime
{
    internal static bool Active { get; set; }

    internal static void Initialize()
    {
        if (DisplayServer.GetName() != "headless"
            || !File.Exists(ProjectSettings.GlobalizePath("user://.cvpp-test-sandbox")))
            throw new InvalidOperationException("Worker requires an isolated headless process.");
    }
}

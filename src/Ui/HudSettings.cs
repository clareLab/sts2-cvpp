using Godot;

namespace cvpp;

internal static class HudSettings
{
    private static readonly ConfigFile File = new();
    internal static int Seconds { get; private set; } = 15;
    internal static int MemoryMiB { get; private set; } = 2048;
    internal static Vector2 Position { get; private set; } = new(0, .14f);
    private const string Path = "user://cvpp.cfg";

    internal static void Load()
    {
        if (File.Load(Path) != Error.Ok) return;
        int seconds = File.GetValue("solver", "seconds", 15).AsInt32();
        if (seconds is >= 0 and <= 86_400) Seconds = seconds;
        int memory = File.GetValue("solver", "memory_mib", 2048).AsInt32();
        if (memory is >= 0 and <= 1_048_576) MemoryMiB = memory;
        var position = File.GetValue("ui", "position", Position).AsVector2();
        if (float.IsFinite(position.X) && float.IsFinite(position.Y)) Position = position.Clamp(Vector2.Zero, Vector2.One);
    }

    internal static void Save(Vector2 position, int seconds, int memory)
    {
        Position = position;
        Seconds = seconds;
        MemoryMiB = memory;
        File.SetValue("solver", "seconds", Seconds);
        File.SetValue("solver", "memory_mib", MemoryMiB);
        File.SetValue("ui", "position", Position);
        if (File.Save(Path) != Error.Ok) GD.PrintErr("[cvpp] Could not save toolbar settings.");
    }
}

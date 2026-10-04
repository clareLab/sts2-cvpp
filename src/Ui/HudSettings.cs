using Godot;

namespace cvpp;

internal static class HudSettings
{
    private static readonly ConfigFile File = new();
    internal static int Seconds { get; private set; } = 15;
    internal static Vector2 Position { get; private set; } = new(0, .14f);
    private const string Path = "user://cvpp.cfg";

    internal static void Load()
    {
        if (File.Load(Path) != Error.Ok) return;
        int seconds = File.GetValue("solver", "seconds", 15).AsInt32();
        if (seconds is 5 or 15 or 30 or 60) Seconds = seconds;
        var position = File.GetValue("ui", "position", Position).AsVector2();
        if (float.IsFinite(position.X) && float.IsFinite(position.Y)) Position = position.Clamp(Vector2.Zero, Vector2.One);
    }

    internal static void Save(Vector2 position, int seconds)
    {
        Position = position;
        Seconds = seconds;
        File.SetValue("solver", "seconds", Seconds);
        File.SetValue("ui", "position", Position);
        if (File.Save(Path) != Error.Ok) GD.PrintErr("[cvpp] Could not save toolbar settings.");
    }
}

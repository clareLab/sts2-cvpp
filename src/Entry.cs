using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace cvpp;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        try
        {
            uint abi = NativeCore.Initialize();
            GD.Print($"[cvpp] Loaded {typeof(Entry).Assembly.GetName().Version?.ToString(3)}, Rust ABI {abi}");
#if CVPP_SELFTEST
            SelfTests.Initialize();
#endif
        }
        catch (Exception error)
        {
            GD.PrintErr("[cvpp] Disabled: " + error);
#if CVPP_SELFTEST
            if (OS.GetCmdlineArgs().Contains("--cvpp-selftest"))
                ((SceneTree)Engine.GetMainLoop()).Quit(1);
#endif
        }
    }
}

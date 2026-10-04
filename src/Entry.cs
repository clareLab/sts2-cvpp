using Godot;
using HarmonyLib;
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
            new Harmony("clarelab.cvpp.choices").CreateClassProcessor(typeof(ChoiceContractPatch)).Patch();
            GD.Print($"[cvpp] Loaded {typeof(Entry).Assembly.GetName().Version?.ToString(3)}, Rust ABI {abi}");
            if (WorkerHost.Active) { WorkerHost.Initialize(); return; }
#if CVPP_SELFTEST
            if (OS.GetCmdlineArgs().Contains("--cvpp-selftest")) { SelfTests.Initialize(); return; }
#endif
            SolverController.Initialize();
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

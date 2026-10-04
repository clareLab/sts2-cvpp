using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
internal static class HeadlessRunSave
{
    private static bool Prefix(ref Task __result)
    {
        if (!TestMode.IsOn) return true;
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveProgressFile))]
internal static class HeadlessProgressSave
{
    private static bool Prefix() => !TestMode.IsOn;
}

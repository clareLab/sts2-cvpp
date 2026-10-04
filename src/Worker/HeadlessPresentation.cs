using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

[HarmonyPatch(typeof(CombatStateTracker), "NotifyCombatStateChanged")]
internal static class HeadlessPresentation
{
    private static bool Prefix() => !TestMode.IsOn;
}

[HarmonyPatch(typeof(NDebugAudioManager), nameof(NDebugAudioManager.Play))]
internal static class HeadlessAudio
{
    private static bool Prefix(ref int __result)
    {
        if (!TestMode.IsOn) return true;
        __result = -1;
        return false;
    }
}

[HarmonyPatch(typeof(NDebugAudioManager), nameof(NDebugAudioManager.Stop))]
internal static class HeadlessAudioStop
{
    private static bool Prefix([HarmonyArgument(0)] int id) => !TestMode.IsOn || id != -1;
}

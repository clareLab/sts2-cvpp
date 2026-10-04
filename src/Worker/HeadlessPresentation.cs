using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

[HarmonyPatch(typeof(CombatStateTracker), "NotifyCombatStateChanged")]
internal static class HeadlessPresentation
{
    private static bool Prefix() => !TestMode.IsOn;
}

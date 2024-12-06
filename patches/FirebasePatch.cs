using DebugToolbox.utils;
using HarmonyLib;

namespace DebugToolbox.Patches;

internal static class FirebasePatch
{
    public static void SelfPatch()
    {
        var harmony = new Harmony(PatchUtils.PatchID(typeof(FirebasePatch)));
        harmony.Patch(AccessTools.Method(typeof(InitStuff), nameof(InitStuff.initFirebase)),
            new HarmonyMethod(typeof(FirebasePatch), nameof(Init_Prefix)));
    }

    private static bool Init_Prefix()
    {
        return false;
    }
}
using DebugToolbox.utils;
using HarmonyLib;

namespace DebugToolbox.Patches;

internal static class DiscordTrackerPatch
{
    public static void SelfPatch()
    {
        var harmony = new Harmony(PatchUtils.PatchID(typeof(DiscordTrackerPatch)));
        harmony.Patch(AccessTools.Method(typeof(DiscordTracker), nameof(DiscordTracker.Start)),
            new HarmonyMethod(typeof(DiscordTrackerPatch), nameof(Start_Prefix)));
    }

    private static bool Start_Prefix()
    {
        return false;
    }
}
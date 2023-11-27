using System.Collections.Generic;
using DebugToolbox.utils;
using HarmonyLib;

namespace DebugToolbox.Patches
{
    public static class DictionaryPatch<TKey, TValue>
    {
        public static void SelfPatch()
        {
            Harmony harmony = new Harmony(PatchUtils.PatchID(typeof(DictionaryPatch<TKey, TValue>)));
            harmony.Patch(typeof(Dictionary<TKey, TValue>).GetMethod("get_Item"), 
                prefix: new HarmonyMethod(typeof(DictionaryPatch<TKey, TValue>), nameof(get_Item_Prefix)));
        }

        private static bool get_Item_Prefix(Dictionary<TKey, TValue> __instance, ref TValue __result, TKey key)
        {
            if (__instance.TryGetValue(key, out TValue value))
            {
                __result = value;
                return false;
            }
            throw new KeyNotFoundException($"Key not found: {key}");
        }
    }
}
using System.Collections.Generic;
using System.Text;
using DebugToolbox.utils;
using HarmonyLib;

namespace DebugToolbox.Patches
{
    internal static class DictionaryPatch<TKey, TValue> where TKey : class
    {
        public static void SelfPatch()
        {
            Harmony harmony = new Harmony(PatchUtils.PatchID(typeof(DictionaryPatch<TKey, TValue>)));
            harmony.Patch(typeof(Dictionary<TKey, TValue>).GetMethod("get_Item"),
                prefix: new HarmonyMethod(typeof(DictionaryPatch<TKey, TValue>), nameof(get_Item_Prefix)));
        }

        private static bool get_Item_Prefix(Dictionary<TKey, TValue> __instance, ref TValue __result, TKey key)
        {
            if (key == null) return true;
            if (__instance.TryGetValue(key, out TValue value))
            {
                __result = value;
                return false;
            }

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<TKey, TValue> pair in __instance)
            {
                sb.AppendLine($"\t\"{pair.Key}\"({pair.Key.GetAddress()}) : \"{pair.Value}\"");
            }

            throw new KeyNotFoundException($"Key not found: \"{key}\"({key.GetAddress()}) in: \n{sb.ToString()}");
        }
    }
}
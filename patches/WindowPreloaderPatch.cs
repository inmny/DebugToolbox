using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using DebugToolbox.utils;

namespace DebugToolbox.Patches
{
    /// <summary>
    ///     程序化触发世界生成时，启动阶段的窗口预加载可能与加载阶段的预加载并发，
    ///     同一窗口完成两次注册会抛“键已存在”并让加载流程无限重试。
    ///     这里吞掉重复注册：已有同名窗口时直接销毁重复实例。
    /// </summary>
    internal static class WindowPreloaderPatch
    {
        private static FieldInfo _preloadedWindows;

        public static void SelfPatch()
        {
            Harmony harmony = new Harmony(PatchUtils.PatchID(typeof(WindowPreloaderPatch)));
            harmony.Patch(AccessTools.Method(typeof(WindowPreloader), "finishPreloadingWindow"),
                prefix: new HarmonyMethod(typeof(WindowPreloaderPatch), nameof(finishPreloadingWindow_Prefix)));
        }

        private static bool finishPreloadingWindow_Prefix(string pWindowID, ScrollWindow pWindow)
        {
            if (_preloadedWindows == null)
            {
                _preloadedWindows = AccessTools.Field(typeof(WindowPreloader), "_preloaded_windows");
            }

            if (_preloadedWindows?.GetValue(null) is IDictionary existing && existing.Contains(pWindowID))
            {
                Object.Destroy(pWindow.gameObject);
                return false;
            }

            return true;
        }
    }
}

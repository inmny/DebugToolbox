using DebugToolbox.utils;
using HarmonyLib;
using UnityEngine;
using GameConsole = WorldBoxConsole.Console;

namespace DebugToolbox.Patches;

/// <summary>让原版日志窗口保持关闭，同时保留日志记录。</summary>
internal static class WorldBoxConsolePatch
{
    /// <summary>拦截错误弹窗、快捷键和直接激活窗口的入口。</summary>
    public static void SelfPatch()
    {
        var harmony = new Harmony(PatchUtils.PatchID(typeof(WorldBoxConsolePatch)));
        var consoleType = typeof(GameConsole);
        var preventOpening = new HarmonyMethod(typeof(WorldBoxConsolePatch), nameof(PreventOpening));
        harmony.Patch(AccessTools.DeclaredMethod(consoleType, nameof(GameConsole.Show)), prefix: preventOpening);
        harmony.Patch(AccessTools.DeclaredMethod(consoleType, nameof(GameConsole.Toggle)), prefix: preventOpening);
        harmony.Patch(AccessTools.DeclaredMethod(consoleType, "OnEnable"),
            postfix: new HarmonyMethod(typeof(WorldBoxConsolePatch), nameof(KeepHidden)));

        foreach (var console in Object.FindObjectsOfType<GameConsole>())
        {
            console.Hide();
        }
    }

    /// <summary>收到显示或切换请求时收起窗口。</summary>
    /// <param name="__instance">原版日志窗口。</param>
    private static bool PreventOpening(GameConsole __instance)
    {
        __instance.Hide();
        return false;
    }

    /// <summary>窗口被直接激活时在绘制前收起。</summary>
    /// <param name="__instance">原版日志窗口。</param>
    private static void KeepHidden(GameConsole __instance)
    {
        __instance.Hide();
    }
}

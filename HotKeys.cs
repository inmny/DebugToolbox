using NeoModLoader.api.attributes;
using ReflectionUtility;
using UnityEngine;

namespace DebugToolbox;

internal static class HotKeys
{
    [Hotfixable]
    public static void init()
    {
        AssetManager.hotkey_library.add(new HotkeyAsset{
                id = "debug_window",
                default_key_1 = KeyCode.J,
                just_pressed_action = PressJ
            }
        );
        
        AssetManager.hotkey_library.post_init();
    }
    [Hotfixable]
    private static void PressJ(HotkeyAsset asset)
    {
        if (!DebugConfig.instance.debugButton.activeSelf)
        {
            DebugConfig.instance.debugButton.SetActive(true);
        }

        if(ScrollWindow.currentWindows.Count == 0)
        {
            ScrollWindow.get("debug").clickShow();
        }
        else if(ScrollWindow.currentWindows.Contains(ScrollWindow.get("debug")))
        {
            ScrollWindow.get("debug").clickHide();
        }
    }
}
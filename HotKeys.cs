using NeoModLoader.api.attributes;
using UnityEngine;

namespace DebugToolbox;

internal static class HotKeys
{
    [Hotfixable]
    public static void init()
    {
        AssetManager.hotkey_library.add(new HotkeyAsset
            {
                id = "debug_window",
                default_key_1 = KeyCode.J,
                default_key_mod_1 = KeyCode.LeftControl,
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
        ScrollWindow.get("debug").clickShow();
    }
}
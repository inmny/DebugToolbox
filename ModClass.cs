using System;
using System.IO;
using System.Reflection;
using DebugToolbox.Patches;
using DebugToolbox.Runtime;
using NeoModLoader.api;
using NeoModLoader.api.attributes;
using NeoModLoader.General;
using NeoModLoader.services;
using UnityEngine;

namespace DebugToolbox
{
    public class ModClass : BasicMod<ModClass>, IReloadable
    {
    public FullExceptionTracker Tracker;
    private DebugRuntime _runtime;
    private int _startupHideFrames = 600;
    private int _welcomeScanFrames;
    protected override void OnModLoad()
        {
            Config.isEditor = true;
            Config.editor_maxim = true;
            Config.editor_mastef = true;
            create_all_patches();
            DictionaryPatch<object,object>.SelfPatch();
            HotKeys.init();
            _runtime = DebugRuntime.Start();
            Tracker = new FullExceptionTracker(_runtime.Events, _runtime.Sources);
            LogInfo($"AI script endpoint: 127.0.0.1:{_runtime.Server.Port}");
            LogInfo($"AI script session: {_runtime.Server.SessionFile}");

            UnityExplorer.ExplorerStandalone.CreateInstance();
        }

        private void Update()
        {
            _runtime?.Update();
            HideStartupOverlays();
        }

        /// <summary>调试加载阶段自动收起 UnityExplorer 并关闭欢迎窗，保证截图与画面干净。</summary>
        private void HideStartupOverlays()
        {
            if (_startupHideFrames > 0)
            {
                _startupHideFrames--;
                try { UnityExplorer.UI.UIManager.ShowMenu = false; } catch { }
            }
            if (++_welcomeScanFrames < 60) return;
            _welcomeScanFrames = 0;
            var welcome = FindObjectOfType<WindowWelcome>();
            if (welcome == null)
            {
                return;
            }

            // 必须走正规关闭流程清空 ScrollWindow._current_window 等静态引用；
            // 裸 Destroy 会残留已销毁对象，世界每帧 meta 变动都会对它调
            // shouldClose 造成空引用死循环刷屏
            ScrollWindow window = welcome.GetComponent<ScrollWindow>()
                                  ?? welcome.GetComponentInChildren<ScrollWindow>(true);
            window?.clickHide();
        }

        private void create_all_patches()
        {
            Type[] types = Assembly.GetExecutingAssembly().GetTypes();
            foreach(var type in types)
            {
                if(type.Namespace == "DebugToolbox.Patches")
                {
                    try
                    {
                        type.GetMethod("SelfPatch").Invoke(null, new object[0]);
                    }
                    catch (Exception)
                    {
                        // ignored
                        continue;
                    }
                }
            }
        }
        [Hotfixable]
        public void Reload()
        {
            HotKeys.init();
            var locale_dir = GetLocaleFilesDirectory(GetDeclaration());
            foreach(var file in Directory.GetFiles(locale_dir, "*.json")){
                LM.LoadLocale(Path.GetFileNameWithoutExtension(file), file);
            }
            LM.ApplyLocale();
        }
    }
}

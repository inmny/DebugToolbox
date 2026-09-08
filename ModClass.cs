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

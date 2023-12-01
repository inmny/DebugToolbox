using System;
using System.IO;
using System.Reflection;
using DebugToolbox.Patches;
using NeoModLoader.api;
using NeoModLoader.api.attributes;
using NeoModLoader.General;

namespace DebugToolbox
{
    public class ModClass : BasicMod<ModClass>, IReloadable
    {
        protected override void OnModLoad()
        {
            Config.isEditor = true;
            Config.editor_maxim = true;
            Config.editor_mastef = true;
            Config.disableLocaleLogs = true;
            create_all_patches();
            DictionaryPatch<object,object>.SelfPatch();
            HotKeys.init();
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
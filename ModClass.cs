using System;
using System.Reflection;
using DebugToolbox.Patches;
using NeoModLoader.api;
using NeoModLoader.api.attributes;

namespace DebugToolbox
{
    public class ModClass : BasicMod<ModClass>, IReloadable
    {
        protected override void OnModLoad()
        {
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
        }
    }
}
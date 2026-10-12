using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;
namespace ApexForge.ProjectOptimizer
{
    public static class AddressablesUtility
    {
        public static bool IsInstalled => FindSettingsType()!=null;
        public static void AddExplicitRoots(HashSet<string> roots)
        {
            if(roots==null)return;
            try
            {
                var type=FindSettingsType(); if(type==null)return;
                var prop=type.GetProperty("Settings",BindingFlags.Public|BindingFlags.Static); var settings=prop?.GetValue(null,null); if(settings==null)return;
                // Reflection over optional package versions is intentionally best effort. The groups fallback below is simpler and compatible with 1.20/1.21.
                var groupsProp=settings.GetType().GetProperty("groups"); var groups=groupsProp?.GetValue(settings,null) as IEnumerable; if(groups==null)return;
                foreach(var group in groups){if(group==null)continue;var entriesProp=group.GetType().GetProperty("entries");var entries=entriesProp?.GetValue(group,null) as IEnumerable;if(entries==null)continue;foreach(var entry in entries){string guid=entry?.GetType().GetField("guid",BindingFlags.Public|BindingFlags.Instance)?.GetValue(entry) as string; if(string.IsNullOrEmpty(guid))guid=entry?.GetType().GetProperty("guid")?.GetValue(entry,null) as string; if(string.IsNullOrEmpty(guid))continue;var path=AssetDatabaseThreadSafeUtility.IsComplete ? AssetDatabaseThreadSafeUtility.GUIDToAssetPath(guid) : AssetDatabase.GUIDToAssetPath(guid);if(!string.IsNullOrEmpty(path))roots.Add(path);}}
            }catch{ }
        }
        static Type FindSettingsType(){foreach(var asm in AppDomain.CurrentDomain.GetAssemblies()){var t=asm.GetType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");if(t!=null)return t;}return null;}
    }
}

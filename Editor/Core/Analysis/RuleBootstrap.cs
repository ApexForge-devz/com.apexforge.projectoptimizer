using System;
using System.Runtime.CompilerServices;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Explicitly initializes every built-in analyzer before catalogue-dependent audits or scans.</summary>
    public static class RuleBootstrap
    {
        static bool s_Initialized;
        static bool s_Initializing;

        public static void EnsureBuiltInRules()
        {
            if (s_Initialized || s_Initializing) return;
            s_Initializing = true;
            try
            {
                RuntimeHelpers.RunClassConstructor(typeof(AssemblyDefinitionAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(AnimatorControllerAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(ProjectAuditorSupplementAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(TextureAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(ModelAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(AddressablesBuildLayoutAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(UnusedAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(AudioAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(SpriteAtlasAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(WorldDataAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(DuplicateAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(VideoAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(ProjectSettingsAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(ScriptAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(BuildProfileAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(AnimationAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(FontAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(UIToolkitAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(RenderingEvidenceAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(TextMeshProAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(PrefabSceneAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(DependencyHotspotAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(MaterialShaderAnalyzer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(AndroidCompatibilityAnalyzer).TypeHandle);

                // Retain aliases only where their historical IDs do not identify active rules.
                RuleCatalog.RegisterAlias("PRJ077", "PRJ013");
                RuleCatalog.RegisterAlias("PRJ078", "PRJ014");
                RuleCatalog.RegisterAlias("PRJ081", "PRJ059");
                RuleCatalog.RegisterAlias("PAS0031", "PRJ030");
                s_Initialized = true;
            }
            finally
            {
                s_Initializing = false;
            }
        }
    }
}

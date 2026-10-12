using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Centralizes the optimizer's hardware-profile to Unity build-target translation.</summary>
    public static class PlatformUtility
    {
        /// <summary>Returns true when the selected profile can resolve a real Unity build target without guessing.</summary>
        public static bool HasConcreteTarget(OptimizerPlatform platform)
        {
            return ToBuildTarget(platform) != BuildTarget.NoTarget;
        }

        public static BuildTarget ToBuildTarget(OptimizerPlatform platform)
        {
            switch (platform)
            {
                case OptimizerPlatform.Android: return BuildTarget.Android;
                case OptimizerPlatform.iOS: return BuildTarget.iOS;
                case OptimizerPlatform.WebGL: return BuildTarget.WebGL;
                case OptimizerPlatform.Console: return GetConcreteActiveConsoleTarget();
                case OptimizerPlatform.StandaloneVR:
                case OptimizerPlatform.PCVR:
                case OptimizerPlatform.Desktop:
                default: return GetStandaloneTarget();
            }
        }

        public static NamedBuildTarget ToNamedBuildTarget(OptimizerPlatform platform)
        {
            BuildTarget target = ToBuildTarget(platform);
            if (target == BuildTarget.NoTarget) return NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Unknown);
            return NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
        }

        public static NamedBuildTarget ToNamedBuildTarget(BuildTargetGroup group)
        {
            return NamedBuildTarget.FromBuildTargetGroup(group);
        }

        public static string GetAnalyzerTargetPlatform(ScanContext context, string fallback)
        {
            try
            {
                var settings = context != null ? context.Settings : null;
                string key = settings != null ? settings.PlatformString : null;
                return string.IsNullOrEmpty(key) ? fallback : key;
            }
            catch { return fallback; }
        }

        /// <summary>
        /// Returns the concrete NamedBuildTarget.TargetName used by importer platform overrides.
        /// The abstract Console profile deliberately resolves to an empty string when no console build
        /// target is active instead of silently inspecting Standalone settings.
        /// </summary>
        public static string GetImporterPlatformString(OptimizerPlatform platform)
        {
            try
            {
                BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
                if (platform == OptimizerPlatform.Console)
                    return IsConsoleTarget(active)
                        ? NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(active)).TargetName
                        : string.Empty;

                BuildTarget target = ToBuildTarget(platform);
                return NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target)).TargetName;
            }
            catch { return string.Empty; }
        }

        public static bool IsConsoleTarget(BuildTarget target)
        {
            string name = target.ToString();
            return name.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PS4", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PS5", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("GameCore", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Prefers Vulkan, guarantees OpenGLES3 fallback, and preserves other configured APIs.</summary>
        public static void PreferAndroidVulkan()
        {
            const BuildTarget target = BuildTarget.Android;
            bool useDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            var current = PlayerSettings.GetGraphicsAPIs(target) ?? Array.Empty<GraphicsDeviceType>();
            PreferAndroidVulkan(useDefaultApis, current, BuildAndroidVulkanPreferredOrder(current));
        }

        /// <summary>Applies the scanned API order only if Android graphics settings have not changed since analysis.</summary>
        internal static void PreferAndroidVulkan(bool expectedUseDefaultApis, GraphicsDeviceType[] expectedCurrent, GraphicsDeviceType[] preferred)
        {
            const BuildTarget target = BuildTarget.Android;
            bool useDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            var current = PlayerSettings.GetGraphicsAPIs(target) ?? Array.Empty<GraphicsDeviceType>();
            if (useDefaultApis != expectedUseDefaultApis || !GraphicsApiListsEqual(current, expectedCurrent))
                throw new InvalidOperationException("Android graphics API settings changed after analysis. Rescan before applying this fix.");
            if (preferred == null || preferred.Length < 2
                || preferred[0] != GraphicsDeviceType.Vulkan || preferred[1] != GraphicsDeviceType.OpenGLES3)
                throw new InvalidOperationException("The requested Android graphics API order must place Vulkan first and OpenGLES3 second.");

            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, preferred);
        }

        internal static bool GraphicsApiListsEqual(GraphicsDeviceType[] left, GraphicsDeviceType[] right)
        {
            left = left ?? Array.Empty<GraphicsDeviceType>();
            right = right ?? Array.Empty<GraphicsDeviceType>();
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        internal static GraphicsDeviceType[] BuildAndroidVulkanPreferredOrder(GraphicsDeviceType[] current)
        {
            var ordered = new List<GraphicsDeviceType> { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 };
            if (current != null)
            {
                for (int i = 0; i < current.Length; i++)
                {
                    GraphicsDeviceType api = current[i];
                    if (api == GraphicsDeviceType.Vulkan || api == GraphicsDeviceType.OpenGLES3 || ordered.Contains(api)) continue;
                    ordered.Add(api);
                }
            }
            return ordered.ToArray();
        }


        static BuildTarget GetStandaloneTarget()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                    return EditorUserBuildSettings.activeBuildTarget;
                default: return BuildTarget.StandaloneWindows64;
            }
        }

        static BuildTarget GetConcreteActiveConsoleTarget()
        {
            BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
            return IsConsoleTarget(active) ? active : BuildTarget.NoTarget;
        }
    }
}

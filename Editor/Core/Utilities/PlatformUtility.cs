using System;
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

        /// <summary>Moves Vulkan to the front of Android graphics APIs while preserving existing fallbacks.</summary>
        public static void PreferAndroidVulkan()
        {
            const BuildTarget target = BuildTarget.Android;
            var current = PlayerSettings.GetGraphicsAPIs(target) ?? Array.Empty<GraphicsDeviceType>();
            bool hasVulkan = false;
            for (int i = 0; i < current.Length; i++) if (current[i] == GraphicsDeviceType.Vulkan) { hasVulkan = true; break; }

            if (!hasVulkan)
            {
                var withVulkan = new GraphicsDeviceType[current.Length + 1];
                withVulkan[0] = GraphicsDeviceType.Vulkan;
                Array.Copy(current, 0, withVulkan, 1, current.Length);
                PlayerSettings.SetGraphicsAPIs(target, withVulkan);
                return;
            }

            var reordered = new GraphicsDeviceType[current.Length];
            reordered[0] = GraphicsDeviceType.Vulkan;
            int write = 1;
            for (int i = 0; i < current.Length; i++) if (current[i] != GraphicsDeviceType.Vulkan) reordered[write++] = current[i];
            if (write != current.Length) throw new InvalidOperationException("Android graphics API list could not be reordered.");
            PlayerSettings.SetGraphicsAPIs(target, reordered);
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

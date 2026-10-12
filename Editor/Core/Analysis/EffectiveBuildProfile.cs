using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Resolved representation of a Unity 6 Build Profile. BuildProfile intentionally exposes only
    /// a small public surface, so platform/subtarget metadata is read from its serialized data while
    /// profile-local settings that Unity does not expose as Object owners remain null rather than
    /// pretending that the global settings object belongs to the profile.
    /// </summary>
    public sealed class EffectiveBuildProfile
    {
        public BuildProfile Profile { get; }
        public BuildTarget BuildTarget { get; }
        public int Subtarget { get; }
        public string PlatformId { get; }
        public bool IsDedicatedServer { get; }
        public UnityEngine.Object PlayerSettingsOwner { get; }
        public UnityEngine.Object GraphicsSettingsOwner { get; }
        public UnityEngine.Object QualitySettingsOwner { get; }
        public string AssetImportOverridesOwner { get; }
        public string AssetImportOverrideMaxTextureSize { get; }
        public string AssetImportOverrideTextureCompression { get; }
        public string AssetPath { get; }

        internal EffectiveBuildProfile(BuildProfile profile, string assetPath)
        {
            Profile = profile;
            AssetPath = assetPath ?? string.Empty;
            var serialized = profile != null ? new SerializedObject(profile) : null;
            BuildTarget = ReadBuildTarget(serialized, EditorUserBuildSettings.activeBuildTarget);
            Subtarget = ReadInt(serialized, "m_Subtarget", 0);
            PlatformId = ReadString(serialized, "m_PlatformId");
            IsDedicatedServer = DetermineDedicatedServer(BuildTarget, Subtarget);

            // Unity serializes profile-local PlayerSettings/other platform settings inside the
            // BuildProfile asset. They are not publicly exposed as UnityEngine.Object owners.
            // Returning null is deliberate: callers then fall back to the actual global owner and
            // never accidentally edit global settings while claiming they belong to a profile.
            PlayerSettingsOwner = null;
            GraphicsSettingsOwner = null;
            QualitySettingsOwner = null;

            AssetImportOverridesOwner = ReadString(serialized, "m_AssetImportOverridesOwner");
            AssetImportOverrideMaxTextureSize = ReadString(serialized, "m_AssetImportOverrideMaxTextureSize");
            AssetImportOverrideTextureCompression = ReadString(serialized, "m_AssetImportOverrideTextureCompression");
        }

        static BuildTarget ReadBuildTarget(SerializedObject serialized, BuildTarget fallback)
        {
            if (serialized == null) return fallback;
            var property = serialized.FindProperty("m_BuildTarget");
            if (property == null) return fallback;
            try
            {
                int value = property.intValue;
                return Enum.IsDefined(typeof(BuildTarget), value) ? (BuildTarget)value : fallback;
            }
            catch { return fallback; }
        }

        static int ReadInt(SerializedObject serialized, string name, int fallback)
        {
            if (serialized == null) return fallback;
            try
            {
                var property = serialized.FindProperty(name);
                if (property == null) return fallback;
                return property.propertyType == SerializedPropertyType.Integer ? property.intValue : fallback;
            }
            catch { return fallback; }
        }

        static string ReadString(SerializedObject serialized, string name)
        {
            if (serialized == null) return null;
            try
            {
                var property = serialized.FindProperty(name);
                if (property == null) return null;
                switch (property.propertyType)
                {
                    case SerializedPropertyType.String:
                        return property.stringValue;
                    case SerializedPropertyType.Hash128:
                        return property.hash128Value.ToString();
                    default:
                        return property.ToString();
                }
            }
            catch { return null; }
        }

        static bool DetermineDedicatedServer(BuildTarget target, int subtarget)
        {
            if (target.ToString().IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try
            {
                if ((target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 ||
                     target == BuildTarget.StandaloneOSX || target == BuildTarget.StandaloneLinux64) &&
                    subtarget == (int)StandaloneBuildSubtarget.Server)
                    return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>All saved Build Profiles plus the currently active profile, resolved once per scan.</summary>
    public sealed class EffectiveBuildProfileSet
    {
        public List<EffectiveBuildProfile> Profiles { get; } = new List<EffectiveBuildProfile>();
        public EffectiveBuildProfile Active { get; private set; }

        public static EffectiveBuildProfileSet Resolve()
        {
            var result = new EffectiveBuildProfileSet();
            var active = BuildProfile.GetActiveBuildProfile();
            var seen = new HashSet<int>();

            string[] guids = AssetDatabase.FindAssets("t:BuildProfile");
            if (guids != null)
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (string.IsNullOrEmpty(path)) continue;
                    var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
                    Add(result, profile, path, active, seen);
                }
            }

            // The active profile can exist in edge cases where the asset importer/index has not
            // returned it yet. Include it explicitly so the scan never silently loses the active build.
            if (active != null)
            {
                string path = AssetDatabase.GetAssetPath(active);
                Add(result, active, path, active, seen);
            }

            result.Profiles.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.AssetPath, b.AssetPath));
            return result;
        }

        static void Add(EffectiveBuildProfileSet set, BuildProfile profile, string path, BuildProfile active, HashSet<int> seen)
        {
            if (profile == null) return;
            int id = profile.GetInstanceID();
            if (!seen.Add(id)) return;
            var effective = new EffectiveBuildProfile(profile, path ?? string.Empty);
            set.Profiles.Add(effective);
            if (ReferenceEquals(profile, active) || id == (active != null ? active.GetInstanceID() : int.MinValue)) set.Active = effective;
        }
    }
}

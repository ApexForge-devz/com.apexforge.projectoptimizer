using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Effective Build Profile configuration and profile-local optimization analysis.</summary>
	public sealed class BuildProfileAnalyzer : IProjectAnalyzer
	{
		public string DisplayName => "Build Profiles";
		public float Progress => 1f;

		static BuildProfileAnalyzer()
		{
			Rules.Register(id: "PRJ071", title: "Build Profile shader stripping is configured conservatively", category: AssetCategory.ProjectSetting,
							why: "Build Profiles can carry their own shader-stripping configuration. Conservative Keep All or Custom settings can increase shader variants and build data, but changing them can break profile-specific or runtime-loaded shader usage.",
							fix: "Review the exact Build Profile shader-stripping settings and select a supported stripping mode that matches the profile's shader/runtime requirements.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);
		}
		public bool Enabled(OptimizerSettings settings) => true;

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			var resolved = EffectiveBuildProfileSet.Resolve();
			ctx.BuildProfiles = resolved;
			for (int i = 0; i < resolved.Profiles.Count; i++)
			{
				var p = resolved.Profiles[i];
				var entry = new AssetEntry
				{
					Path = p.AssetPath,
					Name = p.Profile != null ? p.Profile.name : System.IO.Path.GetFileNameWithoutExtension(p.AssetPath),
						Directory = "Assets", Extension = ".asset", Category = AssetCategory.ProjectSetting,
					TypeLabel = "Build Profile", FileSize = FileUtility.FileSizeOf(p.AssetPath)
				};
				entry.Set("buildTarget", p.BuildTarget.ToString());
				entry.Set("subtarget", p.Subtarget.ToString());
				entry.Set("platformId", p.PlatformId ?? string.Empty);
				entry.Set("active", resolved.Active == p ? "Yes" : "No");
				entry.Set("playerSettingsOwner", p.PlayerSettingsOwner != null ? AssetDatabase.GetAssetPath(p.PlayerSettingsOwner) : "Not profile-owned");
				entry.Set("graphicsSettingsOwner", p.GraphicsSettingsOwner != null ? AssetDatabase.GetAssetPath(p.GraphicsSettingsOwner) : "Not profile-owned");
				entry.Set("qualitySettingsOwner", p.QualitySettingsOwner != null ? AssetDatabase.GetAssetPath(p.QualitySettingsOwner) : "Not profile-owned");
				entry.Set("assetImportOverridesOwner", p.AssetImportOverridesOwner ?? "Unknown");
				entry.Set("assetImportOverrideMaxTextureSize", p.AssetImportOverrideMaxTextureSize ?? "No Override");
				entry.Set("assetImportOverrideTextureCompression", p.AssetImportOverrideTextureCompression ?? "No Override");
				entry.Set("dedicatedServer", p.IsDedicatedServer ? "Yes" : "No");
				AnalyzeShaderStripping(p, entry, p.AssetPath);
				entry.Finalise();
				yield return entry;
			}
		}
		static void AnalyzeShaderStripping(EffectiveBuildProfile profile, AssetEntry entry, string profilePath)
		{
			if (profile == null || profile.GraphicsSettingsOwner == null) return;
			try
			{
				var so = new SerializedObject(profile.GraphicsSettingsOwner);
				var fields = new[] { "m_LightmapStripping", "m_FogStripping", "m_InstancingStripping", "m_BrgStripping" };
				for (int i = 0; i < fields.Length; i++)
				{
					var prop = FindProperty(so, fields[i]);
					if (prop == null) continue;
					string current = ValueName(prop);
					entry.Set(fields[i], current);
					if (!IsConservative(current) || prop.propertyType != SerializedPropertyType.Enum) continue;
					string ownerPath = AssetDatabase.GetAssetPath(profile.GraphicsSettingsOwner);
					string profileGuid = SafeAssetGuid(profilePath);
					string ownerGuid = SafeAssetGuid(ownerPath);
					if (!IsBackupSupportedAssetPath(profilePath) || !IsBackupSupportedAssetPath(ownerPath)
						|| string.IsNullOrEmpty(profileGuid) || string.IsNullOrEmpty(ownerGuid))
					{
						entry.AddIssue("PRJ071", fields[i] + " = " + current, "Review shader-stripping settings manually", 0, null,
							"No automated option is offered because the Build Profile or graphics settings owner lacks a stable, backup-supported Assets path.");
						continue;
					}
					string propertyPath = prop.propertyPath;
					UnityEngine.Object owner = profile.GraphicsSettingsOwner;
					var options = new List<FixOption>();
					for (int n = 0; n < prop.enumDisplayNames.Length; n++)
					{
						int index = n;
						if (index == prop.enumValueIndex) continue;
						string label = prop.enumDisplayNames[n];
						options.Add(new FixOption
						{
							ActionId = "shader_stripping_" + fields[i] + "_" + index,
							Label = fields[i] + " → " + label,
							// Do not recommend a mode by its label alone; no build/variant baseline is available here.
							Recommended = false,
							Risk = FixRisk.High,
							BackupRequiredOverride = true,
							Warning = "Changing profile-local shader stripping can remove variants needed by runtime-loaded content or profile-specific rendering features.",
							Apply = c => SetEnum(owner, propertyPath, index, c, profilePath, profileGuid, ownerPath, ownerGuid),
							Verify = _ => VerifyEnum(owner, propertyPath, index, profilePath, profileGuid, ownerPath, ownerGuid)
						});
					}
					entry.AddIssue("PRJ071", fields[i] + " = " + current, "Select the appropriate stripping mode", 0, null,
						"Profile-local shader stripping can reduce build variants, but runtime AssetBundles/Addressables, runtime keyword changes and profile-specific rendering can require variants to be retained.",
						options: options);
				}
			}
			catch (Exception e)
			{
				entry.Set("shaderStrippingAnalysisError", e.Message);
			}
		}

		static bool IsConservative(string value) => value.IndexOf("Keep All", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0;

		static string ValueName(SerializedProperty prop)
		{
			if (prop.propertyType == SerializedPropertyType.Enum && prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length) return prop.enumDisplayNames[prop.enumValueIndex];
			return prop.propertyType.ToString();
		}

		static SerializedProperty FindProperty(SerializedObject so, string name)
		{
			var direct = so.FindProperty(name);
			if (direct != null) return direct;
			var it = so.GetIterator();
			bool enter = true;
			while (it.NextVisible(enter))
			{
				enter = false;
				if (string.Equals(it.name, name, StringComparison.OrdinalIgnoreCase) || it.propertyPath.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) return it.Copy();
			}
			return null;
		}

		static void SetEnum(UnityEngine.Object owner, string path, int index, FixContext context,
			string expectedProfilePath, string expectedProfileGuid, string expectedOwnerPath, string expectedOwnerGuid)
		{
			if (owner == null) throw new InvalidOperationException("Build Profile graphics settings owner is missing.");
			if (context == null) throw new InvalidOperationException("Fix context is missing; shader stripping was not changed.");
			if (!string.Equals(context.AssetPath, expectedProfilePath, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(SafeAssetGuid(expectedProfilePath), expectedProfileGuid, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(AssetDatabase.GetAssetPath(owner), expectedOwnerPath, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(SafeAssetGuid(expectedOwnerPath), expectedOwnerGuid, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Build Profile or graphics settings owner identity changed after analysis. Rescan before applying.");
			// Back up the affected Build Profile as well as using Unity Undo for the actual settings owner.
			context.RequireBackup(expectedProfilePath);
			context.RequireBackup(expectedOwnerPath);
			var so = new SerializedObject(owner);
			var prop = so.FindProperty(path);
			if (prop == null || prop.propertyType != SerializedPropertyType.Enum || index < 0 || index >= prop.enumDisplayNames.Length)
				throw new InvalidOperationException("Build Profile shader stripping property '" + path + "' could not be resolved.");
			if (prop.enumValueIndex == index) throw new InvalidOperationException("The selected shader-stripping value is already active.");
			Undo.RecordObject(owner, "Change Build Profile shader stripping");
			prop.enumValueIndex = index;
			if (!so.ApplyModifiedProperties())
				throw new InvalidOperationException("Unity did not accept the Build Profile shader stripping change.");
			EditorUtility.SetDirty(owner);
			context.NeedsSave = true;
		}

		static bool VerifyEnum(UnityEngine.Object owner, string path, int index,
			string expectedProfilePath, string expectedProfileGuid, string expectedOwnerPath, string expectedOwnerGuid)
		{
			try
			{
				if (owner == null || !string.Equals(SafeAssetGuid(expectedProfilePath), expectedProfileGuid, StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(AssetDatabase.GetAssetPath(owner), expectedOwnerPath, StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(SafeAssetGuid(expectedOwnerPath), expectedOwnerGuid, StringComparison.OrdinalIgnoreCase)) return false;
				var prop = new SerializedObject(owner).FindProperty(path);
				return prop != null && prop.propertyType == SerializedPropertyType.Enum && prop.enumValueIndex == index;
			}
			catch { return false; }
		}

		static bool IsBackupSupportedAssetPath(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);

		static string SafeAssetGuid(string path)
		{
			try { return string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path); }
			catch { return string.Empty; }
		}
	}
}

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Changes mipmap streaming on the exact serialized Quality Level entries that produced a finding.
	/// QualitySettings.streamingMipmapsActive reads and writes the editor's current level, so it is not
	/// safe for a deferred fix action unless the level context is explicitly switched and restored.
	/// This helper instead validates and edits the target entries directly.
	/// </summary>
	internal static class MipmapStreamingFixUtility
	{
		internal static void EnableForTargetLevels(OptimizerPlatform targetPlatform, int[] expectedIndices, string[] expectedNames)
		{
			ValidateArguments(expectedIndices, expectedNames);

			var selectedSettings = OptimizerSettings.Instance;
			if (selectedSettings == null || selectedSettings.targetPlatform != targetPlatform)
				throw new InvalidOperationException("The selected ProjectOptimizer target is unavailable or changed after this finding was produced. Rescan before applying the mipmap-streaming fix.");

			var active = QualitySettings.GetActiveQualityLevelsForPlatform(PlatformUtility.ToNamedBuildTarget(targetPlatform).TargetName);
			var activeIndices = new HashSet<int>(active ?? Array.Empty<int>());
			var qualityObject = QualitySettings.GetQualitySettings();
			var serialized = new SerializedObject(qualityObject);
			var array = serialized.FindProperty("m_QualitySettings");
			if (array == null)
				throw new InvalidOperationException("Unity's Quality Settings serialized quality-level array is unavailable; no mipmap settings were changed.");

			// Validate all targets before mutating any of them. A stale finding must never affect a
			// different Quality Level that happens to occupy the same index after a settings edit.
			for (int i = 0; i < expectedIndices.Length; i++)
			{
				int index = expectedIndices[i];
				if (!activeIndices.Contains(index) || index < 0 || index >= array.arraySize)
					throw new InvalidOperationException("Quality Level index " + index + " is no longer active for target " + targetPlatform + ". Rescan before applying the fix.");

				var level = array.GetArrayElementAtIndex(index);
				string currentName = SerializedPropertyUtility.ReadRelativeString(level, "name", "Quality " + index);
				if (!string.Equals(currentName, expectedNames[i], StringComparison.Ordinal))
					throw new InvalidOperationException("Quality Level " + index + " changed from '" + expectedNames[i] + "' to '" + currentName + "'. Rescan before applying the fix.");
				if (level.FindPropertyRelative("streamingMipmapsActive") == null)
					throw new InvalidOperationException("Unity's serialized mipmap-streaming field is unavailable on Quality Level '" + currentName + "'.");
			}

			for (int i = 0; i < expectedIndices.Length; i++)
				array.GetArrayElementAtIndex(expectedIndices[i]).FindPropertyRelative("streamingMipmapsActive").boolValue = true;

			serialized.ApplyModifiedProperties();
			EditorUtility.SetDirty(qualityObject);
			if (!AreTargetLevelsEnabled(targetPlatform, expectedIndices, expectedNames))
				throw new InvalidOperationException("Mipmap Streaming did not verify On for every affected Quality Level.");
		}

		internal static bool AreTargetLevelsEnabled(OptimizerPlatform targetPlatform, int[] expectedIndices, string[] expectedNames)
		{
			if (!ArgumentsAreValid(expectedIndices, expectedNames)) return false;
			try
			{
				var selectedSettings = OptimizerSettings.Instance;
				if (selectedSettings == null || selectedSettings.targetPlatform != targetPlatform) return false;

				var active = QualitySettings.GetActiveQualityLevelsForPlatform(PlatformUtility.ToNamedBuildTarget(targetPlatform).TargetName);
				var activeIndices = new HashSet<int>(active ?? Array.Empty<int>());
				var serialized = new SerializedObject(QualitySettings.GetQualitySettings());
				var array = serialized.FindProperty("m_QualitySettings");
				if (array == null) return false;

				for (int i = 0; i < expectedIndices.Length; i++)
				{
					int index = expectedIndices[i];
					if (!activeIndices.Contains(index) || index < 0 || index >= array.arraySize) return false;
					var level = array.GetArrayElementAtIndex(index);
					string currentName = SerializedPropertyUtility.ReadRelativeString(level, "name", "Quality " + index);
					var streaming = level.FindPropertyRelative("streamingMipmapsActive");
					if (!string.Equals(currentName, expectedNames[i], StringComparison.Ordinal) || streaming == null || !streaming.boolValue) return false;
				}
				return true;
			}
			catch { return false; }
		}

		static void ValidateArguments(int[] expectedIndices, string[] expectedNames)
		{
			if (!ArgumentsAreValid(expectedIndices, expectedNames))
				throw new InvalidOperationException("Mipmap streaming fix requires matching, non-empty Quality Level indices and names.");
		}

		static bool ArgumentsAreValid(int[] expectedIndices, string[] expectedNames)
		{
			if (expectedIndices == null || expectedNames == null || expectedIndices.Length == 0 || expectedIndices.Length != expectedNames.Length)
				return false;
			for (int i = 0; i < expectedIndices.Length; i++)
				if (expectedIndices[i] < 0 || string.IsNullOrEmpty(expectedNames[i])) return false;
			return true;
		}
	}
}

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
namespace ApexForge.ProjectOptimizer
{
	public static class UnityEditorUtility
	{
		public static long RuntimeSizeOf(UnityEngine.Object obj){if(obj==null)return -1;try{return Profiler.GetRuntimeMemorySizeLong(obj);}catch{return -1;}}
		public static bool PingPath(string assetPath){var obj=AssetDatabase.LoadMainAssetAtPath(assetPath);if(obj==null)return false;Selection.activeObject=obj;EditorUtility.FocusProjectWindow();EditorGUIUtility.PingObject(obj);return true;}

		/// <summary>Checks VCS editability for imported assets and file-level writeability for project settings.</summary>
		public static bool CanEditPath(string assetPath, out string reason)
		{
			reason = null;
			if (string.IsNullOrEmpty(assetPath)) { reason = "The target path is empty."; return false; }
			string normalized = assetPath.Replace('\\', '/');
			try
			{
				string absolute = AssetPathUtility.ToAbsolutePath(normalized);
				if (File.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.ReadOnly) != 0) { reason = "The target file is read-only."; return false; }
				if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
				{
					if (!AssetDatabase.CanOpenForEdit(normalized, out var vcsReason, StatusQueryOptions.ForceUpdate)) { reason = string.IsNullOrEmpty(vcsReason) ? "The active version-control provider reports the asset is not editable." : vcsReason; return false; }
					string meta = normalized + ".meta";
					if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && File.Exists(AssetPathUtility.ToAbsolutePath(meta)) && !AssetDatabase.CanOpenForEdit(meta, out var metaReason, StatusQueryOptions.ForceUpdate)) { reason = string.IsNullOrEmpty(metaReason) ? "The asset .meta file is not editable." : metaReason; return false; }
				}
				else if (!Directory.Exists(Path.GetDirectoryName(absolute) ?? string.Empty)) { reason = "The target directory does not exist."; return false; }
				return true;
			}
			catch (Exception e) { reason = "Editability could not be established safely: " + e.Message; return false; }
		}
	}
}

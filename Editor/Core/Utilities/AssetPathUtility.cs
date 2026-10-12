using System;
using System.IO;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	public static class AssetPathUtility
	{
		public static string ToAbsolutePath(string assetPath)
		{ if (string.IsNullOrEmpty(assetPath)) return string.Empty; return Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, assetPath); }
		public static bool IsUnder(string assetPath, string folder)
		{
			if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folder)) return false;
			folder = folder.Replace('\\','/').TrimEnd('/'); if (folder.Length == 0 || !assetPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
			return assetPath.Length == folder.Length || assetPath[folder.Length] == '/';
		}
		public static string Directoryish(string assetPath) { if (string.IsNullOrEmpty(assetPath)) return string.Empty; int idx = assetPath.LastIndexOf('/'); return idx <= 0 ? assetPath : assetPath.Substring(0, idx); }
		public static bool IsInResources(string assetPath) => !string.IsNullOrEmpty(assetPath) && assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0;
		public static bool IsInStreamingAssets(string assetPath) => !string.IsNullOrEmpty(assetPath) && (assetPath.IndexOf("/StreamingAssets/", StringComparison.OrdinalIgnoreCase) >= 0 || assetPath.EndsWith("/StreamingAssets", StringComparison.OrdinalIgnoreCase));
		public static bool IsInEditorFolder(string assetPath) => !string.IsNullOrEmpty(assetPath) && (assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0 || assetPath.StartsWith("Assets/Editor/", StringComparison.OrdinalIgnoreCase));
		public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
		public static int NextPowerOfTwoDown(int value) { if (value <= 0) return 0; int p=1; while(p <= value/2) p*=2; return p; }
	}
}

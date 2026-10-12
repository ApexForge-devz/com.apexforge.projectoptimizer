using System;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Centralizes Unity AssetDatabase loads so expensive native materialization is measured and kept consistent.</summary>
	public static class AssetLoadUtility
	{
		public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			return Measure("AssetDatabase/LoadAssetAtPath", assetPath, () => AssetDatabase.LoadAssetAtPath<T>(assetPath));
		}

		public static UnityEngine.Object LoadMainAssetAtPath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			return Measure("AssetDatabase/LoadMainAssetAtPath", assetPath, () => AssetDatabase.LoadMainAssetAtPath(assetPath));
		}

		public static UnityEngine.Object[] LoadAllAssetsAtPath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return Array.Empty<UnityEngine.Object>();
			return Measure("AssetDatabase/LoadAllAssetsAtPath", assetPath, () => AssetDatabase.LoadAllAssetsAtPath(assetPath));
		}

		public static UnityEngine.Object[] LoadAllAssetRepresentationsAtPath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return Array.Empty<UnityEngine.Object>();
			return Measure("AssetDatabase/LoadAllAssetRepresentationsAtPath", assetPath, () => AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath));
		}

		public static bool IsMainAssetAtPathLoaded(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return false;
			double start = ScanTiming.Now;
			bool loaded = AssetDatabase.IsMainAssetAtPathLoaded(assetPath);
			ScanTiming.Active?.AddDiagnosticTiming("AssetDatabase/IsMainAssetAtPathLoaded", ScanTiming.Now - start);
			return loaded;
		}

		public static AssetDatabaseLoadOperation LoadObjectAsync(string assetPath, long localId)
		{
			if (string.IsNullOrEmpty(assetPath) || localId == 0) return null;
			double start = ScanTiming.Now;
			try
			{
				var operation = AssetDatabase.LoadObjectAsync(assetPath, localId);
				ScanTiming.Active?.AddDiagnosticTiming("AssetDatabase/LoadObjectAsync/start", ScanTiming.Now - start);
				return operation;
			}
			catch
			{
				ScanTiming.Active?.AddDiagnosticTiming("AssetDatabase/LoadObjectAsync/failure", ScanTiming.Now - start);
				return null;
			}
		}

		public static bool TryGetLocalFileIdentifier(int instanceId, out long localId)
		{
			localId = 0;
			if (instanceId == 0) return false;
			// Unity 6 still exposes this overload for instance IDs; the replacement takes EntityId.
#pragma warning disable CS0618
			return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instanceId, out _, out localId);
#pragma warning restore CS0618
		}

		static T Measure<T>(string operation, string assetPath, Func<T> action)
		{
			double start = ScanTiming.Now;
			try
			{
				var result = action();
				ScanTiming.Active?.AddDiagnosticTiming(operation, ScanTiming.Now - start);
				return result;
			}
			catch
			{
				ScanTiming.Active?.AddDiagnosticTiming(operation + "/failure", ScanTiming.Now - start);
				throw;
			}
		}
	}
}

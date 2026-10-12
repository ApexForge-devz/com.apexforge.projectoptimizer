using System;
using System.IO;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	public static class CacheFileUtility
	{
		public const string CacheFolder = ProjectOptimizerConstants.CacheFolder;
		public static bool EnsureCacheFolder()
		{
			try { Directory.CreateDirectory(CacheFolder); return true; }
			catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not create " + CacheFolder + ": " + e.Message); return false; }
		}
		public static void ReplaceFile(string temporaryPath, string destinationPath)
		{
			if (File.Exists(destinationPath))
			{
				try { File.Replace(temporaryPath, destinationPath, null); return; }
				catch (IOException) { File.Delete(destinationPath); }
			}
			File.Move(temporaryPath, destinationPath);
		}
	}
}

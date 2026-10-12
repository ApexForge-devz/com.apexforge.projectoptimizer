using System;
using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	public static class FileUtility
	{
		public static long FileSizeOf(string assetPath)
		{
			try { var full = AssetPathUtility.ToAbsolutePath(assetPath); return File.Exists(full) ? new FileInfo(full).Length : 0L; } catch { return 0L; }
		}
		public static string Md5OfAbsolute(string fullPath)
		{
			try
			{
				if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;
				using (var stream = File.OpenRead(fullPath)) using (var md5 = MD5.Create())
				{ var hash = md5.ComputeHash(stream); return BitConverter.ToString(hash).Replace("-", string.Empty); }
			}
			catch { return null; }
		}
		public static string Md5Of(string assetPath) => Md5OfAbsolute(AssetPathUtility.ToAbsolutePath(assetPath));
		public static string QuickFileSignatureOfAbsolute(string fullPath)
		{
			try
			{
				if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;
				using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
				{
					const int sampleSize = 8192; ulong hash = 1469598103934665603UL; byte[] buffer = new byte[sampleSize];
					int first = stream.Read(buffer, 0, buffer.Length); hash = UpdateQuickSignature(hash, buffer, first);
					if (stream.Length > sampleSize) { stream.Position = Math.Max(0L, stream.Length - sampleSize); int tail = stream.Read(buffer, 0, buffer.Length); hash = UpdateQuickSignature(hash, buffer, tail); }
					return hash.ToString("X16", CultureInfo.InvariantCulture);
				}
			}
			catch { return null; }
		}
		static ulong UpdateQuickSignature(ulong hash, byte[] buffer, int count) { for (int i = 0; i < count; i++) { hash ^= buffer[i]; hash *= 1099511628211UL; } return hash; }
	}
}

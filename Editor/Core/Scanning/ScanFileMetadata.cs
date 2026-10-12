using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One scan-wide filesystem metadata table. One dictionary entry carries size and timestamp together.</summary>
	public sealed class ScanFileMetadata
	{
		struct Metadata
		{
			public long Size;
			public long LastWriteTicks;
			public long MetaLastWriteTicks;
			public string Guid;
		}

		readonly Dictionary<string, Metadata> m_Metadata;
		readonly bool m_ThreadSafeSnapshot;

		public ScanFileMetadata() : this(new Dictionary<string, Metadata>(StringComparer.Ordinal), false) { }

		ScanFileMetadata(Dictionary<string, Metadata> metadata, bool threadSafeSnapshot) { m_Metadata = metadata; m_ThreadSafeSnapshot = threadSafeSnapshot; }

		public static ScanFileMetadata FromThreadSafeSnapshot() => new ScanFileMetadata(new Dictionary<string, Metadata>(StringComparer.Ordinal), true);

		public void SetFileSize(string assetPath, long size)
		{
			if (string.IsNullOrEmpty(assetPath)) return;
			m_Metadata.TryGetValue(assetPath, out var value);
			value.Size = size;
			m_Metadata[assetPath] = value;
		}

		public long GetFileSize(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return 0L;
			if (m_ThreadSafeSnapshot && AssetDatabaseThreadSafeUtility.TryGetAssetMetadata(assetPath, out var indexed)) return indexed.FileSize;
			return m_Metadata.TryGetValue(assetPath, out var value) ? value.Size : FileUtility.FileSizeOf(assetPath);
		}

		public string GetGuid(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return string.Empty;
			if (m_ThreadSafeSnapshot && AssetDatabaseThreadSafeUtility.TryGetAssetMetadata(assetPath, out var indexed) && !string.IsNullOrEmpty(indexed.Guid)) return indexed.Guid;
			if (m_Metadata.TryGetValue(assetPath, out var value) && !string.IsNullOrEmpty(value.Guid)) return value.Guid;
			return string.Empty;
		}

		public long GetLastWriteTicks(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return 0L;
			if (m_ThreadSafeSnapshot && AssetDatabaseThreadSafeUtility.TryGetAssetMetadata(assetPath, out var indexed) && indexed.LastWriteTicks != 0L) return indexed.LastWriteTicks;
			if (m_Metadata.TryGetValue(assetPath, out var value) && value.LastWriteTicks != 0L) return value.LastWriteTicks;
			try
			{
				string full = AssetPathUtility.ToAbsolutePath(assetPath);
				return File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0L;
			}
			catch { return 0L; }
		}

		public long GetMetaLastWriteTicks(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return 0L;
			if (m_ThreadSafeSnapshot && AssetDatabaseThreadSafeUtility.TryGetAssetMetadata(assetPath, out var indexed) && indexed.MetaLastWriteTicks != 0L) return indexed.MetaLastWriteTicks;
			if (m_Metadata.TryGetValue(assetPath, out var value) && value.MetaLastWriteTicks != 0L) return value.MetaLastWriteTicks;
			try
			{
				string full = AssetPathUtility.ToAbsolutePath(assetPath) + ".meta";
				return File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0L;
			}
			catch { return 0L; }
		}

		/// <summary>
		/// Computes metadata on workers. When the thread-safe project index is ready, GUIDs, file size and
		/// timestamps are reused from that immutable filesystem snapshot. Paths that are not represented by
		/// the snapshot (for example package-manager locations outside the physical project tree) fall back
		/// to direct filesystem I/O, preserving the old coverage rather than silently dropping them.
		/// </summary>
		public static ScanFileMetadata Build(IReadOnlyList<string> paths, string projectRoot, int workerCount, CancellationToken cancellationToken = default, bool preferThreadSafeIndex = true)
		{
			int count = Math.Max(0, paths?.Count ?? 0);
			if (count == 0) return new ScanFileMetadata(new Dictionary<string, Metadata>(StringComparer.Ordinal), false);

			var sizes = new long[count];
			var stamps = new long[count];
			var guids = new string[count];
			var metaStamps = new long[count];
			bool useThreadSafeIndex = preferThreadSafeIndex && AssetDatabaseThreadSafeUtility.IsReady;
			var fallbackIndices = new List<int>();

			if (useThreadSafeIndex)
			{
				for (int i = 0; i < count; i++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (AssetDatabaseThreadSafeUtility.TryGetAssetMetadata(paths[i], out var indexed))
					{
						sizes[i] = indexed.FileSize;
						stamps[i] = indexed.LastWriteTicks;
						metaStamps[i] = indexed.MetaLastWriteTicks;
						guids[i] = indexed.Guid ?? string.Empty;
					}
					else fallbackIndices.Add(i);
				}
			}
			else
			{
				for (int i = 0; i < count; i++) fallbackIndices.Add(i);
			}

			if (fallbackIndices.Count > 0)
			{
				var options = new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Max(1, Math.Min(32, workerCount)),
					CancellationToken = cancellationToken
				};
				Parallel.For(0, fallbackIndices.Count, options, n =>
				{
					int i = fallbackIndices[n];
					try
					{
						string full = Path.Combine(projectRoot ?? string.Empty, paths[i]);
						var info = new FileInfo(full);
						if (info.Exists)
						{
							sizes[i] = info.Length;
							stamps[i] = info.LastWriteTimeUtc.Ticks;
						}
						string metaPath = full + ".meta";
						if (File.Exists(metaPath))
						{
							metaStamps[i] = File.GetLastWriteTimeUtc(metaPath).Ticks;
							guids[i] = TryReadGuid(metaPath);
						}
					}
					catch
					{
						sizes[i] = 0L;
						stamps[i] = 0L;
						guids[i] = string.Empty;
					}
				});
			}

			var metadata = new Dictionary<string, Metadata>(count, StringComparer.Ordinal);
			for (int i = 0; i < count; i++)
				metadata[paths[i]] = new Metadata { Size = sizes[i], LastWriteTicks = stamps[i], MetaLastWriteTicks = metaStamps[i], Guid = guids[i] ?? string.Empty };
			return new ScanFileMetadata(metadata, false);
		}

		static string TryReadGuid(string metaPath)
		{
			try
			{
				using (var reader = new StreamReader(metaPath))
				{
					for (int i = 0; i < 12; i++)
					{
						var line = reader.ReadLine();
						if (line == null) break;
						const string prefix = "guid: ";
						if (line.StartsWith(prefix, StringComparison.Ordinal)) return line.Substring(prefix.Length).Trim();
					}
				}
			}
			catch { }
			return string.Empty;
		}
	}
}

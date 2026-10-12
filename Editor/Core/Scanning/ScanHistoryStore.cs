using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One completed scan's headline numbers, kept for trend comparisons.</summary>
	[Serializable]
	public sealed class ScanHistoryEntry
	{
		public long TimestampTicks;
		public int ScannedAssets;
		public int CriticalCount;
		public int WarningCount;
		public int InfoCount;
		public long PotentialMemorySavings;
		public long PotentialDiskSavings;

		public int IssueCount => CriticalCount + WarningCount + InfoCount;
	}

	[Serializable]
	sealed class ScanHistoryLog
	{
		public int Version;
		public List<ScanHistoryEntry> Entries = new List<ScanHistoryEntry>();
	}

	/// <summary>
	/// A small rolling log of completed scans - just their headline counters, not the findings
	/// themselves - so the Overview page can say "3 fewer critical since last time" instead of every
	/// scan starting from a blank slate. Lives beside the other Library caches; unlike
	/// <see cref="ScanResultStore"/> this one is deliberately never invalidated by a tool-version bump,
	/// since a bare list of numbers has nothing in it that a schema change could misinterpret.
	/// <para>
	/// Capped at <see cref="k_MaxEntries"/>: this is a trend indicator, not an audit trail, and an
	/// ever-growing file would be the one cache in this tool with no natural ceiling.
	/// </para>
	/// </summary>
	public static class ScanHistoryStore
	{
		const int k_Version = 1;
		const int k_MaxEntries = 30;
		static readonly string k_Path = ProjectOptimizerConstants.ScanHistoryPath;

		/// <summary>Appends one entry for a completed scan. A no-op for an aborted or cancelled one.</summary>
		public static void Record(ScanResult result)
		{
			if (result == null || result.Aborted) return;

			var log = Load() ?? new ScanHistoryLog { Version = k_Version };
			log.Entries.Add(new ScanHistoryEntry
			{
				TimestampTicks = result.ScanTime.Ticks,
				ScannedAssets = result.ScannedAssets,
				CriticalCount = result.CriticalCount,
				WarningCount = result.WarningCount,
				InfoCount = result.InfoCount,
				PotentialMemorySavings = result.PotentialMemorySavings,
				PotentialDiskSavings = result.PotentialDiskSavings
			});

			while (log.Entries.Count > k_MaxEntries)
				log.Entries.RemoveAt(0);

			Save(log);
		}

		/// <summary>Oldest first. Empty, never null, when there is no history yet or it could not be read.</summary>
		public static List<ScanHistoryEntry> LoadEntries()
		{
			return Load()?.Entries ?? new List<ScanHistoryEntry>();
		}

		static ScanHistoryLog Load()
		{
			try
			{
				if (!File.Exists(k_Path)) return null;
				var log = JsonUtility.FromJson<ScanHistoryLog>(File.ReadAllText(k_Path));
				return log != null && log.Version == k_Version ? log : null;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read scan history, discarding it: " + e.Message);
				Clear();
				return null;
			}
		}

		static void Save(ScanHistoryLog log)
		{
			try
			{
				if (!CacheFileUtility.EnsureCacheFolder()) return;

				var json = JsonUtility.ToJson(log);
				var temp = k_Path + ".tmp";
				File.WriteAllText(temp, json);
				CacheFileUtility.ReplaceFile(temp, k_Path);
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not save scan history: " + e.Message);
			}
		}

		public static void Clear()
		{
			try
			{
				if (File.Exists(k_Path)) File.Delete(k_Path);
				if (File.Exists(k_Path + ".tmp")) File.Delete(k_Path + ".tmp");
			}
			catch
			{ /* best effort */
			}
		}
	}
}

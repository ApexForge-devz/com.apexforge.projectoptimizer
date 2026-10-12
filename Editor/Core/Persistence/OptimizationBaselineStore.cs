using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	[Serializable]
	public sealed class OptimizationBaselineRule
	{
		public string RuleId;
		public int Count;
	}

	[Serializable]
	public sealed class OptimizationBaselineCategory
	{
		public string Category;
		public int Count;
	}

	[Serializable]
	public sealed class OptimizationBaseline
	{
		public int Version = 2;
		public string ToolSchema;
		public string UnityVersion;
		public long TimestampTicks;
		public int SettingsRevision;
		public bool Complete;
		public int ScannedAssets;
		public int CriticalCount;
		public int WarningCount;
		public int InfoCount;
		public long PotentialMemorySavings;
		public long PotentialDiskSavings;
		public long TotalFileSize;
		public List<OptimizationBaselineRule> Rules = new List<OptimizationBaselineRule>();
		public List<OptimizationBaselineCategory> Categories = new List<OptimizationBaselineCategory>();
	}

	[Serializable]
	public sealed class OptimizationBaselineRuleDelta
	{
		public string RuleId;
		public int Before;
		public int After;
		public int Delta;
	}

	[Serializable]
	public sealed class OptimizationBaselineCategoryDelta
	{
		public string Category;
		public int Before;
		public int After;
		public int Delta;
	}

	public sealed class OptimizationBaselineComparison
	{
		public bool HasBaseline;
		public bool Compatible;
		public string CompatibilityNote;
		public bool BaselineComplete;
		public int IssueDelta;
		public int CriticalDelta;
		public int WarningDelta;
		public int InfoDelta;
		public long MemorySavingsDelta;
		public long DiskSavingsDelta;
		public long FileSizeDelta;
		public int ScannedAssetsDelta;
		public readonly List<OptimizationBaselineRuleDelta> RuleDeltas = new List<OptimizationBaselineRuleDelta>();
		public readonly List<OptimizationBaselineCategoryDelta> CategoryDeltas = new List<OptimizationBaselineCategoryDelta>();
	}

	/// <summary>
	/// Stores a deliberate optimization baseline separately from passive scan history. A baseline is the
	/// reference point for a before/after experiment; it is never silently replaced by a new scan.
	/// </summary>
	public static class OptimizationBaselineStore
	{
		const int k_Version = 2;
		static readonly string k_Path = ProjectOptimizerConstants.OptimizationBaselinePath;

		public static bool Exists => File.Exists(k_Path);
		public static string Path => k_Path;

		public static OptimizationBaseline Load()
		{
			try
			{
				if (!File.Exists(k_Path)) return null;
				var baseline = JsonUtility.FromJson<OptimizationBaseline>(File.ReadAllText(k_Path));
				return baseline != null && baseline.Version == k_Version ? baseline : null;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read optimization baseline: " + e.Message);
				return null;
			}
		}

		public static OptimizationBaseline Capture(ScanResult result)
		{
			if (result == null || !result.IsComplete) return null;

			var baseline = new OptimizationBaseline
			{
				Version = k_Version,
				ToolSchema = OptimizerVersion.Schema.ToString(CultureInfo.InvariantCulture),
				UnityVersion = Application.unityVersion,
				TimestampTicks = result.ScanTime.Ticks,
				SettingsRevision = result.SettingsRevision,
				Complete = result.IsComplete,
				ScannedAssets = result.ScannedAssets,
				CriticalCount = result.CriticalCount,
				WarningCount = result.WarningCount,
				InfoCount = result.InfoCount,
				PotentialMemorySavings = result.PotentialMemorySavings,
				PotentialDiskSavings = result.PotentialDiskSavings,
				TotalFileSize = result.TotalFileSize
			};

			var ruleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
			var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var entry in result.Entries)
			{
				if (entry == null) continue;
				foreach (var issue in entry.Issues)
				{
					if (issue == null || issue.Stale) continue;
					if (!string.IsNullOrEmpty(issue.RuleId))
					{
						ruleCounts.TryGetValue(issue.RuleId, out int count);
						ruleCounts[issue.RuleId] = count + 1;
					}
					string category = entry.Category.ToString();
					categoryCounts.TryGetValue(category, out int categoryCount);
					categoryCounts[category] = categoryCount + 1;
				}
			}

			foreach (var pair in ruleCounts)
				baseline.Rules.Add(new OptimizationBaselineRule { RuleId = pair.Key, Count = pair.Value });
			foreach (var pair in categoryCounts)
				baseline.Categories.Add(new OptimizationBaselineCategory { Category = pair.Key, Count = pair.Value });
			baseline.Rules.Sort((a, b) => string.CompareOrdinal(a.RuleId, b.RuleId));
			baseline.Categories.Sort((a, b) => string.CompareOrdinal(a.Category, b.Category));
			return baseline;
		}

		public static bool Save(ScanResult result)
		{
			var baseline = Capture(result);
			if (baseline == null) return false;
			return Save(baseline);
		}

		public static bool Save(OptimizationBaseline baseline)
		{
			if (baseline == null || !baseline.Complete) return false;
			try
			{
				if (!CacheFileUtility.EnsureCacheFolder()) return false;
				string temp = k_Path + ".tmp";
				File.WriteAllText(temp, JsonUtility.ToJson(baseline, true));
				CacheFileUtility.ReplaceFile(temp, k_Path);
				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not save optimization baseline: " + e.Message);
				return false;
			}
		}

		public static OptimizationBaselineComparison Compare(ScanResult result)
		{
			var comparison = new OptimizationBaselineComparison();
			var baseline = Load();
			comparison.HasBaseline = baseline != null;
			if (baseline == null || result == null) return comparison;

			comparison.BaselineComplete = baseline.Complete;
			comparison.Compatible = baseline.Complete
				&& string.Equals(baseline.UnityVersion, Application.unityVersion, StringComparison.Ordinal)
				&& string.Equals(baseline.ToolSchema, OptimizerVersion.Schema.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
			comparison.CompatibilityNote = comparison.Compatible
				? "Same Unity/tool schema and complete baseline"
				: "Unity version, tool schema, or baseline completeness changed; treat this comparison as directional only";

			int previousIssues = baseline.CriticalCount + baseline.WarningCount + baseline.InfoCount;
			comparison.IssueDelta = result.IssueCount - previousIssues;
			comparison.CriticalDelta = result.CriticalCount - baseline.CriticalCount;
			comparison.WarningDelta = result.WarningCount - baseline.WarningCount;
			comparison.InfoDelta = result.InfoCount - baseline.InfoCount;
			comparison.MemorySavingsDelta = result.PotentialMemorySavings - baseline.PotentialMemorySavings;
			comparison.DiskSavingsDelta = result.PotentialDiskSavings - baseline.PotentialDiskSavings;
			comparison.FileSizeDelta = result.TotalFileSize - baseline.TotalFileSize;
			comparison.ScannedAssetsDelta = result.ScannedAssets - baseline.ScannedAssets;

			var beforeRules = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var rule in baseline.Rules)
				if (rule != null && !string.IsNullOrEmpty(rule.RuleId)) beforeRules[rule.RuleId] = rule.Count;
			var afterRules = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var entry in result.Entries)
			{
				if (entry == null) continue;
				foreach (var issue in entry.Issues)
				{
					if (issue == null || issue.Stale || string.IsNullOrEmpty(issue.RuleId)) continue;
					afterRules.TryGetValue(issue.RuleId, out int count);
					afterRules[issue.RuleId] = count + 1;
				}
			}
			var ruleIds = new HashSet<string>(beforeRules.Keys, StringComparer.Ordinal);
			foreach (var id in afterRules.Keys) ruleIds.Add(id);
			foreach (var id in ruleIds)
			{
				beforeRules.TryGetValue(id, out int before);
				afterRules.TryGetValue(id, out int after);
				if (before == after) continue;
				comparison.RuleDeltas.Add(new OptimizationBaselineRuleDelta { RuleId = id, Before = before, After = after, Delta = after - before });
			}
			comparison.RuleDeltas.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)) != 0
				? Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta))
				: string.CompareOrdinal(a.RuleId, b.RuleId));

			var beforeCategories = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var category in baseline.Categories)
				if (category != null && !string.IsNullOrEmpty(category.Category)) beforeCategories[category.Category] = category.Count;
			var afterCategories = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var entry in result.Entries)
			{
				if (entry == null) continue;
				foreach (var issue in entry.Issues)
				{
					if (issue == null || issue.Stale) continue;
					string key = entry.Category.ToString();
					afterCategories.TryGetValue(key, out int count);
					afterCategories[key] = count + 1;
				}
			}
			var categoryIds = new HashSet<string>(beforeCategories.Keys, StringComparer.Ordinal);
			foreach (var id in afterCategories.Keys) categoryIds.Add(id);
			foreach (var id in categoryIds)
			{
				beforeCategories.TryGetValue(id, out int before);
				afterCategories.TryGetValue(id, out int after);
				if (before == after) continue;
				comparison.CategoryDeltas.Add(new OptimizationBaselineCategoryDelta { Category = id, Before = before, After = after, Delta = after - before });
			}
			comparison.CategoryDeltas.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)) != 0
				? Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta))
				: string.CompareOrdinal(a.Category, b.Category));
			return comparison;
		}

		public static void Clear()
		{
			try
			{
				if (File.Exists(k_Path)) File.Delete(k_Path);
				if (File.Exists(k_Path + ".tmp")) File.Delete(k_Path + ".tmp");
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not clear optimization baseline: " + e.Message);
			}
		}

		public static string Describe(OptimizationBaseline baseline)
		{
			if (baseline == null) return "No baseline saved.";
			DateTime when;
			try { when = new DateTime(baseline.TimestampTicks); }
			catch { when = DateTime.MinValue; }
			return "Captured " + (when == DateTime.MinValue ? "at an unknown time" : when.ToString("g"))
				+ " on Unity " + (baseline.UnityVersion ?? "unknown") + ".";
		}
	}
}

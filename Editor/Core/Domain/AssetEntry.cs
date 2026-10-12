using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One row in a category table: an asset (or a pseudo-asset such as a project setting) plus its issues.</summary>
	public sealed class AssetEntry
	{
		/// <summary>Stable dense id assigned by the scan asset catalog. Runtime-only; never serialized into persisted scan results.</summary>
		[NonSerialized] public int AssetId = -1;

		/// <summary>Internal payload used by hybrid background analyzers to hand off parsed facts to the Unity main thread.</summary>
		[NonSerialized] public object BackgroundPayload;
		public string Guid = string.Empty;
		public string Path = string.Empty;
		public string Name = string.Empty;
		public string Directory = string.Empty;
		public string Extension = string.Empty;
		public AssetCategory Category;
		public string TypeLabel = string.Empty;

		/// <summary>Deterministic scan position used to make parallel completion order irrelevant.</summary>
		public long ScanOrder = long.MaxValue;

		/// <summary>Bytes on disk (source file).</summary>
		public long FileSize;
		/// <summary>Estimated bytes in memory at runtime. -1 when not applicable.</summary>
		public long RuntimeSize = -1;

		/// <summary>Extra per-category values, addressed by column key.</summary>
		public readonly Dictionary<string, object> Fields = new Dictionary<string, object>();

		public readonly List<OptimizationIssue> Issues = new List<OptimizationIssue>();

		public IssueSeverity WorstSeverity;
		public bool HasIssues => Issues.Count > 0;

		/// <summary>Everything applying every fix on this asset would recover, memory and disk together.</summary>
		public long PotentialSavings;

		/// <summary>The part of <see cref="PotentialSavings"/> that comes out of runtime memory.</summary>
		public long PotentialMemorySavings;

		/// <summary>The part of <see cref="PotentialSavings"/> that comes off the source file.</summary>
		public long PotentialDiskSavings;

		/// <summary>
		/// What the per-issue estimates add up to if you simply sum them. Kept only so the UI can
		/// notice when that sum overstates the real figure and say so.
		/// </summary>
		public long NaiveSavingsSum;

		/// <summary>
		/// Candidate strings the fuzzy search scores independently, so a query can match the asset
		/// name, its path, its type or a rule id/title without one long concatenated blob diluting
		/// the match position bonuses those all depend on. Built once in <see cref="Finalise"/>, not
		/// recomputed per keystroke - see PART 13 / P2.9 of the pass 4 TODO.
		/// </summary>
		public readonly List<string> SearchCandidates = new List<string>();

		Object m_Cached;
		bool m_Loaded;

		public object Field(string key)
		{
			return Fields.TryGetValue(key, out var v) ? v : null;
		}

		public void Set(string key, object value) => Fields[key] = value;

		public Object LoadAsset()
		{
			if (BackgroundRuleExecution.IsBackground)
				throw new InvalidOperationException("AssetEntry.LoadAsset cannot be called from a background analyzer.");
			if (m_Loaded) return m_Cached;
			m_Loaded = true;
			if (!string.IsNullOrEmpty(Path))
				m_Cached = AssetDatabase.LoadMainAssetAtPath(Path);
			return m_Cached;
		}

		/// <summary>
		/// Recomputes rollups after all analyzers have contributed issues.
		/// <para>
		/// Savings are capped against the measured per-asset memory/disk pools. This prevents
		/// overlapping findings from producing impossible totals while avoiding the old
		/// multiplicative-share model, which could materially understate independent savings.
		/// </para>
		/// <para>
		/// A savings value with no measured resource basis is not included in the rollup; it remains
		/// visible on the individual finding but is not converted into a fabricated project total.
		/// </para>
		/// </summary>

		public void Finalise()
		{
			SavingsEstimator.Enrich(this);
			WorstSeverity = IssueSeverity.Info;
			NaiveSavingsSum = 0;

			long memoryBase = RuntimeSize > 0 ? RuntimeSize : 0;
			long diskBase = FileSize > 0 ? FileSize : 0;

			long memorySaved = 0L;
			long diskSaved = 0L;

			for (int i = 0; i < Issues.Count; i++)
			{
				var issue = Issues[i];
				if (i == 0 || issue.Severity > WorstSeverity) WorstSeverity = issue.Severity;

				long saving = issue.EstimatedSavings;
				if (saving <= 0) continue;

				// An unquantified saving contributes nothing to any total. AddIssue already forces
				// these to zero; this is the second line of the same defence, so a directly built
				// issue cannot slip an invented number into the rollup either.
				if (issue.Basis == SavingsBasis.Unknown) continue;

				NaiveSavingsSum += saving;

				FixContract.AddCappedSavings(ref memorySaved, ref diskSaved, memoryBase, diskBase, issue, null);
			}

			PotentialMemorySavings = memorySaved;
			PotentialDiskSavings = diskSaved;
			PotentialSavings = PotentialMemorySavings + PotentialDiskSavings;

			SearchCandidates.Clear();
			if (!string.IsNullOrEmpty(Name)) SearchCandidates.Add(Name);
			if (!string.IsNullOrEmpty(Path)) SearchCandidates.Add(Path);
			if (!string.IsNullOrEmpty(TypeLabel)) SearchCandidates.Add(TypeLabel);
			for (int i = 0; i < Issues.Count; i++)
			{
				SearchCandidates.Add(Issues[i].RuleId);
				if (!string.IsNullOrEmpty(Issues[i].Title)) SearchCandidates.Add(Issues[i].Title);
			}
		}
	}
}

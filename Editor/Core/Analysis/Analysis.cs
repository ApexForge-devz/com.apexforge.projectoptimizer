using System;
using System.Collections.Generic;
using UnityEditor;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Everything an analyzer needs to know about the scan it is part of.</summary>
	public sealed class ScanContext
	{
		public OptimizerSettings Settings;

		/// <summary>
		/// The scan scope: the assets this run analyzes and reports on. Narrowed by the folder,
		/// kind, size and build-reachability filters the user chose.
		/// </summary>
		public List<string> AllAssetPaths = new List<string>();

		/// <summary>Compact preclassification table parallel to AllAssetPaths. One byte per asset avoids repeated extension parsing.</summary>
		public List<byte> WorkKinds = new List<byte>();

		/// <summary>Dense identity table shared by the scan, background queues and source-data readers.</summary>
		public AssetCatalog AssetCatalog;

		/// <summary>
		/// The reference scope: every statically referenceable asset in the project, regardless of
		/// what the scan was pointed at.
		/// <para>
		/// These two are not the same set and must never be conflated. Restricting a scan to one
		/// folder narrows what is <em>reported</em>; it says nothing about what <em>references</em>
		/// the assets in that folder. Building the reference graph from the scan scope is how a tool
		/// concludes that a material has no referrers because the only prefab using it was out of
		/// scope - and then offers to delete it.
		/// </para>
		/// </summary>
		public List<string> ReferenceScopePaths = new List<string>();

		public readonly Dictionary<string, AssetEntry> EntriesByPath = new Dictionary<string, AssetEntry>();

		/// <summary>Assets reachable from build scenes / Resources / preloaded assets. Filled by the unused-asset pass.</summary>
		public HashSet<string> ReachableAssets;

		/// <summary>
		/// The same reachability set, when the scope already had to compute it to answer "only what
		/// ships in a build". Reused rather than recomputed: it costs a transitive dependency walk.
		/// </summary>
		public HashSet<string> BuildSet;

		/// <summary>True when build reachability was fully established for this scan.</summary>
		public bool BuildReachabilityComplete = true;

		/// <summary>Effective Build Profile configuration resolved once for this scan.</summary>
		public EffectiveBuildProfileSet BuildProfiles;

		/// <summary>Explains why build reachability could not be treated as complete.</summary>
		public string BuildReachabilityCaveat = string.Empty;

		/// <summary>
		/// Measurement cache. Analyzers should wrap anything that loads an asset in
		/// <see cref="ScanCache.Get{T}"/> so a rescan does not pay for it again.
		/// </summary>
		public ScanCache Cache;

		/// <summary>Only the currently processed asset keeps its live dependency hash in memory.</summary>
		public string CurrentAssetPath;
		public string CurrentDependencyHash;
		public string CurrentGuid;

		public string GetDependencyHash(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			if (string.Equals(CurrentAssetPath, assetPath, StringComparison.Ordinal) && CurrentDependencyHash != null)
				return CurrentDependencyHash;
			if (!string.Equals(CurrentAssetPath, assetPath, StringComparison.Ordinal))
			{
				CurrentAssetPath = assetPath;
				CurrentGuid = null;
			}
			CurrentDependencyHash = Cache != null ? Cache.GetLiveHash(assetPath) : null;
			return CurrentDependencyHash;
		}

		/// <summary>Returns the current asset GUID once per asset instead of repeating native AssetDatabase lookups across analyzers.</summary>
		public string GetGuid(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return string.Empty;
			if (AssetCatalog != null)
			{
				var catalogGuid = AssetCatalog.GuidFor(assetPath);
				if (!string.IsNullOrEmpty(catalogGuid))
				{
					CurrentAssetPath = assetPath;
					CurrentGuid = catalogGuid;
					return catalogGuid;
				}
			}
			if (!string.Equals(CurrentAssetPath, assetPath, StringComparison.Ordinal))
			{
				CurrentAssetPath = assetPath;
				CurrentDependencyHash = null;
				CurrentGuid = null;
			}
			if (CurrentGuid == null)
			{
				CurrentGuid = AssetDatabaseThreadSafeUtility.IsReady
					? AssetDatabaseThreadSafeUtility.AssetPathToGUID(assetPath)
					: string.Empty;
				if (string.IsNullOrEmpty(CurrentGuid)) CurrentGuid = AssetDatabase.AssetPathToGUID(assetPath);
			}
			return CurrentGuid;
		}

		/// <summary>ReferenceIndex hash provider. Reuses a hash already written by ScanCache when possible.</summary>
		public string GetReferenceDependencyHash(string assetPath)
		{
			if (Cache != null && Cache.TryGetStoredHash(assetPath, out var stored)) return stored;
			return Cache != null ? Cache.GetLiveHash(assetPath) : null;
		}

		public string GetReferenceValidationStamp(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return string.Empty;
			long fileTicks = FileMetadata != null ? FileMetadata.GetLastWriteTicks(assetPath) : 0L;
			long metaTicks = FileMetadata != null ? FileMetadata.GetMetaLastWriteTicks(assetPath) : 0L;
			return fileTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + metaTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		public T GetCached<T>(string assetPath, Func<T> measure) where T : class
		{
			if (Cache == null) return measure();
			string hash = GetDependencyHash(assetPath);
			// Multiple analyzers may legitimately persist different fact shapes for one asset
			// (for example a model's geometry facts and its animation facts). Namespace rows by
			// their CLR fact type so one analyzer can never overwrite another analyzer's cache row.
			string key = ScanCache.ScopedKey(assetPath, typeof(T).FullName);
			return Cache.Get(key, measure, hash);
		}

		/// <summary>Scan-wide cached filesystem sizes. Falls back to direct I/O for paths outside the table.</summary>
		public ScanFileMetadata FileMetadata;

		/// <summary>
		/// Who references what. Built once per scan and shared, since both the unused pass and the
		/// duplicate fix need it and it is expensive to produce.
		/// <para>
		/// Built over <see cref="ReferenceScopePaths"/>, never over <see cref="AllAssetPaths"/>.
		/// </para>
		/// </summary>
		public ReferenceIndex References;

		/// <summary>
		/// How often each material is used by a renderer, and in how many prefabs and scenes.
		/// Filled by the prefab/scene pass; null when prefabs and scenes were not scanned.
		/// </summary>
		public MaterialUsageIndex MaterialUsage;

		/// <summary>Lazy project-wide proof index. Built only by Deep/Exhaustive semantic analysis.</summary>
		public ProjectProofIndex ProofIndex;

		public ProjectProofIndex GetProofIndex()
		{
			if (ProofIndex == null) ProofIndex = new ProjectProofIndex();
			return ProofIndex;
		}

		// Only survives for the currently processed path. Multiple analyzers can inspect the same
		// imported model without calling LoadAllAssetsAtPath twice, but the scanner clears it after
		// the path so this does not pin every imported object in memory.
		string m_LoadedObjectsPath;
		UnityEngine.Object[] m_LoadedObjects;
		string m_CurrentTextPath;
		string m_CurrentText;

		public T GetMainAsset<T>(string assetPath) where T : UnityEngine.Object
			=> AssetLoadUtility.LoadAssetAtPath<T>(assetPath);

		public UnityEngine.Object GetMainAsset(string assetPath)
			=> AssetLoadUtility.LoadMainAssetAtPath(assetPath);

		public UnityEngine.Object[] GetLoadedObjects(string assetPath)
		{
			if (string.Equals(m_LoadedObjectsPath, assetPath, StringComparison.Ordinal) && m_LoadedObjects != null)
				return m_LoadedObjects;
			m_LoadedObjectsPath = assetPath;
			m_LoadedObjects = AssetLoadUtility.LoadAllAssetsAtPath(assetPath);
			return m_LoadedObjects;
		}

		public string GetAssetText(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			if (string.Equals(m_CurrentTextPath, assetPath, StringComparison.Ordinal)) return m_CurrentText;
			m_CurrentTextPath = assetPath;
			try { m_CurrentText = System.IO.File.ReadAllText(assetPath); }
			catch { m_CurrentText = null; }
			return m_CurrentText;
		}

		public void ClearLoadedObjects(string assetPath)
		{
			if (!string.Equals(m_LoadedObjectsPath, assetPath, StringComparison.Ordinal)) return;
			m_LoadedObjectsPath = null;
			m_LoadedObjects = null;
			if (string.Equals(m_CurrentTextPath, assetPath, StringComparison.Ordinal))
			{
				m_CurrentTextPath = null;
				m_CurrentText = null;
			}
		}
	}

	/// <summary>Analyzes a single asset path.</summary>
	[Flags]
	public enum AssetKindMask
	{
		None = 0,
		Texture = 1 << (int)AssetKind.Texture,
		Audio = 1 << (int)AssetKind.Audio,
		Video = 1 << (int)AssetKind.Video,
		Model = 1 << (int)AssetKind.Model,
		MaterialShader = 1 << (int)AssetKind.MaterialShader,
		Animation = 1 << (int)AssetKind.Animation,
		Prefab = 1 << (int)AssetKind.Prefab,
		Scene = 1 << (int)AssetKind.Scene,
		Script = 1 << (int)AssetKind.Script,
		FontAtlas = 1 << (int)AssetKind.FontAtlas,
		DataAsset = 1 << (int)AssetKind.DataAsset,
		Other = 1 << (int)AssetKind.Other
	}

	public interface IAssetAnalyzer
	{
		AssetCategory Category { get; }
		AssetKindMask SupportedKinds { get; }
		/// <summary>Called once before any asset is handed over, so analyzers can build per-scan caches.</summary>
		void BeginScan(ScanContext ctx);
		/// <summary>Cheap extension/type test, run before the expensive work.</summary>
		bool Handles(string assetPath);
		/// <summary>Returns a row for the asset, or null to skip it entirely.</summary>
		AssetEntry Analyze(string assetPath, ScanContext ctx);
	}

	/// <summary>Analyzes the project as a whole rather than one asset at a time.</summary>
	public interface IProjectAnalyzer
	{
		string DisplayName { get; }
		bool Enabled(OptimizerSettings settings);
		/// <summary>Yields rows; yield null to give a time slice back to the editor without producing a row.</summary>
		IEnumerable<AssetEntry> Analyze(ScanContext ctx);

		/// <summary>
		/// How far through its own work this pass is, 0..1, or -1 when it cannot say.
		/// <para>
		/// A project-wide pass is one opaque enumerator to the scanner, so without this the progress
		/// bar has nothing to move between the start and the end of it. On a large project the
		/// duplicate and unused pass runs for minutes, and a bar that sits still for minutes and then
		/// jumps to done is indistinguishable from one that has hung.
		/// </para>
		/// </summary>
		float Progress { get; }
	}

	public static class IssueExtensions
	{
		/// <summary>
		/// Adds an issue for <paramref name="ruleId"/> unless the user disabled that rule.
		/// Title/why/fix text and severity come from the rule catalog (with user overrides applied).
		/// <para>
		/// <paramref name="estimatedSavings"/> is what this fix saves <em>on its own</em>, and
		/// <paramref name="basis"/> says which pool it comes out of. Both matter: two fixes drawing
		/// on the same pool overlap, and only the basis makes that visible, so it is the difference
		/// between a total that can be trusted and one that can exceed the asset itself.
		/// </para>
		/// </summary>
		/// <returns>The issue, so evidence can be chained onto it, or null when the rule is off.</returns>
		public static OptimizationIssue AddIssue(
			this AssetEntry entry,
			string ruleId,
			string current,
			string recommended,
			long estimatedSavings = 0,
			Action<FixContext> fix = null,
			string detail = null,
			SavingsBasis basis = SavingsBasis.Memory,
			Func<FixContext, bool> verify = null,
			List<FixOption> options = null)
		{
			var settings = BackgroundRuleExecution.CurrentSettings ?? OptimizerSettings.Instance;
			if (settings == null || !settings.IsRuleEnabled(ruleId)) return null;

			if (settings.IsSuppressed(ruleId, entry.Path)) return null;

			var def = RuleCatalog.Get(ruleId);
			if (def == null)
			{
				UnityEngine.Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Unregistered rule id '{ruleId}'.");
				return null;
			}

			FixKind requestedKind = def.FixKind;

			long savings = basis == SavingsBasis.Unknown ? 0L : Math.Max(0, estimatedSavings);

			var issue = new OptimizationIssue
			{
				IssueInstanceId = (entry.Path ?? string.Empty) + "::" + ruleId + "::" + entry.Issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
				RuleId = ruleId,
				Title = def.Title,
				Why = def.Why,
				FixHint = def.Fix,
				Severity = settings.SeverityFor(def),
				Category = def.Category,
				Confidence = def.Confidence,
				Risk = def.DefaultFixRisk,
				BackupRequired = def.DefaultBackupRequired,
				Current = current,
				Recommended = recommended,
				EstimatedSavings = savings,
				Basis = basis,
				FixKind = requestedKind,
				Fix = requestedKind != FixKind.None ? fix : null,
				Verify = requestedKind != FixKind.None ? verify : null,
				RecommendationConfidence = def.RecommendationConfidence,
				RequiredEvidence = def.RequiredEvidence,
				RuleCatalogSignature = RuleCatalog.SemanticSignature,
				AnalyzerSignature = EvidenceContract.AnalyzerSignature,
				FixContractSignature = EvidenceContract.FixSignature
			};

			if (requestedKind == FixKind.Verified)
			{
				issue.AssetEvidencePath = entry.Path;
				issue.AssetEvidenceSignature = EvidenceContract.ComputeAssetEvidenceSignature(entry.Path);
				if (def.RequiredEvidence == EvidenceRequirement.None && !string.IsNullOrEmpty(issue.AssetEvidenceSignature))
				{
					issue.EvidenceState = EvidenceState.Fresh;
					issue.EvidenceSignature = issue.AssetEvidenceSignature;
				}
			}

			if (def.RequiredEvidence != EvidenceRequirement.None)
				issue.EvidenceState = EvidenceState.InsufficientSamples;


			if (requestedKind != FixKind.None && options != null)
			{
				var normalizedOptions = new List<FixOption>(options.Count);
				var actionIds = new HashSet<string>(StringComparer.Ordinal);
				for (int i = 0; i < options.Count; i++)
				{
					var option = options[i];
					if (option == null || option.Apply == null) continue;
					if (string.IsNullOrEmpty(option.ActionId))
						option.ActionId = option.StableActionId(ruleId);
					if (!actionIds.Add(option.ActionId)) continue;
					if (option.Risk == FixRisk.Unspecified)
						option.Risk = def.DefaultFixRisk != FixRisk.Unspecified ? def.DefaultFixRisk : FixRisk.Medium;
					if (option.Basis == SavingsBasis.Unknown) option.EstimatedSavings = 0;
					normalizedOptions.Add(option);
				}
				if (requestedKind == FixKind.Review) issue.Fix = null;
				issue.Options.AddRange(normalizedOptions);
			}

			// A detail passed as plain text stays plain text. Analyzers with something structured to
			// say attach it with the helpers below instead, which is what lets the window colour it.
			if (!string.IsNullOrEmpty(detail))
				issue.Details.Add(new IssueDetail { Text = detail, Kind = DetailKind.Note });

			entry.Issues.Add(issue);
			ScanTiming.MarkIssue(ruleId, entry.Path);
			return issue;
		}

		public static OptimizationIssue WithEvidence(this OptimizationIssue issue, EvidenceRequirement requirement, EvidenceState state, string signature)
		{
			if (issue == null) return null;
			issue.RequiredEvidence = requirement;
			issue.EvidenceState = state;
			issue.EvidenceSignature = signature;
			return issue;
		}

		/// <summary>
		/// Records which platform and quality level this finding was measured at, so it can be
		/// reproduced, verified, and - if it ever gains a fix - targeted at that exact level rather
		/// than at whichever one the editor is currently showing.
		/// </summary>
		public static OptimizationIssue AtQualityLevel(this OptimizationIssue issue,
			string platform, int levelIndex, string levelName)
		{
			if (issue == null) return null;
			issue.TargetPlatform = platform;
			issue.QualityLevelIndex = levelIndex;
			issue.QualityLevelName = levelName;
			return issue;
		}

		/// <summary>Records the source line this finding is about, so the UI can jump straight to it.</summary>
		public static OptimizationIssue AtLine(this OptimizationIssue issue, int line)
		{
			if (issue != null && line > 0) issue.SourceLine = line;
			return issue;
		}

		/// <summary>Adds a plain line of supporting text.</summary>
		public static OptimizationIssue Note(this OptimizationIssue issue, string text, string label = null)
			=> Detail(issue, label, text, DetailKind.Note, null);

		/// <summary>Adds the copy being kept, or whatever the fix is preserving.</summary>
		public static OptimizationIssue Good(this OptimizationIssue issue, string label, string text,
			string assetPath = null)
			=> Detail(issue, label, text, DetailKind.Good, assetPath);

		/// <summary>Adds the thing at fault, so it stands out from what is being kept.</summary>
		public static OptimizationIssue Bad(this OptimizationIssue issue, string label, string text,
			string assetPath = null)
			=> Detail(issue, label, text, DetailKind.Bad, assetPath);

		/// <summary>
		/// Adds a list under one label, one entry per line. Long lists are cut off with a count of
		/// what was left out: twenty broken references make the same point as two hundred, and the
		/// details panel has to stay readable.
		/// </summary>
		public static OptimizationIssue Items(this OptimizationIssue issue, string label,
			IReadOnlyList<string> items, DetailKind kind = DetailKind.Bad, bool arePaths = false, int max = 12)
		{
			if (issue == null || items == null || items.Count == 0) return issue;

			int shown = Math.Min(items.Count, max);
			for (int i = 0; i < shown; i++)
			{
				var text = items[i];
				if (string.IsNullOrEmpty(text)) continue;
				issue.Details.Add(new IssueDetail
				{
					// Only the first row is labelled, so the list reads as one block.
					Label = i == 0 ? label : null,
					Text = text,
					Kind = kind,
					AssetPath = arePaths ? text : null
				});
			}

			if (items.Count > shown)
			{
				issue.Details.Add(new IssueDetail
				{
					Text = "and " + (items.Count - shown) + " more",
					Kind = DetailKind.Note
				});
			}
			return issue;
		}

		static OptimizationIssue Detail(OptimizationIssue issue, string label, string text,
			DetailKind kind, string assetPath)
		{
			if (issue == null || string.IsNullOrEmpty(text)) return issue;
			issue.Details.Add(new IssueDetail
			{
				Label = label,
				Text = text,
				Kind = kind,
				AssetPath = assetPath
			});
			return issue;
		}

	}

	/// <summary>One metric participating in a grouped user-facing insight.</summary>
	public sealed class GroupedFindingMetric
	{
		public string RuleId;
		public string Label;
		public string Value;
		public bool ThresholdExceeded;
		public string Current;
		public string Recommended;

		public GroupedFindingMetric(string ruleId, string label, string value, bool thresholdExceeded,
			string current = null, string recommended = null)
		{
			RuleId = ruleId;
			Label = label;
			Value = value;
			ThresholdExceeded = thresholdExceeded;
			Current = current;
			Recommended = recommended;
		}
	}

	/// <summary>
	/// Shared policy for grouping several measurements into one issue without defeating rule settings.
	/// The chosen issue ID comes from a triggered, enabled, unsuppressed metric; ties retain the
	/// caller's deterministic metric order. Every eligible metric remains visible as evidence.
	/// </summary>
	public static class GroupedFindingPolicy
	{
		public static bool CanReport(string ruleId, string assetPath)
		{
			var settings = BackgroundRuleExecution.CurrentSettings ?? OptimizerSettings.Instance;
			return settings != null && !string.IsNullOrEmpty(ruleId)
				&& settings.IsRuleEnabled(ruleId)
				&& !settings.IsSuppressed(ruleId, assetPath)
				&& RuleCatalog.Get(ruleId) != null;
		}

		public static OptimizationIssue AddGroupedIssue(AssetEntry entry,
			IReadOnlyList<GroupedFindingMetric> metrics, string explanation)
		{
			if (entry == null || metrics == null || metrics.Count == 0) return null;
			var settings = BackgroundRuleExecution.CurrentSettings ?? OptimizerSettings.Instance;
			if (settings == null) return null;

			GroupedFindingMetric selected = null;
			IssueSeverity selectedSeverity = IssueSeverity.Info;
			for (int i = 0; i < metrics.Count; i++)
			{
				var metric = metrics[i];
				if (metric == null || !metric.ThresholdExceeded || !CanReport(metric.RuleId, entry.Path)) continue;
				var definition = RuleCatalog.Get(metric.RuleId);
				if (definition == null) continue;
				IssueSeverity severity = settings.SeverityFor(definition);
				if (selected == null || severity > selectedSeverity)
				{
					selected = metric;
					selectedSeverity = severity;
				}
			}

			if (selected == null) return null;
			var issue = entry.AddIssue(selected.RuleId,
				selected.Current ?? selected.Value ?? string.Empty,
				selected.Recommended ?? string.Empty,
				0, null, explanation, SavingsBasis.Unknown);
			if (issue == null) return null;

			var measurements = new List<string>(metrics.Count);
			var exceeded = new List<string>(metrics.Count);
			for (int i = 0; i < metrics.Count; i++)
			{
				var metric = metrics[i];
				if (metric == null || string.IsNullOrEmpty(metric.Label)
					|| !CanReport(metric.RuleId, entry.Path)) continue;
				// A grouped finding must respect each metric's rule toggle and per-asset suppression.
				// Otherwise disabling one rule would still leak its measurement through a sibling rule.
				measurements.Add(metric.Label + ": " + (metric.Value ?? "not available"));
				if (metric.ThresholdExceeded) exceeded.Add(metric.Label);
			}

			issue.Items("Measurements", measurements, DetailKind.Note);
			issue.Items("Thresholds exceeded", exceeded, DetailKind.Bad);
			if (measurements.Count > 1)
				issue.Note("These measurements describe different aspects of the same asset. Do not add their values or infer a combined variant/savings estimate unless the units and overlap are independently established.", "Grouping policy");
			return issue;
		}
	}

}

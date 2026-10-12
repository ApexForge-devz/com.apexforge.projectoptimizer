using System;
using System.Collections.Generic;
using System.IO;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One <see cref="AssetEntry.Fields"/> value. Only the shapes analyzers actually produce today.</summary>
	[Serializable]
	public sealed class PersistedField
	{
		public string Key;

		/// <summary>0 = string, 1 = long, 2 = double.</summary>
		public int Kind;
		public string StringValue;
		public long LongValue;
		public double DoubleValue;
	}

	[Serializable]
	public sealed class PersistedDetail
	{
		public string Label;
		public string Text;
		public int Kind;
		public string AssetPath;
	}

	[Serializable]
	public sealed class PersistedFixOption
	{
		public string ActionId;
		public string Label;
		public string ResultDescription;
		public string Warning;
		public bool Recommended;
		public int BackupRequiredOverride = -1;
		public int Risk;
		public long EstimatedSavings;
		public int Basis;
		public bool HasVerifier;
		public bool HasApply;
	}

	[Serializable]
	public sealed class PersistedIssue
	{
		public string RuleId;
		public int Severity;
		public string Current;
		public string Recommended;
		public long EstimatedSavings;
		public int Basis;
		public int Confidence;
		public int RecommendationConfidence;
		public int RequiredEvidence;
		public int EvidenceState;
		public string EvidenceSignature;
		public string AssetEvidenceSignature;
		public string AssetEvidencePath;
		public string RuleCatalogSignature;
		public string AnalyzerSignature;
		public string FixContractSignature;
		public int OriginalFixKind;
		public int Risk;
		public bool BackupRequired;
		public bool HadLiveAction;
		public bool HasVerifier;
		public int SourceLine = -1;
		public int QualityLevelIndex = -1;
		public string QualityLevelName;
		public string TargetPlatform;
		public string ImporterScope;
		public List<PersistedDetail> Details = new List<PersistedDetail>();
		public List<PersistedFixOption> Options = new List<PersistedFixOption>();
	}

	[Serializable]
	public sealed class PersistedEntry
	{
		public string Guid;
		public string Path;
		public string Name;
		public string Directory;
		public string Extension;
		public int Category;
		public string TypeLabel;
		public long FileSize;
		public long RuntimeSize = -1;
		public List<PersistedField> Fields = new List<PersistedField>();
		public List<PersistedIssue> Issues = new List<PersistedIssue>();
	}

	/// <summary>The on-disk shape of a restorable <see cref="ScanResult"/>. See <see cref="ScanResultStore"/>.</summary>
	[Serializable]
	public sealed class PersistedScanResult
	{
		public int Version;
		public int ToolSchema;
		public string UnityVersion;
		public long ScanTimeTicks;
		public double DurationSeconds;
		public int ScannedAssets;
		public int SettingsRevision;
		public long TotalFileSize;
		public List<string> CompletenessCaveats = new List<string>();
		public List<string> AnalysisLimitations = new List<string>();
		public string RuleCatalogSignature;
		public string AnalyzerSignature;
		public string FixContractSignature;
		public int EvidenceSchema;
		public long CrossAssetEvidenceJournalGeneration = -1;
		public string CrossAssetEvidenceScopeFingerprint;
		public string CrossAssetEvidenceContentIdentity;
		public bool CrossAssetEvidenceComplete;
		public List<PersistedEntry> Entries = new List<PersistedEntry>();
	}

	/// <summary>
	/// Persists the last completed scan in Library so the Project Optimizer window can restore the exact
	/// saved findings after a domain reload without running another scan. Live fix delegates cannot be
	/// serialized; their metadata is retained and the concrete action is rebuilt only if the user invokes it.
	/// <para>
	/// A domain reload is not a scan invalidation event. A finding that was fixable a moment before the
	/// Editor reloaded its scripts is still true and still fixable afterwards - nothing about the
	/// project changed, only the managed process looking at it did. An earlier version of this class
	/// got this wrong: it reconstructed every restored issue from a data snapshot and then called
	/// <see cref="OptimizationIssue.MarkStale"/> on all of them unconditionally, because a snapshot
	/// cannot carry a live <c>Fix</c> delegate. That made every restored finding read "rescan required"
	/// regardless of whether anything had actually changed - domain reload was standing in for
	/// invalidation instead of being checked against it.
	/// </para>
	/// <para>
	/// The snapshot stores the complete display payload for every finding. Live Fix/Review delegates
	/// cannot be serialized, so their metadata is persisted and the action is rehydrated only when the
	/// user invokes it. That keeps startup proportional to snapshot deserialization rather than to
	/// the number of analyzers that produced the findings.
	/// </para>
	/// <para>
	/// Cross-asset findings are the one deliberate exception: the Duplicate and Unused categories, and
	/// the two per-asset rules (MAT004/MAT009) decided by comparing every material in the project.
	/// Nothing about these can be re-derived for a single asset in isolation - only a full walk of the
	/// reference scope confirms them, and running that walk on every domain reload would turn a
	/// lightweight restore into exactly the automatic full scan this design must not be. These are
	/// restored as display data and then retired through the existing
	/// <see cref="ScanResult.InvalidateCrossAssetFindings"/> - the same mechanism, and the same honest
	/// "rescan required" outcome, already used whenever a targeted refresh cannot confirm them either.
	/// This is a real, narrow invalidation condition - the one slice of a restored scan a domain reload
	/// actually does leave unconfirmed - not a blanket one applied to everything that came back.
	/// </para>
	/// </summary>
	public static class ScanResultStore
	{
		/// <summary>Bumped whenever the shape of <see cref="PersistedScanResult"/> or its children changes.</summary>
		const int k_Version = 12;

		static readonly string k_Path = ProjectOptimizerConstants.ScanSnapshotPath;

		/// <summary>True when a structurally valid-looking snapshot file exists and can be offered to the UI.</summary>
		public static bool HasSnapshot => File.Exists(k_Path);

		public static string SnapshotPath => k_Path;

		const string k_CrossAssetInvalidationReason =
			"This finding depends on project-wide relationship evidence. Its persisted evidence identity no longer "
			+ "matches the current project state, so it must be rescanned before it can be acted on.";

		/// <summary>
		/// Saves the outcome of a completed scan. A no-op for anything that is not one: an aborted or
		/// cancelled scan must never overwrite the last good snapshot with a half-built result.
		/// </summary>
		public static void Save(ScanResult result)
		{
			if (result == null || result.Aborted) return;

			try
			{
				if (!CacheFileUtility.EnsureCacheFolder()) return;

				var snapshot = Build(result);
				var json = JsonUtility.ToJson(snapshot);

				// Written beside the live file and swapped in, the same way ScanCache protects its own
				// file: an interruption mid-write must never leave a truncated file that parses cleanly
				// up to the cut and is then trusted.
				var temp = k_Path + ".tmp";
				File.WriteAllText(temp, json);
				CacheFileUtility.ReplaceFile(temp, k_Path);
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not save the scan cache: " + e.Message);
			}
		}

		static PersistedScanResult Build(ScanResult result)
		{
			var snapshot = new PersistedScanResult
			{
				Version = k_Version,
				ToolSchema = OptimizerVersion.Schema,
				UnityVersion = Application.unityVersion,
				RuleCatalogSignature = RuleCatalog.SemanticSignature,
				AnalyzerSignature = EvidenceContract.AnalyzerSignature,
				FixContractSignature = EvidenceContract.FixSignature,
				EvidenceSchema = EvidenceContract.EvidenceSchemaVersion,
				CrossAssetEvidenceJournalGeneration = result.CrossAssetEvidenceJournalGeneration,
				CrossAssetEvidenceScopeFingerprint = result.CrossAssetEvidenceScopeFingerprint,
				CrossAssetEvidenceContentIdentity = result.CrossAssetEvidenceContentIdentity,
				CrossAssetEvidenceComplete = result.CrossAssetEvidenceComplete,
				ScanTimeTicks = result.ScanTime.Ticks,
				DurationSeconds = result.DurationSeconds,
				ScannedAssets = result.ScannedAssets,
				SettingsRevision = result.SettingsRevision,
				TotalFileSize = result.TotalFileSize
			};
			snapshot.CompletenessCaveats.AddRange(result.CompletenessCaveats);
			snapshot.AnalysisLimitations.AddRange(result.AnalysisLimitations);

			foreach (var entry in result.Entries)
			{
				if (!entry.HasIssues) continue;
				snapshot.Entries.Add(BuildEntry(entry));
			}
			return snapshot;
		}

		/// <summary>
		/// Persists the complete displayable finding. The restore path no longer re-runs analyzers merely
		/// to reconstruct the screen. Live Fix/Review delegates cannot be serialized, so their metadata is
		/// stored and the actual action is hydrated lazily when the user invokes it.
		/// </summary>
		static PersistedEntry BuildEntry(AssetEntry entry)
		{
			var persisted = new PersistedEntry
			{
				Guid = entry.Guid,
				Path = entry.Path,
				Name = entry.Name,
				Directory = entry.Directory,
				Extension = entry.Extension,
				Category = (int)entry.Category,
				TypeLabel = entry.TypeLabel,
				FileSize = entry.FileSize,
				RuntimeSize = entry.RuntimeSize
			};

			foreach (var pair in entry.Fields)
				persisted.Fields.Add(BuildField(pair.Key, pair.Value));

			foreach (var issue in entry.Issues)
				persisted.Issues.Add(BuildIssue(issue));

			return persisted;
		}

		static PersistedField BuildField(string key, object value)
		{
			var field = new PersistedField { Key = key };
			switch (value)
			{
				case long l: field.Kind = 1; field.LongValue = l; break;
				case int i: field.Kind = 1; field.LongValue = i; break;
				case double d: field.Kind = 2; field.DoubleValue = d; break;
				case float f: field.Kind = 2; field.DoubleValue = f; break;
				default: field.Kind = 0; field.StringValue = value?.ToString(); break;
			}
			return field;
		}

		static PersistedIssue BuildIssue(OptimizationIssue issue)
		{
			var persisted = new PersistedIssue
			{
				RuleId = issue.RuleId,
				Severity = (int)issue.Severity,
				Current = issue.Current,
				Recommended = issue.Recommended,
				EstimatedSavings = issue.EstimatedSavings,
				Basis = (int)issue.Basis,
				Confidence = (int)issue.Confidence,
				RecommendationConfidence = (int)issue.RecommendationConfidence,
				RequiredEvidence = (int)issue.RequiredEvidence,
				EvidenceState = (int)issue.EvidenceState,
				EvidenceSignature = issue.EvidenceSignature,
				AssetEvidenceSignature = issue.AssetEvidenceSignature,
				AssetEvidencePath = issue.AssetEvidencePath,
				RuleCatalogSignature = issue.RuleCatalogSignature,
				AnalyzerSignature = issue.AnalyzerSignature,
				FixContractSignature = issue.FixContractSignature,
				OriginalFixKind = (int)issue.FixKind,
				Risk = (int)issue.Risk,
				BackupRequired = issue.BackupRequired,
				HadLiveAction = issue.Fix != null || (issue.Options != null && issue.Options.Exists(o => o != null && o.Apply != null)),
				HasVerifier = issue.Verify != null,
				SourceLine = issue.SourceLine,
				QualityLevelIndex = issue.QualityLevelIndex,
				QualityLevelName = issue.QualityLevelName,
				TargetPlatform = issue.TargetPlatform,
				ImporterScope = issue.ImporterScope
			};

			foreach (var detail in issue.Details)
			{
				persisted.Details.Add(new PersistedDetail
				{
					Label = detail.Label,
					Text = detail.Text,
					Kind = (int)detail.Kind,
					AssetPath = detail.AssetPath
				});
			}

			foreach (var option in issue.Options)
			{
				if (option == null) continue;
				persisted.Options.Add(new PersistedFixOption
				{
					ActionId = option.ActionId,
					Label = option.Label,
					ResultDescription = option.ResultDescription,
					Warning = option.Warning,
					Recommended = option.Recommended,
					BackupRequiredOverride = option.BackupRequiredOverride.HasValue ? (option.BackupRequiredOverride.Value ? 1 : 0) : -1,
					Risk = (int)option.Risk,
					EstimatedSavings = option.EstimatedSavings,
					Basis = (int)option.Basis,
					HasVerifier = option.Verify != null,
					HasApply = option.Apply != null
				});
			}

			return persisted;
		}

		/// <summary>
		/// Starts a resumable, snapshot-first restore. No per-asset analyzer is invoked merely to paint
		/// the restored result. Live Fix/Review work is hydrated only when a user actually invokes it.
		/// </summary>
		public static RestoreOperation BeginRestore(ProjectScanner scanner, OptimizerSettings settings)
		{
			if (scanner == null || settings == null) return null;
			var snapshot = LoadValidSnapshot();
			return snapshot == null ? null : new RestoreOperation(scanner, settings, snapshot);
		}

		/// <summary>
		/// Synchronous compatibility wrapper for non-UI callers.
		/// </summary>
		public static ScanResult TryLoad(ProjectScanner scanner, OptimizerSettings settings)
		{
			var operation = BeginRestore(scanner, settings);
			if (operation == null) return null;

			while (!operation.IsComplete)
				operation.Tick(1000.0);
			return operation.Result;
		}

		/// <summary>Incremental, editor-friendly restoration of persisted scan data without live analyzer calls.</summary>
		public sealed class RestoreOperation
		{
			readonly ProjectScanner m_Scanner;
			readonly PersistedScanResult m_Snapshot;
			ScanContext m_RestoreContext;
			readonly ScanResult m_Result;
			bool m_Finished;

			public bool IsComplete => m_Finished;
			public ScanResult Result => m_Finished ? m_Result : null;
			public string Status { get; private set; } = "Restoring previous scan...";

			internal RestoreOperation(ProjectScanner scanner, OptimizerSettings settings, PersistedScanResult snapshot)
			{
				m_Scanner = scanner;
				m_Snapshot = snapshot;
				m_Result = new ScanResult
				{
					ScanTime = new DateTime(snapshot.ScanTimeTicks),
					DurationSeconds = snapshot.DurationSeconds,
					ScannedAssets = snapshot.ScannedAssets,
					SettingsRevision = snapshot.SettingsRevision,
					TotalFileSize = snapshot.TotalFileSize
				};

				if (snapshot.CompletenessCaveats != null)
					foreach (var caveat in snapshot.CompletenessCaveats)
						m_Result.AddCaveat(caveat);
				if (snapshot.AnalysisLimitations != null)
					foreach (var limitation in snapshot.AnalysisLimitations)
						m_Result.AddLimitation(limitation);

				// Restore is display-first, but executable evidence cannot be trusted blindly. We compare only
				// deterministic semantic/evidence identities here; no analyzers or per-asset validation are run.
				bool semanticMismatch = !string.Equals(snapshot.RuleCatalogSignature, RuleCatalog.SemanticSignature, StringComparison.Ordinal)
					|| !string.Equals(snapshot.AnalyzerSignature, EvidenceContract.AnalyzerSignature, StringComparison.Ordinal)
					|| !string.Equals(snapshot.FixContractSignature, EvidenceContract.FixSignature, StringComparison.Ordinal)
					|| snapshot.EvidenceSchema != EvidenceContract.EvidenceSchemaVersion;
				bool crossAssetStale = !IsCrossAssetEvidenceFresh(snapshot);

				if (snapshot.Entries != null)
				{
					for (int i = 0; i < snapshot.Entries.Count; i++)
					{
						var persisted = snapshot.Entries[i];
						if (persisted == null || string.IsNullOrEmpty(persisted.Path)) continue;

						var entry = RestoreEntry(persisted, semanticMismatch, crossAssetStale);
						if (entry == null) continue;
						entry.Finalise();
						m_Result.Add(entry);
					}
				}

				m_Result.CrossAssetEvidenceJournalGeneration = snapshot.CrossAssetEvidenceJournalGeneration;
				m_Result.CrossAssetEvidenceScopeFingerprint = snapshot.CrossAssetEvidenceScopeFingerprint;
				m_Result.CrossAssetEvidenceContentIdentity = snapshot.CrossAssetEvidenceContentIdentity;
				m_Result.CrossAssetEvidenceComplete = snapshot.CrossAssetEvidenceComplete && !crossAssetStale;
				if (crossAssetStale)
					m_Result.MarkCrossAssetEvidenceStale("Persisted cross-asset evidence no longer matches the current project state; a fresh project-wide pass is required.");
				if (crossAssetStale)
				m_Result.InvalidateCrossAssetFindings(m_Result.CrossAssetStaleReason);
				m_Result.Recount();
				Status = crossAssetStale ? "Restored previous scan; stale cross-asset findings require a fresh pass." : "Restored previous scan.";
				m_Finished = true;

				// Give the scanner the restored result immediately, but deliberately without a live scan
				// context. A real ScanContext is constructed lazily only if a user invokes a live Fix/Review
				// action or asks for an explicit refresh.
				m_Scanner?.AdoptRestoredResult(m_Result, null);
			}

			public void Tick(double budgetMs)
			{
				// Snapshot reconstruction is completed in the constructor. There is intentionally no
				// per-entry analyzer work and no time-sliced verification here.
			}

			ScanContext EnsureLiveContext()
			{
				if (m_RestoreContext != null) return m_RestoreContext;
				if (m_Scanner == null) throw new InvalidOperationException("The optimizer scanner is no longer available.");

				m_RestoreContext = m_Scanner.PrepareRestoreContext(OptimizerSettings.Instance);
				m_Scanner.AdoptRestoredResult(m_Result, m_RestoreContext);
				return m_RestoreContext;
			}

			AssetEntry FindLiveIssue(string path, AssetCategory category, string ruleId)
			{
				if (string.IsNullOrEmpty(path)) return null;
				var candidates = m_Scanner.AnalyzeAsset(path, EnsureLiveContext());
				if (candidates == null) return null;

				for (int i = 0; i < candidates.Count; i++)
				{
					var entry = candidates[i];
					if (entry == null || entry.Category != category) continue;
					for (int j = 0; j < entry.Issues.Count; j++)
						if (entry.Issues[j].RuleId == ruleId)
							return entry;
				}

				return null;
			}

			void ExecuteLiveFix(FixContext context, string path, AssetCategory category, string ruleId)
			{
				var live = FindLiveIssue(path, category, ruleId);
				if (live == null || live.Issues == null)
					throw new InvalidOperationException("The finding could not be revalidated because the asset no longer produces it.");

				for (int i = 0; i < live.Issues.Count; i++)
				{
					var candidate = live.Issues[i];
					if (candidate.RuleId != ruleId) continue;
					if (candidate.FixKind == FixKind.None || candidate.Fix == null)
						throw new InvalidOperationException("The finding is no longer fixable after live revalidation.");
					candidate.Fix(context);
					return;
				}

				throw new InvalidOperationException("The finding is no longer present after live revalidation.");
			}

			bool VerifyLiveFix(FixContext context, string path, AssetCategory category, string ruleId)
			{
				var live = FindLiveIssue(path, category, ruleId);
				if (live == null) return false;
				foreach (var candidate in live.Issues)
					if (candidate.RuleId == ruleId)
						return candidate.Verify != null && candidate.Verify(context);
				return false;
			}

			void ExecuteLiveOption(FixContext context, string path, AssetCategory category, string ruleId, string actionId)
			{
				var live = FindLiveIssue(path, category, ruleId);
				if (live == null)
					throw new InvalidOperationException("The finding no longer exists after live revalidation.");

				foreach (var candidate in live.Issues)
				{
					if (candidate.RuleId != ruleId) continue;
					foreach (var option in candidate.Options)
					{
						if (!string.Equals(option.StableActionId(ruleId), actionId, StringComparison.Ordinal)) continue;
						if (option.Apply == null)
							throw new InvalidOperationException("The selected option is no longer applicable.");
						option.Apply(context);
						return;
					}
				}

				throw new InvalidOperationException("The selected option is no longer applicable after live revalidation.");
			}

			bool VerifyLiveOption(FixContext context, string path, AssetCategory category, string ruleId, string actionId)
			{
				var live = FindLiveIssue(path, category, ruleId);
				if (live == null) return false;

				foreach (var candidate in live.Issues)
				{
					if (candidate.RuleId != ruleId) continue;
					foreach (var option in candidate.Options)
						if (string.Equals(option.StableActionId(ruleId), actionId, StringComparison.Ordinal))
							return option.Verify != null && option.Verify(context);
				}

				return false;
			}

			OptimizationIssue RestoreIssueSnapshot(PersistedIssue persisted, string path, AssetCategory category, int index, bool forceStale, bool semanticMismatch)
			{
				var def = RuleCatalog.Get(persisted.RuleId);
				var issue = new OptimizationIssue
				{
					IssueInstanceId = (path ?? string.Empty) + "::" + persisted.RuleId + "::" + index,
					RuleId = persisted.RuleId,
					Title = def != null ? def.Title : persisted.RuleId,
					Why = def != null ? def.Why : string.Empty,
					FixHint = def != null ? def.Fix : string.Empty,
					Severity = (IssueSeverity)persisted.Severity,
					Category = category,
					Current = persisted.Current,
					Recommended = persisted.Recommended,
					EstimatedSavings = persisted.EstimatedSavings,
					Basis = (SavingsBasis)persisted.Basis,
					Confidence = (OptimizationConfidence)persisted.Confidence,
					RecommendationConfidence = (OptimizationConfidence)persisted.RecommendationConfidence,
					RequiredEvidence = (EvidenceRequirement)persisted.RequiredEvidence,
					EvidenceState = (EvidenceState)persisted.EvidenceState,
					EvidenceSignature = persisted.EvidenceSignature,
					AssetEvidenceSignature = persisted.AssetEvidenceSignature,
					AssetEvidencePath = string.IsNullOrEmpty(persisted.AssetEvidencePath) ? path : persisted.AssetEvidencePath,
					RuleCatalogSignature = persisted.RuleCatalogSignature,
					AnalyzerSignature = persisted.AnalyzerSignature,
					FixContractSignature = persisted.FixContractSignature,
					Risk = (FixRisk)persisted.Risk,
					BackupRequired = persisted.BackupRequired,
					SourceLine = persisted.SourceLine,
					QualityLevelIndex = persisted.QualityLevelIndex,
					QualityLevelName = persisted.QualityLevelName,
					TargetPlatform = persisted.TargetPlatform,
					ImporterScope = persisted.ImporterScope,
					FixKind = (FixKind)persisted.OriginalFixKind
				};

				if (!forceStale && !semanticMismatch && persisted.OriginalFixKind == (int)FixKind.Verified
					&& !string.IsNullOrEmpty(persisted.AssetEvidenceSignature))
				{
					string currentAssetEvidence = EvidenceContract.ComputeAssetEvidenceSignature(path);
					if (string.IsNullOrEmpty(currentAssetEvidence) || !string.Equals(currentAssetEvidence, persisted.AssetEvidenceSignature, StringComparison.Ordinal))
						forceStale = true;
				}

				if (persisted.Details != null)
				{
					for (int i = 0; i < persisted.Details.Count; i++)
					{
						var detail = persisted.Details[i];
						if (detail == null) continue;
						issue.Details.Add(new IssueDetail
						{
							Label = detail.Label,
							Text = detail.Text,
							Kind = (DetailKind)detail.Kind,
							AssetPath = detail.AssetPath
						});
					}
				}

				if (persisted.Options != null)
				{
					for (int i = 0; i < persisted.Options.Count; i++)
					{
						var savedOption = persisted.Options[i];
						if (savedOption == null) continue;

						var lazyOption = new FixOption
						{
							ActionId = savedOption.ActionId,
							Label = savedOption.Label,
							ResultDescription = savedOption.ResultDescription,
							Warning = savedOption.Warning,
							Recommended = savedOption.Recommended,
							BackupRequiredOverride = savedOption.BackupRequiredOverride >= 0 ? savedOption.BackupRequiredOverride != 0 : (bool?)null,
							Risk = (FixRisk)savedOption.Risk,
							EstimatedSavings = savedOption.EstimatedSavings,
							Basis = (SavingsBasis)savedOption.Basis
						};

						string actionId = savedOption.ActionId;
						if (savedOption.HasApply)
							lazyOption.Apply = c => ExecuteLiveOption(c, path, category, persisted.RuleId, actionId);
						if (savedOption.HasVerifier)
							lazyOption.Verify = c => VerifyLiveOption(c, path, category, persisted.RuleId, actionId);
						issue.Options.Add(lazyOption);
					}
				}

				if (persisted.HadLiveAction && issue.Options.Count == 0 && issue.FixKind != FixKind.None)
				{
					issue.Fix = c => ExecuteLiveFix(c, path, category, persisted.RuleId);
					if (persisted.HasVerifier)
						issue.Verify = c => VerifyLiveFix(c, path, category, persisted.RuleId);
				}

				if (forceStale)
					issue.MarkStale(semanticMismatch
						? "The rule/analyzer/fix semantics changed since this finding was scanned; rescan before acting."
						: "Persisted evidence no longer matches the current project or asset state; rescan before acting.");

				return issue;
			}

			AssetEntry RestoreEntry(PersistedEntry persisted, bool semanticMismatch, bool crossAssetStale)
			{
				var entry = new AssetEntry
				{
					Guid = persisted.Guid ?? string.Empty,
					Path = persisted.Path ?? string.Empty,
					Name = persisted.Name ?? string.Empty,
					Directory = persisted.Directory ?? string.Empty,
					Extension = persisted.Extension ?? string.Empty,
					Category = (AssetCategory)persisted.Category,
					TypeLabel = persisted.TypeLabel ?? string.Empty,
					FileSize = persisted.FileSize,
					RuntimeSize = persisted.RuntimeSize
				};

				if (persisted.Fields != null)
				{
					for (int i = 0; i < persisted.Fields.Count; i++)
					{
						var field = persisted.Fields[i];
						if (field == null || string.IsNullOrEmpty(field.Key)) continue;
						switch (field.Kind)
						{
							case 1: entry.Set(field.Key, field.LongValue); break;
							case 2: entry.Set(field.Key, field.DoubleValue); break;
							default: entry.Set(field.Key, field.StringValue); break;
						}
					}
				}

				if (persisted.Issues != null)
				{
					for (int i = 0; i < persisted.Issues.Count; i++)
					{
						var savedIssue = persisted.Issues[i];
						if (savedIssue == null || !RuleCatalog.Exists(savedIssue.RuleId)) continue;
						entry.Issues.Add(RestoreIssueSnapshot(savedIssue, entry.Path, entry.Category, entry.Issues.Count, semanticMismatch || crossAssetStale && ScanResult.IsCrossAssetCategory(entry.Category) || crossAssetStale && ScanResult.IsCrossAssetRule(savedIssue.RuleId), semanticMismatch));
					}
				}

				return entry;
			}
		}

		static AssetEntry PickMatching(List<AssetEntry> candidates, AssetCategory category)
		{
			if (candidates == null || candidates.Count == 0) return null;
			foreach (var candidate in candidates)
				if (candidate.Category == category) return candidate;
			return candidates[0];
		}

		static bool HasRule(AssetEntry entry, string ruleId)
		{
			foreach (var issue in entry.Issues)
				if (issue.RuleId == ruleId) return true;
			return false;
		}

		static AssetEntry RestoreCrossAssetEntry(PersistedEntry persisted)
		{
			var entry = new AssetEntry
			{
				Guid = persisted.Guid ?? string.Empty,
				Path = persisted.Path ?? string.Empty,
				Name = persisted.Name ?? string.Empty,
				Directory = persisted.Directory ?? string.Empty,
				Extension = persisted.Extension ?? string.Empty,
				Category = (AssetCategory)persisted.Category,
				TypeLabel = persisted.TypeLabel ?? string.Empty,
				FileSize = persisted.FileSize,
				RuntimeSize = persisted.RuntimeSize
			};

			if (persisted.Fields != null)
			{
				foreach (var field in persisted.Fields)
				{
					if (string.IsNullOrEmpty(field.Key)) continue;
					switch (field.Kind)
					{
						case 1: entry.Set(field.Key, field.LongValue); break;
						case 2: entry.Set(field.Key, field.DoubleValue); break;
						default: entry.Set(field.Key, field.StringValue); break;
					}
				}
			}

			if (persisted.Issues != null)
			{
				foreach (var savedIssue in persisted.Issues)
				{
					if (!RuleCatalog.Exists(savedIssue.RuleId)) continue;
					entry.Issues.Add(RestoreIssue(savedIssue, entry.Path, entry.Category, entry.Issues.Count));
				}
			}

			return entry;
		}

		/// <summary>
		/// Rebuilds one cross-asset issue from persisted data. Every caller of this immediately either
		/// is building a row that gets no live analysis at all (Duplicate/Unused), or is about to have
		/// <see cref="ScanResult.InvalidateCrossAssetFindings"/> retire it - never shown as fixable in
		/// between - so Fix/Verify/Options are deliberately left unset rather than approximated: nothing
		/// here can make them real.
		/// </summary>
		static OptimizationIssue RestoreIssue(PersistedIssue persisted, string path, AssetCategory category, int index)
		{
			var def = RuleCatalog.Get(persisted.RuleId);
			var issue = new OptimizationIssue
			{
				IssueInstanceId = (path ?? string.Empty) + "::" + persisted.RuleId + "::" + index,
				RuleId = persisted.RuleId,
				// Title/Why/FixHint are read from the current rule catalog rather than carried on the
				// wire: they are static per rule id, and re-reading them means a restored finding shows
				// this build's copy rather than a frozen one from before it.
				Title = def.Title,
				Why = def.Why,
				FixHint = def.Fix,
				Severity = (IssueSeverity)persisted.Severity,
				Category = category,
				Current = persisted.Current,
				Recommended = persisted.Recommended,
				EstimatedSavings = persisted.EstimatedSavings,
				Basis = (SavingsBasis)persisted.Basis,
				Confidence = (OptimizationConfidence)persisted.Confidence,
				SourceLine = persisted.SourceLine,
				QualityLevelIndex = persisted.QualityLevelIndex,
				QualityLevelName = persisted.QualityLevelName,
				TargetPlatform = persisted.TargetPlatform,
				ImporterScope = persisted.ImporterScope
			};

			if (persisted.Details != null)
			{
				foreach (var detail in persisted.Details)
				{
					issue.Details.Add(new IssueDetail
					{
						Label = detail.Label,
						Text = detail.Text,
						Kind = (DetailKind)detail.Kind,
						AssetPath = detail.AssetPath
					});
				}
			}
			return issue;
		}

		static bool IsCrossAssetEvidenceFresh(PersistedScanResult snapshot)
		{
			if (snapshot == null || !snapshot.CrossAssetEvidenceComplete) return false;
			long currentJournal = AssetDatabaseThreadSafeUtility.JournalGeneration;
			if (snapshot.CrossAssetEvidenceJournalGeneration < 0 || currentJournal < snapshot.CrossAssetEvidenceJournalGeneration) return false;
			if (currentJournal != snapshot.CrossAssetEvidenceJournalGeneration) return false;
			if (string.IsNullOrEmpty(snapshot.CrossAssetEvidenceScopeFingerprint)) return false;
			if (string.IsNullOrEmpty(snapshot.CrossAssetEvidenceContentIdentity)) return false;
			string currentScope = EvidenceContract.ComputeProjectAssetScopeFingerprint();
			if (string.IsNullOrEmpty(currentScope) || !string.Equals(currentScope, snapshot.CrossAssetEvidenceScopeFingerprint, StringComparison.Ordinal)) return false;
			string currentContent = EvidenceContract.ComputeProjectAssetIdentity();
			if (string.IsNullOrEmpty(currentContent) || !string.Equals(currentContent, snapshot.CrossAssetEvidenceContentIdentity, StringComparison.Ordinal)) return false;
			return true;
		}

		static PersistedScanResult LoadValidSnapshot()
		{
			PersistedScanResult snapshot;
			try
			{
				if (!File.Exists(k_Path)) return null;
				snapshot = JsonUtility.FromJson<PersistedScanResult>(File.ReadAllText(k_Path));
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read the scan cache, discarding it: " + e.Message);
				Clear();
				return null;
			}

			if (snapshot == null)
			{
				Clear();
				return null;
			}

			// Schema/Unity-version validation, the same axes ScanCache fingerprints its own rows
			// against: a mismatch on either means the shape or the meaning of what is on disk may no
			// longer match what this build expects, so the whole snapshot is discarded rather than
			// partially reinterpreted. Rule removal is handled separately, per issue, on restore - it
			// does not need to invalidate the rest of the snapshot.
			if (snapshot.Version != k_Version
				|| snapshot.ToolSchema != OptimizerVersion.Schema
				|| snapshot.UnityVersion != Application.unityVersion)
			{
				Debug.Log(ProjectOptimizerConstants.LogPrefix + " The saved scan cache is from a different tool or Unity version; "
						  + "starting without a restored scan.");
				Clear();
				return null;
			}

			return snapshot;
		}

		/// <summary>Deletes the saved snapshot, if any. Best-effort, matching <see cref="ScanCache.Delete"/>.</summary>
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

using System;
using System.Collections.Generic;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	public enum EligibilityStatus
	{
		Eligible,
		AlreadyCorrect,
		Stale,
		Incomplete,
		Unsupported,
		ConflictingContext,
		NoMatchingRule,
		MissingAction
	}

	public sealed class ReviewActionDescriptor
	{
		public string RuleId;
		public string IssueInstanceId;
		public string ActionId = string.Empty;
		public string DisplayName;
		public string ProposedLabel;
		public FixKind FixKind;
		public bool Destructive;
		public bool BackupRequired;
		public bool HasVerifier;
		public FixRisk Risk;
		public string Explanation;
		public string Warning;
		public string TargetPlatform;
		public int QualityLevelIndex = -1;
		public string QualityLevelName;
		public string ImporterScope;
		public int EligibleCount;
		public int MatchingCount;
		public long EstimatedSavings;
		public SavingsBasis SavingsBasis = SavingsBasis.Unknown;
		public string IssueInstanceContextKey;
		public bool HasSingleContext => QualityLevelIndex >= 0 || !string.IsNullOrEmpty(TargetPlatform) || !string.IsNullOrEmpty(ImporterScope);
	}

	public sealed class ReviewEligibilityRow
	{
		public AssetEntry Entry;
		public OptimizationIssue Issue;
		public FixOption Option;
		public EligibilityStatus Status;
		public string Reason;
		public string CurrentValue;
		public string ContextKey;
		public Action<FixContext> Fix => Option?.Apply ?? Issue?.Fix;
		public Func<FixContext, bool> Verify => FixContract.EffectiveVerify(Issue, Option);
		public bool IsEligible => Status == EligibilityStatus.Eligible;

		public FixKind EffectiveFixKind => Issue?.FixKind ?? FixKind.None;
		public bool EffectiveDestructive => EffectiveRisk == FixRisk.Destructive;
		public bool EffectiveBackupRequired => FixContract.EffectiveBackupRequired(Issue, Option);
		public FixRisk EffectiveRisk => Option?.EffectiveRisk(Issue?.Risk ?? FixRisk.Unspecified) ??
			(Issue?.Risk != FixRisk.Unspecified ? Issue.Risk : FixRisk.Medium);
		public string ActionId => Option?.StableActionId(Issue?.RuleId) ?? ((Issue?.RuleId ?? "rule") + "_default");
	}

	public static class ReviewActionCatalog
	{
		public static List<ReviewActionDescriptor> DetectCommonActions(IReadOnlyList<AssetEntry> selection)
		{
			var byKey = new Dictionary<string, ReviewActionDescriptor>(StringComparer.Ordinal);
			if (selection == null) return new List<ReviewActionDescriptor>();

			foreach (var entry in selection)
			{
				if (entry == null || entry.Issues == null) continue;
				foreach (var issue in entry.Issues)
				{
					if (issue == null || issue.FixKind != FixKind.Review || issue.Stale || !FixContract.MeetsReviewConfidence(issue.Confidence)) continue;
					if (issue.Options.Count > 0)
					{
						foreach (var option in issue.Options)
							if (option?.Apply != null) Accumulate(byKey, issue, option);
					}
					else if (issue.Fix != null)
					{
						Accumulate(byKey, issue, null);
					}
				}
			}

			var list = new List<ReviewActionDescriptor>(byKey.Values);
			list.Sort((a, b) =>
			{
				int byCount = b.MatchingCount.CompareTo(a.MatchingCount);
				if (byCount != 0) return byCount;
				int byRule = string.Compare(a.RuleId, b.RuleId, StringComparison.Ordinal);
				return byRule != 0 ? byRule : string.Compare(a.ActionId, b.ActionId, StringComparison.Ordinal);
			});
			for (int i = 0; i < list.Count; i++) list[i].EstimatedSavings = EstimateSelectionSavings(list[i], selection);
			return list;
		}

		static void Accumulate(Dictionary<string, ReviewActionDescriptor> byKey, OptimizationIssue issue, FixOption option)
		{
			string actionId = option?.StableActionId(issue.RuleId) ?? issue.RuleId + "_default";
			string context = ContextKey(issue, option);
			string key = issue.RuleId + "::" + actionId + "::" + context;
			var kind = issue.FixKind;
			var effectiveRisk = option?.EffectiveRisk(issue.Risk) ?? (issue.Risk != FixRisk.Unspecified ? issue.Risk : FixRisk.Medium);
			var destructive = effectiveRisk == FixRisk.Destructive;
			var backup = FixContract.EffectiveBackupRequired(issue, option);
			if (kind != FixKind.Review || !FixContract.MeetsReviewConfidence(issue.Confidence)) return;
			var candidateVerify = FixContract.EffectiveVerify(issue, option);
			if (FixContract.RequiresPostconditionVerifier(effectiveRisk) && candidateVerify == null) return;

			if (!byKey.TryGetValue(key, out var descriptor))
			{
				descriptor = new ReviewActionDescriptor
				{
					RuleId = issue.RuleId,
					IssueInstanceId = issue.IssueInstanceId,
					ActionId = actionId,
					DisplayName = option != null ? issue.Title + " · " + option.Label : issue.Title,
					ProposedLabel = option?.Label ?? (string.IsNullOrEmpty(issue.Recommended) ? issue.Title : issue.Recommended),
					FixKind = kind,
					Destructive = destructive,
					BackupRequired = backup,
					HasVerifier = FixContract.EffectiveVerify(issue, option) != null,
					Risk = effectiveRisk,
					Explanation = issue.Why,
					Warning = option?.Warning,
					TargetPlatform = issue.TargetPlatform,
					QualityLevelIndex = issue.QualityLevelIndex,
					QualityLevelName = issue.QualityLevelName,
					ImporterScope = issue.ImporterScope,
					IssueInstanceContextKey = context,
					SavingsBasis = option?.Basis ?? issue.Basis
				};
				byKey[key] = descriptor;
			}

			descriptor.MatchingCount++;
			descriptor.HasVerifier = descriptor.HasVerifier && (FixContract.EffectiveVerify(issue, option) != null);
			descriptor.Destructive = descriptor.Destructive || destructive;
			descriptor.BackupRequired = descriptor.BackupRequired || backup;
			var actionRisk = effectiveRisk;
			if (actionRisk > descriptor.Risk) descriptor.Risk = actionRisk;
		}

		static long EstimateSelectionSavings(ReviewActionDescriptor descriptor, IReadOnlyList<AssetEntry> selection)
		{
			if (descriptor == null || selection == null) return 0L;
			long total = 0L;
			for (int i = 0; i < selection.Count; i++)
			{
				var entry = selection[i];
				if (entry == null || entry.Issues == null) continue;
				long memoryBase = Math.Max(0L, entry.RuntimeSize);
				long diskBase = Math.Max(0L, entry.FileSize);
				long memorySaved = 0L, diskSaved = 0L;
				for (int j = 0; j < entry.Issues.Count; j++)
				{
					var issue = entry.Issues[j];
					if (issue == null || issue.Stale || issue.RuleId != descriptor.RuleId || issue.FixKind != FixKind.Review) continue;
					if (!ContextMatches(descriptor, issue)) continue;
					if (issue.Options != null && issue.Options.Count > 0)
					{
						for (int k = 0; k < issue.Options.Count; k++)
						{
							var option = issue.Options[k];
							if (option == null || option.Apply == null || option.StableActionId(issue.RuleId) != descriptor.ActionId || ContextKey(issue, option) != descriptor.IssueInstanceContextKey) continue;
							total += FixContract.AddCappedSavings(ref memorySaved, ref diskSaved, memoryBase, diskBase, issue, option);
							break;
						}
					}
					else if (descriptor.ActionId == issue.RuleId + "_default")
						total += FixContract.AddCappedSavings(ref memorySaved, ref diskSaved, memoryBase, diskBase, issue, null);
				}
			}
			return Math.Max(0L, total);
		}

		public static List<ReviewEligibilityRow> Evaluate(ReviewActionDescriptor descriptor, IReadOnlyList<AssetEntry> selection)
		{
			var rows = new List<ReviewEligibilityRow>(selection?.Count ?? 0);
			if (descriptor == null || selection == null) return rows;
			foreach (var entry in selection) rows.Add(EvaluateOne(descriptor, entry));
			return rows;
		}

		// Compatibility overload for existing callers.
		public static List<ReviewEligibilityRow> Evaluate(string ruleId, string actionId, IReadOnlyList<AssetEntry> selection)
		{
			var descriptor = new ReviewActionDescriptor { RuleId = ruleId, ActionId = actionId ?? string.Empty, FixKind = FixKind.Review };
			return Evaluate(descriptor, selection);
		}

		static ReviewEligibilityRow EvaluateOne(ReviewActionDescriptor descriptor, AssetEntry entry)
		{
			var row = new ReviewEligibilityRow { Entry = entry };
			if (entry == null)
				return WithStatus(row, EligibilityStatus.NoMatchingRule, "Asset is no longer available in the current result.");

			OptimizationIssue match = null;
			FixOption matchOption = null;
			int matches = 0;

			foreach (var issue in entry.Issues)
			{
				if (issue == null || issue.RuleId != descriptor.RuleId || issue.Stale) continue;
				if (!ContextMatches(descriptor, issue)) continue;

				if (issue.Options.Count > 0)
				{
					for (int i = 0; i < issue.Options.Count; i++)
					{
						var candidate = issue.Options[i];
						if (candidate == null || candidate.Apply == null) continue;
						if (candidate.StableActionId(issue.RuleId) != descriptor.ActionId) continue;
						if (issue.FixKind != FixKind.Review) continue;
						match = issue;
						matchOption = candidate;
						matches++;
					}
				}
				else if (descriptor.ActionId == issue.RuleId + "_default" && issue.Fix != null
						 && issue.FixKind == FixKind.Review)
				{
					match = issue;
					matches++;
				}
			}

			if (matches == 0)
				return WithStatus(row, EligibilityStatus.NoMatchingRule, "No matching finding/action on this asset.");
			if (matches > 1)
				return WithStatus(row, EligibilityStatus.ConflictingContext, "Multiple findings provide the same action; rescan or resolve the target context.");

			row.Issue = match;
			row.Option = matchOption;
			row.CurrentValue = match.Current;
			row.ContextKey = ContextKey(match, matchOption);

			if (!FixContract.MeetsReviewConfidence(match.Confidence))
				return WithStatus(row, EligibilityStatus.Incomplete, "The finding is below the Review confidence floor.");

			var effectiveRisk = matchOption?.EffectiveRisk(match.Risk) ?? (match.Risk != FixRisk.Unspecified ? match.Risk : FixRisk.Medium);
			var candidateVerify = FixContract.EffectiveVerify(match, matchOption);
			if (FixContract.RequiresPostconditionVerifier(effectiveRisk) && candidateVerify == null)
				return WithStatus(row, EligibilityStatus.Incomplete, "High-risk Review actions require a postcondition verifier.");
			if (!UnityEditorUtility.CanEditPath(entry.Path, out var editReason))
				return WithStatus(row, EligibilityStatus.Incomplete, editReason);

			var kind = match.FixKind;
			if (kind != FixKind.Review)
				return WithStatus(row, EligibilityStatus.Unsupported, "The requested action is no longer a Review Fix.");

			var apply = matchOption?.Apply ?? match.Fix;
			if (apply == null)
				return WithStatus(row, EligibilityStatus.MissingAction, "The action is no longer executable.");

			var verify = FixContract.EffectiveVerify(match, matchOption);
			if (verify != null)
			{
				bool already;
				try
				{
					var probe = ProbeContext(entry.Path);
					already = verify(probe);
				}
				catch (Exception e)
				{
					return WithStatus(row, EligibilityStatus.Incomplete, "Could not verify current state safely: " + e.Message);
				}
				if (already)
					return WithStatus(row, EligibilityStatus.AlreadyCorrect, "Already in the target state.");
			}

			return WithStatus(row, EligibilityStatus.Eligible, null);
		}

		static bool ContextMatches(ReviewActionDescriptor descriptor, OptimizationIssue issue)
		{
			if (!string.IsNullOrEmpty(descriptor.TargetPlatform)
				&& !string.Equals(descriptor.TargetPlatform, issue.TargetPlatform, StringComparison.Ordinal)) return false;
			if (descriptor.QualityLevelIndex >= 0 && descriptor.QualityLevelIndex != issue.QualityLevelIndex) return false;
			if (!string.IsNullOrEmpty(descriptor.ImporterScope)
				&& !string.Equals(descriptor.ImporterScope, issue.ImporterScope, StringComparison.Ordinal)) return false;
			return true;
		}

		static ReviewEligibilityRow WithStatus(ReviewEligibilityRow row, EligibilityStatus status, string reason)
		{
			row.Status = status;
			row.Reason = reason;
			return row;
		}

		public static FixContext ProbeContext(string assetPath)
		{
			return new FixContext
			{
				AssetPath = assetPath,
				Importer = AssetImporter.GetAtPath(assetPath),
				Asset = AssetDatabase.LoadMainAssetAtPath(assetPath),
				BackupEnabled = false,
				IsVerifierProbe = true
			};
		}

		public static List<ContextGroup> GroupByContext(IEnumerable<ReviewEligibilityRow> eligible)
		{
			var groups = new List<ContextGroup>();
			var index = new Dictionary<string, int>(StringComparer.Ordinal);
			if (eligible == null) return groups;

			foreach (var row in eligible)
			{
				if (row == null || row.Issue == null) continue;
				string key = ContextKey(row.Issue, row.Option);
				if (!index.TryGetValue(key, out int i))
				{
					i = groups.Count;
					index[key] = i;
					groups.Add(new ContextGroup
					{
						Key = key,
						Label = ContextLabel(row.Issue),
						TargetPlatform = row.Issue.TargetPlatform,
						QualityLevelIndex = row.Issue.QualityLevelIndex,
						QualityLevelName = row.Issue.QualityLevelName,
						ImporterScope = row.Issue.ImporterScope
					});
				}
				groups[i].Rows.Add(row);
			}
			return groups;
		}

		static string ContextKey(OptimizationIssue issue, FixOption option)
		{
			return (issue?.TargetPlatform ?? "") + "#" + (issue?.QualityLevelIndex ?? -1)
				   + "#" + (issue?.ImporterScope ?? "");
		}

		static string ContextLabel(OptimizationIssue issue)
		{
			if (issue == null) return "Unknown context";
			if (string.IsNullOrEmpty(issue.TargetPlatform) && issue.QualityLevelIndex < 0 && string.IsNullOrEmpty(issue.ImporterScope))
				return "Default (no platform/quality scope)";
			var label = issue.TargetPlatform ?? "Default";
			if (issue.QualityLevelIndex >= 0)
				label += " · quality " + issue.QualityLevelIndex + (string.IsNullOrEmpty(issue.QualityLevelName) ? "" : " (" + issue.QualityLevelName + ")");
			if (!string.IsNullOrEmpty(issue.ImporterScope)) label += " · " + issue.ImporterScope;
			return label;
		}

		public sealed class ContextGroup
		{
			public string Key;
			public string Label;
			public string TargetPlatform;
			public int QualityLevelIndex = -1;
			public string QualityLevelName;
			public string ImporterScope;
			public readonly List<ReviewEligibilityRow> Rows = new List<ReviewEligibilityRow>();
		}
	}
}

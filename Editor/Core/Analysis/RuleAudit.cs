using System;
using System.Collections.Generic;
using System.Text;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Checks the rule catalogue and, when given a scan result, the concrete fix actions produced by
	/// the analyzers. The catalogue describes what a rule is allowed to offer; a scan issue contains
	/// the actual Fix delegate / Review options, so both levels need auditing.
	/// </summary>
	public static class RuleAudit
	{
		public sealed class Finding
		{
			public string RuleId;
			public string Problem;
			public bool IsError;
			public string AssetPath;
			public string ActionId;

			public override string ToString()
			{
				string location = string.Empty;
				if (!string.IsNullOrEmpty(AssetPath)) location += " @ " + AssetPath;
				if (!string.IsNullOrEmpty(ActionId)) location += " [" + ActionId + "]";
				return (IsError ? "ERROR   " : "WARNING ")
					   + (RuleId ?? "<none>").PadRight(8)
					   + location + " " + Problem;
			}
		}

		/// <summary>Runs structural checks over the registered rule catalogue only.</summary>
		public static List<Finding> Run()
		{
			return Run(null);
		}

		/// <summary>
		/// Runs both catalogue checks and concrete action checks over a completed scan. The issue
		/// audit is deliberately separate from registration: options are created by analyzers at
		/// scan time and cannot be inspected from <see cref="RuleDef"/> alone.
		/// </summary>
		public static List<Finding> Run(ScanResult scanResult)
		{
			var findings = AuditRules();
			if (scanResult != null) findings.AddRange(AuditIssues(scanResult.Entries));
			return findings;
		}

		static List<Finding> AuditRules()
		{
			var findings = new List<Finding>();
			var seen = new HashSet<string>(StringComparer.Ordinal);

			foreach (var rule in RuleCatalog.All)
			{
				if (rule == null)
				{
					findings.Add(new Finding { RuleId = "<null>", Problem = "catalogue contains a null rule", IsError = true });
					continue;
				}

				if (!seen.Add(rule.Id))
				{
					// RuleCatalog refuses duplicates at registration, so reaching this means two
					// definitions raced or the catalogue was mutated afterwards.
					findings.Add(new Finding { RuleId = rule.Id, Problem = "registered more than once", IsError = true });
					continue;
				}

				if (string.IsNullOrWhiteSpace(rule.Title))
					findings.Add(new Finding { RuleId = rule.Id, Problem = "has no title", IsError = true });

				if (string.IsNullOrWhiteSpace(rule.Why))
					findings.Add(new Finding { RuleId = rule.Id, Problem = "has no explanation", IsError = true });

				if (string.IsNullOrWhiteSpace(rule.Fix))
					findings.Add(new Finding { RuleId = rule.Id, Problem = "has no fix guidance", IsError = true });

				if (!IsWellFormedId(rule.Id))
				{
					findings.Add(new Finding
					{
						RuleId = rule.Id,
						Problem = "id is not three letters followed by three or four digits",
						IsError = true
					});
				}

				if (rule.FixKind == FixKind.Verified && rule.Confidence < RuleCatalog.MinimumForAutomaticFix)
				{
					findings.Add(new Finding
					{
						RuleId = rule.Id,
						Problem = "declares Verified at " + rule.Confidence + " confidence, below the "
								  + RuleCatalog.MinimumForAutomaticFix + " minimum",
						IsError = true
					});
				}

				if (rule.FixKind == FixKind.Verified && rule.RecommendationConfidence < RuleCatalog.MinimumForAutomaticFix)
				{
					findings.Add(new Finding { RuleId = rule.Id, Problem = "declares Verified but recommendation confidence is below the automatic-fix floor", IsError = true });
				}

				if (rule.FixKind == FixKind.Verified && string.IsNullOrWhiteSpace(rule.SemanticVersion))
				{
					findings.Add(new Finding { RuleId = rule.Id, Problem = "declares Verified without a semantic rule version", IsError = true });
				}

				if (rule.FixKind == FixKind.Review && rule.Confidence < RuleCatalog.MinimumForReviewFix)
				{
					findings.Add(new Finding
					{
						RuleId = rule.Id,
						Problem = "declares Review at " + rule.Confidence + " confidence, below the "
								  + RuleCatalog.MinimumForReviewFix + " minimum",
						IsError = true
					});
				}


				if (rule.Category == AssetCategory.ProjectSetting && rule.FixKind != FixKind.None && !rule.DefaultBackupRequired)
				{
					findings.Add(new Finding
					{ RuleId = rule.Id, Problem = "is a project-setting action but does not require a backup", IsError = true });
				}

				if ((rule.DefaultFixRisk == FixRisk.Destructive) && rule.FixKind == FixKind.None)
				{
					findings.Add(new Finding
					{
						RuleId = rule.Id,
						Problem = "is marked destructive but has neither a Verified nor a Review fix, which is meaningless",
						IsError = false
					});
				}

				// A Verified destructive fix runs without anyone looking, so it needs the strongest
				// floor there is. A Review destructive fix still needs a human to approve it every
				// time, so the Heuristic Review floor is enough.
				if (rule.FixKind == FixKind.Verified && rule.DefaultFixRisk >= FixRisk.High && !rule.DefaultBackupRequired)
			{
				findings.Add(new Finding
				{
					RuleId = rule.Id,
					Problem = "is a High-risk Verified action but does not require a backup",
					IsError = true
				});
			}

			if ((rule.DefaultFixRisk == FixRisk.Destructive) && rule.FixKind == FixKind.Verified && rule.Confidence < OptimizationConfidence.Proven)
				{
					findings.Add(new Finding
					{
						RuleId = rule.Id,
						Problem = "is a Verified destructive fix at only " + rule.Confidence
								  + " confidence, below the Proven floor",
						IsError = true
					});
				}

				if ((rule.DefaultFixRisk == FixRisk.Destructive) && !rule.DefaultBackupRequired)
				{
					findings.Add(new Finding
					{
						RuleId = rule.Id,
						Problem = "is destructive but its default action does not declare backup required",
						IsError = true
					});
				}

			}

			findings.AddRange(CheckIdOrdering());
			var settings = OptimizerSettings.Instance;
			var suppressionKeys = new HashSet<string>(StringComparer.Ordinal);
			if (settings.suppressions != null)
				for (int i = 0; i < settings.suppressions.Count; i++)
				{
					var suppression = settings.suppressions[i];
					if (suppression == null || string.IsNullOrWhiteSpace(suppression.RuleId) || string.IsNullOrWhiteSpace(suppression.AssetPath))
						findings.Add(new Finding { RuleId = suppression?.RuleId, AssetPath = suppression?.AssetPath, Problem = "suppression is missing a rule id or asset path", IsError = true });
					else if (!RuleCatalog.Exists(suppression.RuleId))
						findings.Add(new Finding { RuleId = suppression.RuleId, AssetPath = suppression.AssetPath, Problem = "suppression references an unknown rule", IsError = true });
					else if (!suppressionKeys.Add(suppression.Key))
						findings.Add(new Finding { RuleId = suppression.RuleId, AssetPath = suppression.AssetPath, Problem = "duplicate suppression", IsError = true });
				}
			return findings;
		}

		/// <summary>
		/// Audits the concrete actions attached to scanned issues. The same action id is expected to
		/// appear on many assets, so uniqueness is checked within one issue, not globally.
		/// </summary>
		public static List<Finding> AuditIssues(IEnumerable<AssetEntry> entries)
		{
			var findings = new List<Finding>();
			if (entries == null) return findings;

			// Avoid printing the same malformed declaration once per asset in a large scan.
			var emitted = new HashSet<string>(StringComparer.Ordinal);

			foreach (var entry in entries)
			{
				if (entry == null || entry.Issues == null) continue;

				foreach (var issue in entry.Issues)
				{
					if (issue == null) continue;
					AuditIssue(issue, entry.Path, findings, emitted);
				}
			}

			return findings;
		}

		static void AuditIssue(OptimizationIssue issue, string assetPath,
			List<Finding> findings, HashSet<string> emitted)
		{
			if (issue.Stale)
			{
				if (issue.Fix != null || issue.Verify != null || (issue.Options != null && issue.Options.Count > 0) || issue.FixKind != FixKind.None)
				{
					AddIssueFinding(findings, emitted, issue, assetPath, null,
						"is marked stale but still carries an executable/verifiable action", true);
				}
				return;
			}

			bool hasDefaultFix = issue.Fix != null;
			bool hasOptions = issue.Options != null && issue.Options.Count > 0;

			if (issue.FixKind == FixKind.Verified && issue.Confidence < RuleCatalog.MinimumForAutomaticFix)
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is Verified at " + issue.Confidence + " confidence, below the " + RuleCatalog.MinimumForAutomaticFix + " automatic-fix floor", true);

			if (issue.FixKind == FixKind.Review && issue.Confidence < RuleCatalog.MinimumForReviewFix)
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is a Review action at " + issue.Confidence + " confidence, below the " + RuleCatalog.MinimumForReviewFix + " review floor", true);

			if (issue.FixKind == FixKind.Verified && issue.RecommendationConfidence < RuleCatalog.MinimumForAutomaticFix)
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is Verified but recommendation confidence is " + issue.RecommendationConfidence + ", below the automatic-fix floor", true);

			if (issue.FixKind == FixKind.Verified && issue.RequiredEvidence != EvidenceRequirement.None)
			{
				if (issue.EvidenceState != EvidenceState.Fresh)
					AddIssueFinding(findings, emitted, issue, assetPath, null,
						"is Verified but required evidence is " + issue.EvidenceState + ", not Fresh", true);
				if (string.IsNullOrWhiteSpace(issue.EvidenceSignature))
					AddIssueFinding(findings, emitted, issue, assetPath, null,
						"is Verified but has no required evidence signature", true);
			}

			if (issue.FixKind == FixKind.Verified)
			{
				string currentCatalog = RuleCatalog.SemanticSignature;
				if (!string.IsNullOrEmpty(issue.RuleCatalogSignature) && !string.Equals(issue.RuleCatalogSignature, currentCatalog, StringComparison.Ordinal))
					AddIssueFinding(findings, emitted, issue, assetPath, null, "has a stale rule-catalog semantic signature", true);
				if (!string.Equals(issue.AnalyzerSignature, EvidenceContract.AnalyzerSignature, StringComparison.Ordinal) || !string.Equals(issue.FixContractSignature, EvidenceContract.FixSignature, StringComparison.Ordinal))
					AddIssueFinding(findings, emitted, issue, assetPath, null, "has stale analyzer/fix semantic signatures", true);
				if (!FixContract.CanExecute(FixExecutionMode.VerifiedSingle, issue, null, out var contractReason))
					AddIssueFinding(findings, emitted, issue, assetPath, null, "fails the current automatic-fix contract: " + contractReason, true);
			}

			if (issue.FixKind == FixKind.Verified && issue.Risk >= FixRisk.High && !issue.BackupRequired)
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is a High-risk Verified action but does not require a backup", true);

			if (issue.FixKind == FixKind.None && (hasDefaultFix || hasOptions || issue.Verify != null))
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"has fix/verify data while its concrete FixKind is None", true);
			}


			if (issue.FixKind == FixKind.Verified && !hasDefaultFix && !hasOptions)
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is Verified but has no executable action", true);
			}

			// Fix All does not choose Review options. A Verified issue therefore needs a concrete
			// default Fix delegate; an option-only Verified issue would advertise a Fix All action
			// that the engine cannot actually execute.
			if (issue.FixKind == FixKind.Verified && !hasDefaultFix && hasOptions)
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is Verified with options but no default Fix delegate; Fix All cannot execute an option-only Verified action",
					true);
			}

			if (issue.FixKind == FixKind.Verified && hasDefaultFix && issue.Verify == null)
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is Verified with an executable default Fix but has no verifier; automatic execution cannot be post-validated",
					true);
			}

			if (issue.FixKind == FixKind.Review && hasDefaultFix && hasOptions)
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"has both a default Fix delegate and Review options; the default action is hidden by the option list",
					false);
			}

			if (issue.Destructive && !issue.BackupRequired)
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"is destructive but does not declare backup required", true);
			}

			if (issue.EstimatedSavings < 0)
			{
				AddIssueFinding(findings, emitted, issue, assetPath, null,
					"has a negative estimated saving", true);
			}

			if (!hasOptions) return;

			var ids = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < issue.Options.Count; i++)
			{
				var option = issue.Options[i];
				if (option == null)
				{
					AddIssueFinding(findings, emitted, issue, assetPath, "<null>",
						"contains a null Review option", true);
					continue;
				}

				string actionId = option.StableActionId(issue.RuleId);
				if (string.IsNullOrWhiteSpace(actionId))
				{
					AddIssueFinding(findings, emitted, issue, assetPath, "<empty>",
						"has a Review option with an empty ActionId", true);
					continue;
				}

				if (!ids.Add(actionId))
				{
					AddIssueFinding(findings, emitted, issue, assetPath, actionId,
						"contains duplicate options with the same ActionId", true);
				}

				if (option.EffectiveRisk(issue.Risk) >= FixRisk.High && FixContract.EffectiveVerify(issue, option) == null)
					AddIssueFinding(findings, emitted, issue, assetPath, actionId,
						"has a High-risk Review option without an option-specific verifier; execution is blocked until a meaningful postcondition can be checked", true);

				if (option.Apply == null)
				{
					AddIssueFinding(findings, emitted, issue, assetPath, actionId,
						"has no executable Apply delegate", true);
				}

				bool destructive = option.EffectiveRisk(issue.Risk) == FixRisk.Destructive;
				if (destructive && option.BackupRequiredOverride.HasValue && !option.BackupRequiredOverride.Value)
				{
					AddIssueFinding(findings, emitted, issue, assetPath, actionId,
						"explicitly disables backup for a destructive action", true);
				}

				if (option.EstimatedSavings < 0)
				{
					AddIssueFinding(findings, emitted, issue, assetPath, actionId,
						"has a negative estimated saving", true);
				}
			}
		}

		static void AddIssueFinding(List<Finding> findings, HashSet<string> emitted,
			OptimizationIssue issue, string assetPath, string actionId, string problem, bool isError)
		{
			string key = (issue.RuleId ?? "<none>") + "|" + (actionId ?? string.Empty) + "|" + problem;
			if (!emitted.Add(key)) return;

			findings.Add(new Finding
			{
				RuleId = issue.RuleId,
				AssetPath = assetPath,
				ActionId = actionId,
				Problem = problem,
				IsError = isError
			});
		}

		/// <summary>
		/// Reports gaps and out-of-order ids per prefix. A gap is normally deliberate; ids are never
		/// renumbered because they are persisted configuration keys.
		/// </summary>
		static List<Finding> CheckIdOrdering()
		{
			var findings = new List<Finding>();
			var byPrefix = new Dictionary<string, List<int>>(StringComparer.Ordinal);

			foreach (var rule in RuleCatalog.All)
			{
				if (rule == null || !IsWellFormedId(rule.Id)) continue;

				var prefix = rule.Id.Substring(0, 3);
				if (!int.TryParse(rule.Id.Substring(3), out int number)) continue;

				if (!byPrefix.TryGetValue(prefix, out var numbers))
				{
					numbers = new List<int>();
					byPrefix[prefix] = numbers;
				}
				numbers.Add(number);
			}

			foreach (var pair in byPrefix)
			{
				var numbers = pair.Value;
				var sorted = new List<int>(numbers);
				sorted.Sort();

				var gaps = new List<int>();
				for (int i = 1; i < sorted.Count; i++)
					for (int missing = sorted[i - 1] + 1; missing < sorted[i]; missing++)
						gaps.Add(missing);

				if (gaps.Count > 0)
				{
					var names = new List<string>();
					foreach (var gap in gaps) names.Add(pair.Key + gap.ToString("000"));
					findings.Add(new Finding
					{
						RuleId = pair.Key,
						Problem = "unused id(s): " + string.Join(", ", names.ToArray())
								  + " - withdrawn rules, or a numbering slip. Ids are never reused.",
						IsError = false
					});
				}

				bool inOrder = true;
				for (int i = 1; i < numbers.Count; i++)
					if (numbers[i] < numbers[i - 1]) inOrder = false;

				if (!inOrder)
				{
					findings.Add(new Finding
					{
						RuleId = pair.Key,
						Problem = "ids are registered out of numeric order, which makes the catalogue harder to read",
						IsError = false
					});
				}
			}

			return findings;
		}

		static bool IsWellFormedId(string id)
		{
			if (string.IsNullOrEmpty(id) || (id.Length != 6 && id.Length != 7)) return false;
			for (int i = 0; i < 3; i++) if (!char.IsUpper(id[i])) return false;
			for (int i = 3; i < id.Length; i++) if (!char.IsDigit(id[i])) return false;
			return true;
		}

		/// <summary>
		/// The full inventory: every rule with its analyzer category, severity, confidence, FixKind,
		/// and default action safety metadata.
		/// </summary>
		public static string Inventory()
		{
			var sb = new StringBuilder();
			sb.AppendLine("Rule      Category        Severity  Confidence      FixKind   Risk          Backup  Destructive");
			sb.AppendLine(new string('-', 98));

			foreach (var rule in RuleCatalog.All)
			{
				sb.Append(rule.Id.PadRight(10))
				  .Append(rule.Category.ToString().PadRight(16))
				  .Append(DisplayUtility.SeverityLabel(rule.DefaultSeverity).PadRight(10))
				  .Append(rule.Confidence.ToString().PadRight(16))
				  .Append(rule.FixKind.ToString().PadRight(10))
				  .Append((rule.DefaultFixRisk == FixRisk.Unspecified ? "-" : rule.DefaultFixRisk.ToString()).PadRight(14))
				  .Append((rule.DefaultBackupRequired ? "yes" : "-").PadRight(8))
				  .AppendLine((rule.DefaultFixRisk == FixRisk.Destructive) ? "yes" : "-");
			}

			sb.AppendLine();
			sb.AppendLine(RuleCatalog.All.Count + " rules registered.");
			return sb.ToString();
		}


	}
}

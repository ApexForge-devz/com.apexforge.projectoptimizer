using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// The specific confirmation a Review Fix requires - single-asset or, since pass 6, an explicit
	/// multi-asset Review Batch - built around a clear visual hierarchy (header / target / current /
	/// proposed / why / evidence / risk / verification / backup / actions) rather than a stack of
	/// all-caps prose paragraphs.
	/// <para>
	/// Both modes run through the exact same <see cref="FixEngine"/> transaction a Verified fix does -
	/// backup, apply, verify, rollback - via <see cref="FixExecutionMode.ReviewSingle"/> or
	/// <see cref="FixExecutionMode.ReviewBatch"/>. Nothing here re-implements any part of that
	/// machinery; this class only decides, before the first mutation, exactly which requests are safe
	/// to hand to it (see <see cref="ReviewActionCatalog"/>), and shows the result afterwards instead
	/// of closing blind.
	/// </para>
	/// </summary>
	public sealed class ReviewFixDialog : EditorWindow
	{
		// --- single-asset mode ---
		AssetEntry m_Entry;
		OptimizationIssue m_Issue;

		// --- batch mode ---
		List<AssetEntry> m_Selection;
		ReviewActionDescriptor m_Descriptor;
		List<ReviewEligibilityRow> m_AllRows;
		List<ReviewActionCatalog.ContextGroup> m_ContextGroups;
		int m_ChosenContextIndex;
		string m_BatchFilter = string.Empty;
		EligibilityStatus? m_BatchStatusFilter;

		bool IsBatch => m_Selection != null;

		Action<IReadOnlyList<string>> m_AfterFix;
		FixOption m_Selected;
		VisualElement m_Body;
		FixReport m_Result;
		bool m_Applying;

		const int k_PreviewRowCap = 200;

		// --- entry points ------------------------------------------------------

		/// <summary>One issue, one asset - the per-issue "Review Fix" button.</summary>
		public static void Open(AssetEntry entry, OptimizationIssue issue, Action<IReadOnlyList<string>> afterFix)
		{
			if (entry == null || issue == null || issue.Stale || issue.FixKind != FixKind.Review) return;

			var window = CreateInstance<ReviewFixDialog>();
			window.m_Entry = entry;
			window.m_Issue = issue;
			window.m_AfterFix = afterFix;
			window.m_Selected = PickDefault(issue.Options);
			window.Setup("Review Fix · " + issue.RuleId);
		}

		/// <summary>One explicitly chosen action, across an explicit multi-asset selection.</summary>
		public static void OpenBatch(IReadOnlyList<AssetEntry> selection, ReviewActionDescriptor descriptor,
			Action<IReadOnlyList<string>> afterFix)
		{
			if (selection == null || selection.Count == 0 || descriptor == null || descriptor.FixKind != FixKind.Review) return;

			var window = CreateInstance<ReviewFixDialog>();
			window.m_Selection = new List<AssetEntry>(selection);
			window.m_Descriptor = descriptor;
			window.m_AfterFix = afterFix;
			window.RecomputeBatch();
			window.Setup("Review Batch · " + descriptor.RuleId);
		}

		void Setup(string title)
		{
			titleContent = new GUIContent(title);
			minSize = new Vector2(560f, 480f);
			maxSize = new Vector2(1400f, 1400f);
			var size = IsBatch ? new Vector2(760f, 680f) : new Vector2(640f, 560f);
			position = new Rect(position.position, size);
			Build();
			ShowUtility();
		}

		static FixOption PickDefault(List<FixOption> options)
		{
			if (options == null || options.Count == 0) return null;
			foreach (var option in options) if (option.Recommended) return option;
			return options[0];
		}

		/// <summary>
		/// Re-derives every eligibility row and the target-context grouping from the assets' current
		/// state. Called once on open and again whenever the chosen context changes, so what the
		/// dialog shows is never older than the last user action - see P3.7 of the pass 6 TODO.
		/// </summary>
		void RecomputeBatch()
		{
			m_AllRows = ReviewActionCatalog.Evaluate(m_Descriptor, m_Selection);
			m_ContextGroups = ReviewActionCatalog.GroupByContext(m_AllRows.Where(r => r.IsEligible));
			if (m_ChosenContextIndex >= m_ContextGroups.Count) m_ChosenContextIndex = 0;
		}

		List<ReviewEligibilityRow> ChosenRows =>
			m_ContextGroups.Count > 0 ? m_ContextGroups[m_ChosenContextIndex].Rows : new List<ReviewEligibilityRow>();

		// --- shell --------------------------------------------------------------

		void Build()
		{
			var root = rootVisualElement;
			var sheet = LoadStylesheetByPath();
			if (sheet != null) root.styleSheets.Add(sheet);
			root.AddToClassList("po-root");
			root.AddToClassList("po-page");

			var scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			scroll.style.flexGrow = 1f;
			root.Add(scroll);
			m_Body = UiKit.Column("po-dialog");
			scroll.Add(m_Body);

			Refresh();
		}

		static StyleSheet LoadStylesheetByPath()
		{
			foreach (var guid in AssetDatabase.FindAssets("ProjectOptimizerStyles t:StyleSheet"))
			{
				var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
				if (sheet != null) return sheet;
			}
			return null;
		}

		void Refresh()
		{
			m_Body.Clear();

			if (m_Result != null)
			{
				BuildResultScreen();
				return;
			}

			BuildHeader();
			BuildTarget();
			BuildCurrentProposed();
			BuildDivider();
			BuildSection("Why", into => into.Add(Prose(RuleWhy)));

			if (IsBatch) BuildBatchEligibility();

			var evidence = BuildEvidenceSection();
			if (evidence != null) m_Body.Add(evidence);

			BuildSection("Risk", BuildRiskContent);

			if (!IsBatch && m_Issue.Options.Count > 1)
				BuildSection("Options", BuildOptionPicker);

			BuildSection("Verification", BuildVerificationContent);
			BuildSection("Backup", BuildBackupContent);

			if (IsBatch) BuildBatchPreview();

			BuildImpactSection();
			BuildActions();
		}

		// --- shared field accessors --------------------------------------------

		string RuleId => IsBatch ? m_Descriptor.RuleId : m_Issue.RuleId;
		FixRisk ActionRisk => IsBatch ? m_Descriptor.Risk : (m_Selected?.EffectiveRisk(m_Issue.Risk != FixRisk.Unspecified ? m_Issue.Risk : FixRisk.Medium) ?? (m_Issue.Risk != FixRisk.Unspecified ? m_Issue.Risk : FixRisk.Medium));
		bool Destructive => IsBatch ? m_Descriptor.Destructive : ActionRisk == FixRisk.Destructive;
		bool BackupRequired => IsBatch ? m_Descriptor.BackupRequired : FixContract.EffectiveBackupRequired(m_Issue, m_Selected);
		string RuleWhy => IsBatch ? m_Descriptor.Explanation : m_Issue.Why;
		string SelectedWarning => IsBatch ? m_Descriptor.Warning : m_Selected?.Warning;
		Func<FixContext, bool> SelectedVerify => IsBatch ? null : FixContract.EffectiveVerify(m_Issue, m_Selected);
		bool HasVerifier => IsBatch ? m_Descriptor.HasVerifier : SelectedVerify != null;
		string ProposedLabel => IsBatch ? m_Descriptor.ProposedLabel
			: (m_Selected?.Label ?? (string.IsNullOrEmpty(m_Issue.Recommended) ? "-" : m_Issue.Recommended));
		long EstimatedSavings => IsBatch ? m_Descriptor.EstimatedSavings : FixContract.EffectiveSavings(m_Issue, m_Selected);

		// --- header --------------------------------------------------------------

		void BuildHeader()
		{
			var header = UiKit.Column();
			var row = UiKit.Row("po-dialog-header");

			row.Add(UiKit.Pill(IsBatch ? "REVIEW BATCH" : "REVIEW FIX", "sev-warning"));
			if (Destructive)
			{
				var d = UiKit.Pill("DESTRUCTIVE", "sev-critical");
				d.style.marginLeft = 6f;
				row.Add(d);
			}
			var backupPill = UiKit.Pill(BackupRequired ? "BACKUP REQUIRED" : "NO BACKUP", BackupRequired ? "sev-info" : "sev-clean");
			backupPill.style.marginLeft = 6f;
			row.Add(backupPill);
			header.Add(row);

			var title = UiKit.Text(IsBatch ? m_Descriptor.DisplayName : m_Issue.Title, "po-dialog-title");
			title.style.marginTop = 6f;
			header.Add(title);

			var ruleLine = UiKit.Row();
			ruleLine.Add(UiKit.Text(RuleId, "po-issue-rule"));
			var def = RuleCatalog.Get(RuleId);
			if (def != null)
			{
				var confidence = UiKit.Text("·  Finding confidence: " + DisplayUtility.ConfidenceLabel(def.Confidence), "po-issue-rule");
				confidence.style.marginLeft = 8f;
				confidence.tooltip = DisplayUtility.ConfidenceExplanation(def.Confidence);
				ruleLine.Add(confidence);
			}
			header.Add(ruleLine);

			m_Body.Add(header);
		}

		// --- target --------------------------------------------------------------

		void BuildTarget()
		{
			m_Body.Add(BuildSectionTitle("Target"));

			if (!IsBatch)
			{
				var pathRow = UiKit.Row();
				pathRow.style.alignItems = Align.Center;
				var path = UiKit.Text(m_Entry.Path, "po-dialog-prose");
				path.style.flexShrink = 1;
				path.tooltip = "Click to select in the Project window.";
				path.RegisterCallback<ClickEvent>(_ => PingOrExplain(m_Entry.Path));
				pathRow.Add(path);

				var copy = UiKit.Button("Copy", () =>
				{
					EditorGUIUtility.systemCopyBuffer = m_Entry.Path;
					Feedback.Show("Path copied.");
				}, "po-button-small");
				copy.style.marginLeft = 6f;
				pathRow.Add(copy);
				m_Body.Add(pathRow);

				m_Body.Add(Prose(m_Entry.TypeLabel + ContextSuffix(m_Issue)));
			}
			else
			{
				int eligibleNow = ChosenRows.Count;
				m_Body.Add(Prose(eligibleNow + " of " + m_Selection.Count + " selected assets"));

				if (m_ContextGroups.Count > 1)
				{
					var warn = UiKit.Text("This action was measured under " + m_ContextGroups.Count
						+ " different platform/quality contexts. Choose exactly one to apply to - the others "
						+ "are left untouched by this batch.", "po-dialog-prose");
					warn.style.marginTop = 4f;
					warn.style.color = new Color(1f, 0.69f, 0.13f);
					m_Body.Add(warn);

					for (int i = 0; i < m_ContextGroups.Count; i++)
					{
						int captured = i;
						var group = m_ContextGroups[i];
						var pickRow = UiKit.Row();
						pickRow.style.alignItems = Align.Center;
						pickRow.style.marginTop = 4f;
						var radio = new Toggle { value = i == m_ChosenContextIndex };
						radio.style.marginRight = 6f;
						radio.RegisterValueChangedCallback(evt =>
						{
							if (!evt.newValue) return;
							m_ChosenContextIndex = captured;
							Refresh();
						});
						pickRow.Add(radio);
						pickRow.Add(UiKit.Text(group.Label + "  (" + group.Rows.Count + " eligible)", "po-dialog-prose"));
						m_Body.Add(pickRow);
					}
				}
				else if (m_ContextGroups.Count == 1 && (m_ContextGroups[0].Label != "Default (no platform/quality scope)"))
				{
					m_Body.Add(Prose("Context: " + m_ContextGroups[0].Label));
				}
			}
		}

		static string ContextSuffix(OptimizationIssue issue)
		{
			if (issue == null || (string.IsNullOrEmpty(issue.TargetPlatform) && issue.QualityLevelIndex < 0))
				return string.Empty;
			var text = "  ·  " + (issue.TargetPlatform ?? "Default");
			if (issue.QualityLevelIndex >= 0)
				text += ", quality " + issue.QualityLevelIndex
						+ (string.IsNullOrEmpty(issue.QualityLevelName) ? "" : " (" + issue.QualityLevelName + ")");
			return text;
		}

		// --- current / proposed ---------------------------------------------------

		void BuildCurrentProposed()
		{
			m_Body.Add(BuildSectionTitle("Change"));

			var pair = UiKit.Row("po-value-pair");

			var currentBox = UiKit.Column("po-value-box");
			currentBox.Add(UiKit.Text("CURRENT", "po-value-box-label"));
			currentBox.Add(UiKit.Text(CurrentValueSummary(), "po-value-box-value"));
			pair.Add(currentBox);

			pair.Add(UiKit.Text("→", "po-value-arrow"));

			var proposedBox = UiKit.Column("po-value-box", "po-value-proposed");
			proposedBox.Add(UiKit.Text(IsBatch || (m_Issue.Options.Count > 0) ? "SELECTED" : "PROPOSED", "po-value-box-label"));
			proposedBox.Add(UiKit.Text(ProposedLabel, "po-value-box-value"));
			pair.Add(proposedBox);

			m_Body.Add(pair);

			if (!IsBatch)
			{
				var desc = m_Selected?.ResultDescription;
				if (!string.IsNullOrEmpty(desc))
				{
					var d = Prose(desc);
					d.style.marginTop = 4f;
					m_Body.Add(d);
				}
			}
		}

		string CurrentValueSummary()
		{
			if (!IsBatch) return string.IsNullOrEmpty(m_Issue.Current) ? "-" : m_Issue.Current;

			// Mixed current values are common and fine (P0.8) - shown as a distribution rather than
			// one misleading value, counted over the rows this apply will actually touch.
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var row in ChosenRows)
			{
				var value = string.IsNullOrEmpty(row.CurrentValue) ? "(unknown)" : row.CurrentValue;
				counts.TryGetValue(value, out int c);
				counts[value] = c + 1;
			}
			if (counts.Count == 0) return "-";
			if (counts.Count == 1) return counts.Keys.First();

			var parts = counts.Select(p => p.Key + " (" + p.Value + ")").Take(4);
			return string.Join(",  ", parts) + (counts.Count > 4 ? ", …" : string.Empty);
		}

		// --- batch eligibility summary ----------------------------------------------

		void BuildBatchEligibility()
		{
			m_Body.Add(BuildSectionTitle("Selection breakdown"));

			var counts = new Dictionary<EligibilityStatus, int>();
			foreach (var row in m_AllRows)
			{
				counts.TryGetValue(row.Status, out int c);
				counts[row.Status] = c + 1;
			}

			var strip = UiKit.Row("po-eligibility-strip");
			strip.Add(EligibilityStat("Selected", m_Selection.Count.ToString(), null));
			strip.Add(EligibilityStat("Eligible", ChosenRows.Count.ToString(), "po-risk-low"));
			AddStatIfAny(strip, counts, EligibilityStatus.AlreadyCorrect, "Already correct");
			AddStatIfAny(strip, counts, EligibilityStatus.Stale, "Stale");
			AddStatIfAny(strip, counts, EligibilityStatus.Unsupported, "Unsupported");
			AddStatIfAny(strip, counts, EligibilityStatus.NoMatchingRule, "No matching rule");
			AddStatIfAny(strip, counts, EligibilityStatus.MissingAction, "Different option");
			int conflicting = m_AllRows.Count(r => r.IsEligible) - ChosenRows.Count;
			if (conflicting > 0) strip.Add(EligibilityStat("Other context", conflicting.ToString(), "po-risk-medium"));

			m_Body.Add(strip);
		}

		static void AddStatIfAny(VisualElement strip, Dictionary<EligibilityStatus, int> counts, EligibilityStatus status, string label)
		{
			if (counts.TryGetValue(status, out int c) && c > 0)
				strip.Add(EligibilityStat(label, c.ToString(), "po-risk-medium"));
		}

		static VisualElement EligibilityStat(string label, string value, string valueClass)
		{
			var column = UiKit.Column("po-eligibility-stat");
			var v = UiKit.Text(value, "po-eligibility-stat-value");
			if (!string.IsNullOrEmpty(valueClass)) v.AddToClassList(valueClass);
			column.Add(v);
			column.Add(UiKit.Text(label.ToUpperInvariant(), "po-eligibility-stat-label"));
			return column;
		}

		// --- evidence --------------------------------------------------------------

		VisualElement BuildEvidenceSection()
		{
			if (IsBatch) return null; // the eligibility breakdown above and the preview table below are this mode's evidence.
			if (m_Issue.Details.Count == 0) return null;

			var column = UiKit.Column("po-dialog-section");
			column.Add(BuildSectionTitle("Evidence"));

			var group = UiKit.Column("po-kv-group");
			int shown = Math.Min(m_Issue.Details.Count, 24);
			for (int i = 0; i < shown; i++)
			{
				var detail = m_Issue.Details[i];
				var row = UiKit.Row("po-kv-row");
				row.Add(UiKit.Text(string.IsNullOrEmpty(detail.Label) ? "—" : detail.Label, "po-kv-key"));
				var value = UiKit.Text(detail.Text, "po-kv-value");
				if (detail.Kind == DetailKind.Bad) value.style.color = new StyleColor(new Color(1f, 0.69f, 0.13f));
				else if (detail.Kind == DetailKind.Good) value.style.color = new StyleColor(new Color(0.25f, 0.81f, 0.56f));
				row.Add(value);
				group.Add(row);
			}
			if (m_Issue.Details.Count > shown)
				group.Add(Prose("… and " + (m_Issue.Details.Count - shown) + " more"));
			column.Add(group);
			return column;
		}

		// --- risk --------------------------------------------------------------

		void BuildRiskContent(VisualElement into)
		{
			var def = RuleCatalog.Get(RuleId);
			string riskLabel; string riskClass;
			bool hasWarning = !string.IsNullOrEmpty(SelectedWarning);
			int optionCount = IsBatch ? 0 : m_Issue.Options.Count;

			if (Destructive) { riskLabel = "Destructive"; riskClass = "po-risk-destructive"; }
			else if (hasWarning || optionCount > 1) { riskLabel = "Medium"; riskClass = "po-risk-medium"; }
			else { riskLabel = "Low"; riskClass = "po-risk-low"; }

			var row = UiKit.Row();
			var risk = UiKit.Text("Action risk: " + riskLabel, "po-dialog-prose");
			risk.AddToClassList(riskClass);
			risk.style.unityFontStyleAndWeight = FontStyle.Bold;
			row.Add(risk);
			into.Add(row);

			into.Add(Prose("Reason: runtime usage cannot be proven from imported asset data alone, "
				+ "which is exactly why this needs a human to approve it rather than running automatically."));

			if (hasWarning)
			{
				var warn = Prose(SelectedWarning);
				warn.style.color = new StyleColor(new Color(1f, 0.69f, 0.13f));
				warn.style.marginTop = 4f;
				into.Add(warn);
			}
		}

		// --- option picker (single mode only) ---------------------------------------

		void BuildOptionPicker(VisualElement into)
		{
			foreach (var option in m_Issue.Options)
			{
				var captured = option;
				bool selected = ReferenceEquals(m_Selected, option);

				var card = UiKit.Row("po-option-card");
				if (selected) card.AddToClassList("po-option-selected");

				var radio = new VisualElement();
				radio.AddToClassList("po-option-radio");
				if (selected) radio.AddToClassList("po-option-radio-on");
				card.Add(radio);

				var body = UiKit.Column("po-option-body");
				var titleRow = UiKit.Row("po-option-title-row");
				titleRow.Add(UiKit.Text(option.Label, "po-option-label"));
				if (option.Recommended)
				{
					var pill = UiKit.Pill("RECOMMENDED", "sev-clean");
					pill.style.marginLeft = 6f;
					titleRow.Add(pill);
				}
				body.Add(titleRow);
				if (!string.IsNullOrEmpty(option.ResultDescription))
					body.Add(UiKit.Text(option.ResultDescription, "po-option-desc"));
				if (!string.IsNullOrEmpty(option.Warning))
					body.Add(UiKit.Text(option.Warning, "po-option-warning"));
				card.Add(body);

				if (option.EstimatedSavings > 0)
				{
					var impact = UiKit.Column("po-option-impact");
					impact.Add(UiKit.Text(DisplayUtility.FormatBytes(option.EstimatedSavings), "po-option-impact-value"));
					card.Add(impact);
				}

				card.RegisterCallback<ClickEvent>(_ =>
				{
					m_Selected = captured;
					Refresh();
				});
				into.Add(card);
			}
		}

		// --- verification --------------------------------------------------------------

		void BuildVerificationContent(VisualElement into)
		{
			if (HasVerifier)
			{
				into.Add(VerifyRow("Re-reads the asset after the change is committed"));
				into.Add(VerifyRow("Confirms the result matches: " + ProposedLabel));
				if (IsBatch) into.Add(VerifyRow("Any asset that fails this check is rolled back individually"));
			}
			else
			{
				into.Add(Prose("Not available for this action. The result will be reported as unverified - "
					+ "applied on the strength of the API call alone, not confirmed afterwards."));
			}
		}

		static VisualElement VerifyRow(string text)
		{
			var row = UiKit.Row("po-verify-row");
			row.Add(UiKit.Text("✓", "po-verify-mark"));
			row.Add(UiKit.Text(text, "po-verify-text"));
			return row;
		}

		// --- backup --------------------------------------------------------------

		void BuildBackupContent(VisualElement into)
		{
			into.Add(Prose("Required. " + (IsBatch ? "Every affected asset" : "This asset")
				+ " (and anything else the fix touches) will be snapshotted before modification."));
			into.Add(Prose("The batch can be reverted afterwards from the Fix History toolbar."));
		}

		// --- batch preview table ------------------------------------------------------

		void BuildBatchPreview()
		{
			var column = UiKit.Column("po-dialog-section");
			column.Add(BuildSectionTitle("Preview"));

			var filterRow = UiKit.Row();
			filterRow.style.marginBottom = 4f;
			var search = new TextField { value = m_BatchFilter };
			search.AddToClassList("po-search");
			search.textEdition.placeholder = "Filter by path...";
			search.RegisterValueChangedCallback(evt => { m_BatchFilter = evt.newValue; Refresh(); });
			filterRow.Add(search);

			var statusChoices = new List<string> { "All", "Eligible", "Already correct", "Stale", "Unsupported", "No matching rule", "Different option" };
			var statusIndex = m_BatchStatusFilter switch
			{
				null => 1,
				EligibilityStatus.Eligible => 1,
				EligibilityStatus.AlreadyCorrect => 2,
				EligibilityStatus.Stale => 3,
				EligibilityStatus.Unsupported => 4,
				EligibilityStatus.NoMatchingRule => 5,
				EligibilityStatus.MissingAction => 6,
				_ => 1
			};
			var statusDropdown = new PopupField<string>(statusChoices, statusIndex);
			statusDropdown.AddToClassList("po-dropdown");
			statusDropdown.RegisterValueChangedCallback(evt =>
			{
				m_BatchStatusFilter = statusChoices.IndexOf(evt.newValue) switch
				{
					1 => EligibilityStatus.Eligible,
					2 => EligibilityStatus.AlreadyCorrect,
					3 => EligibilityStatus.Stale,
					4 => EligibilityStatus.Unsupported,
					5 => EligibilityStatus.NoMatchingRule,
					6 => EligibilityStatus.MissingAction,
					_ => (EligibilityStatus?)null
				};
				Refresh();
			});
			filterRow.Add(statusDropdown);
			column.Add(filterRow);

			IEnumerable<ReviewEligibilityRow> filtered = m_AllRows;
			if (!string.IsNullOrEmpty(m_BatchFilter))
				filtered = filtered.Where(r => r.Entry.Path.IndexOf(m_BatchFilter, StringComparison.OrdinalIgnoreCase) >= 0);
			if (m_BatchStatusFilter.HasValue)
				filtered = filtered.Where(r => r.Status == m_BatchStatusFilter.Value);

			var list = filtered.ToList();
			var table = UiKit.Column("po-kv-group");
			int shown = Math.Min(list.Count, k_PreviewRowCap);
			for (int i = 0; i < shown; i++) table.Add(BuildPreviewRow(list[i]));
			if (list.Count > shown)
				table.Add(Prose("+ " + (list.Count - shown) + " more not shown"));
			if (list.Count == 0)
				table.Add(Prose("Nothing matches this filter."));
			column.Add(table);

			m_Body.Add(column);
		}

		VisualElement BuildPreviewRow(ReviewEligibilityRow row)
		{
			var element = UiKit.Row("po-batch-row");
			var path = UiKit.Text(row.Entry.Name, "po-batch-row-path");
			path.tooltip = row.Entry.Path;
			element.Add(path);
			element.Add(UiKit.Text(string.IsNullOrEmpty(row.CurrentValue) ? "-" : row.CurrentValue, "po-batch-row-value"));
			element.Add(UiKit.Text(row.IsEligible ? ProposedLabel : "-", "po-batch-row-value"));

			var (statusText, statusClass) = StatusLabel(row.Status);
			var status = UiKit.Text(statusText, "po-batch-row-status");
			status.AddToClassList(statusClass);
			status.tooltip = row.Reason;
			element.Add(status);

			element.RegisterCallback<ClickEvent>(_ => PingOrExplain(row.Entry.Path));
			return element;
		}

		static (string, string) StatusLabel(EligibilityStatus status)
		{
			return status switch
			{
				EligibilityStatus.Eligible => ("Eligible", "po-risk-low"),
				EligibilityStatus.AlreadyCorrect => ("Already correct", "po-cell-dim"),
				EligibilityStatus.Stale => ("Stale", "po-risk-medium"),
				EligibilityStatus.Incomplete => ("Incomplete", "po-risk-medium"),
				EligibilityStatus.Unsupported => ("Unsupported", "po-cell-dim"),
				EligibilityStatus.ConflictingContext => ("Other context", "po-risk-medium"),
				EligibilityStatus.NoMatchingRule => ("No finding", "po-cell-dim"),
				EligibilityStatus.MissingAction => ("Different option", "po-cell-dim"),
				_ => ("-", "po-cell-dim")
			};
		}

		// --- impact --------------------------------------------------------------

		void BuildImpactSection()
		{
			long savings = EstimatedSavings;
			if (savings <= 0) return;
			m_Body.Add(BuildSectionTitle("Estimated impact"));
			m_Body.Add(Prose(DisplayUtility.FormatBytes(savings) + (IsBatch ? " across the eligible assets." : " on its own.")));
		}

		// --- actions --------------------------------------------------------------

		void BuildActions()
		{
			var actions = UiKit.Row();
			actions.style.marginTop = 16f;
			actions.Add(UiKit.Button("Cancel", Close, "po-button-small"));

			int eligibleCount = IsBatch ? ChosenRows.Count : 1;
			string label = IsBatch
				? "Apply to " + eligibleCount + " asset" + (eligibleCount == 1 ? "" : "s")
				: (Destructive && RuleId.StartsWith("UNU", StringComparison.Ordinal) ? "Apply and Delete" : "Apply Review Fix");

			var apply = UiKit.Button(label, ApplyAndShowResult, "po-button-primary", "po-button-small");
			apply.style.marginLeft = 8f;
			apply.SetEnabled(!m_Applying && eligibleCount > 0);
			actions.Add(apply);

			if (IsBatch && eligibleCount == 0)
			{
				var note = UiKit.Text("Nothing in this selection is currently eligible for this action.", "po-issue-rule");
				note.style.marginLeft = 8f;
				actions.Add(note);
			}
			m_Body.Add(actions);
		}

		// --- result screen --------------------------------------------------------------

		void BuildResultScreen()
		{
			var report = m_Result;
			var header = UiKit.Column();
			header.Add(UiKit.Text(IsBatch ? "Review Batch Complete" : "Review Fix Applied", "po-dialog-title"));
			m_Body.Add(header);

			var table = UiKit.Column("po-kv-group");
			table.style.marginTop = 10f;
			AddResultRow(table, "Applied and verified", report.AppliedVerified, "po-risk-low");
			AddResultRow(table, "Applied, unverified", report.AppliedUnverified, "po-risk-medium");
			AddResultRow(table, "Already correct (no-op)", report.NoOp, null);
			AddResultRow(table, "Failed", report.Failed, report.Failed > 0 ? "po-risk-high" : null);
			AddResultRow(table, "Rolled back", report.RolledBack, report.RolledBack > 0 ? "po-risk-medium" : null);
			AddResultRow(table, "Blocked (no backup)", report.Blocked, report.Blocked > 0 ? "po-risk-high" : null);
			if (IsBatch)
			{
				int skipped = m_AllRows.Count - m_AllRows.Count(r => r.IsEligible);
				AddResultRow(table, "Skipped (not eligible)", skipped, null);
			}
			m_Body.Add(table);

			m_Body.Add(BuildSectionTitle("Backup"));
			m_Body.Add(Prose(string.IsNullOrEmpty(report.BackupFolder)
				? "No backup folder was created (nothing was written)."
				: "Backup created. Folder: " + report.BackupFolder));

			if (report.Errors.Count > 0)
			{
				m_Body.Add(BuildSectionTitle("Details"));
				int shown = Math.Min(report.Errors.Count, 10);
				for (int i = 0; i < shown; i++) m_Body.Add(Prose(report.Errors[i]));
				if (report.Errors.Count > shown) m_Body.Add(Prose("… and " + (report.Errors.Count - shown) + " more - see the console."));
			}

			var actions = UiKit.Row();
			actions.style.marginTop = 16f;
			actions.Add(UiKit.Button("Open console", () => EditorApplication.ExecuteMenuItem("Window/General/Console"), "po-button-small"));
			if (FixEngine.CanRevert)
			{
				var undo = UiKit.Button("Undo this batch", () =>
				{
					var result = FixEngine.RevertLastBatch();
					if (result.Complete) Feedback.Success("Reverted " + result.Restored + " file(s).");
					else Feedback.Error("Only " + result.Restored + " of " + result.Expected + " file(s) restored.");
				}, "po-button-small");
				undo.style.marginLeft = 8f;
				actions.Add(undo);
			}
			var close = UiKit.Button("Close", Close, "po-button-primary", "po-button-small");
			close.style.marginLeft = 8f;
			actions.Add(close);
			m_Body.Add(actions);
		}

		static void AddResultRow(VisualElement into, string label, int count, string valueClass)
		{
			if (count == 0 && valueClass == null) return;
			var row = UiKit.Row("po-kv-row");
			row.Add(UiKit.Text(label, "po-kv-key"));
			var value = UiKit.Text(count.ToString(), "po-kv-value");
			if (!string.IsNullOrEmpty(valueClass) && count > 0) value.AddToClassList(valueClass);
			row.Add(value);
			into.Add(row);
		}

		// --- applying --------------------------------------------------------------

		void ApplyAndShowResult()
		{
			if (m_Applying) return;
			if (!IsBatch && (m_Issue == null || m_Issue.Stale || m_Issue.FixKind != FixKind.Review))
			{
				Feedback.Warn("This Review Fix is no longer current. Rescan before applying it.");
				Close();
				return;
			}
			m_Applying = true;

			List<FixRequest> requests;
			FixExecutionMode mode;

			if (IsBatch)
			{
				// Re-evaluate one last time immediately before the first mutation (P3.3 preflight):
				// the selection's state may have changed since the dialog opened.
				RecomputeBatch();
				requests = new List<FixRequest>(ChosenRows.Count);
				foreach (var row in ChosenRows)
				{
					requests.Add(new FixRequest
					{
						Entry = row.Entry,
						Issue = row.Issue,
						OptionOverride = row.Option
					});
				}
				mode = FixExecutionMode.ReviewBatch;
			}
			else
			{
				requests = new List<FixRequest>
				{
					new FixRequest
					{
						Entry = m_Entry,
						Issue = m_Issue,
						OptionOverride = m_Selected
					}
				};
				mode = FixExecutionMode.ReviewSingle;
			}

			if (requests.Count == 0)
			{
				m_Applying = false;
				Feedback.Warn("Nothing eligible to apply. The selection may have changed.");
				Refresh();
				return;
			}

			var report = FixEngine.Apply(requests, true, mode);
			CategoryPage.LogFixReport(report);

			var affected = new List<string>(report.TouchedPaths);
			affected.AddRange(report.RolledBackPaths);
			if (affected.Count > 0) m_AfterFix?.Invoke(affected);

			m_Applying = false;
			m_Result = report;
			Refresh();
		}

		// --- small helpers --------------------------------------------------------------

		void BuildDivider()
		{
			var divider = new VisualElement();
			divider.AddToClassList("po-dialog-divider");
			m_Body.Add(divider);
		}

		static Label BuildSectionTitle(string title)
		{
			var label = UiKit.Text(title, "po-dialog-section-title");
			label.style.marginTop = 12f;
			return label;
		}

		void BuildSection(string title, Action<VisualElement> content)
		{
			var column = UiKit.Column("po-dialog-section");
			column.Add(BuildSectionTitle(title));
			content(column);
			m_Body.Add(column);
		}

		static Label Prose(string text)
		{
			var label = UiKit.Text(text ?? string.Empty, "po-dialog-prose");
			return label;
		}

		static void PingOrExplain(string path)
		{
			if (UnityEditorUtility.PingPath(path)) return;
			Feedback.Warn("Could not open " + System.IO.Path.GetFileName(path)
						  + " - it is no longer in the project. Rescan to refresh this list.");
		}
	}
}

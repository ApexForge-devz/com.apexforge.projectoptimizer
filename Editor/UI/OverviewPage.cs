using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Landing page: headline numbers, where the issues live, and the biggest single wins.</summary>
	public sealed class OverviewPage : VisualElement
	{
		readonly Action<string> m_Navigate;
		readonly Action<IReadOnlyList<string>> m_AfterFix;
		readonly Action<ScanResult> m_OptimizeSafe;
		readonly ScrollView m_Scroll;

		public OverviewPage(Action<string> navigate, Action<IReadOnlyList<string>> afterFix, Action<ScanResult> optimizeSafe = null)
		{
			m_Navigate = navigate;
			m_AfterFix = afterFix;
			m_OptimizeSafe = optimizeSafe;

			AddToClassList("po-page");
			m_Scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			m_Scroll.AddToClassList("po-scroll");
			Add(m_Scroll);
		}

		public void SetData(ScanResult result, IReadOnlyList<TabDefinition> tabs)
		{
			m_Scroll.Clear();

			if (result == null)
			{
				var empty = UiKit.Column("po-hero", "po-hero-empty");
				empty.Add(UiKit.Text("PROJECT STATUS", "po-hero-kicker"));
				empty.Add(UiKit.Text("Ready for your first scan", "po-hero-title"));
				empty.Add(UiKit.Text(
					"Run a project scan to populate health, issue distribution, optimization opportunities and build insights.",
					"po-hero-subtitle"));
				empty.Add(UiKit.Text("No scan snapshot is loaded automatically. Use Load Scan Report to inspect the last saved report.", "po-hero-meta"));
				m_Scroll.Add(empty);
				return;
			}

			m_Scroll.Add(BuildHero(result));
			var plan = BuildOptimizationPlan(result);
			if (plan != null) m_Scroll.Add(plan);
			m_Scroll.Add(BuildTiles(result));
			var baseline = BuildBaseline(result);
			if (baseline != null) m_Scroll.Add(baseline);
			var trend = BuildTrend(result);
			if (trend != null) m_Scroll.Add(trend);
			var buildDelta = BuildBuildDelta();
			if (buildDelta != null) m_Scroll.Add(buildDelta);
			BuildScopeNote(result);
			m_Scroll.Add(UiKit.Text("Where the issues are", "po-section-title"));
			m_Scroll.Add(BuildCategoryBreakdown(result, tabs));
			m_Scroll.Add(UiKit.Text("Biggest single wins", "po-section-title"));
			m_Scroll.Add(BuildTopOpportunities(result));
			m_Scroll.Add(UiKit.Text("Disk footprint by category", "po-section-title"));
			m_Scroll.Add(BuildFootprint(result));
		}

		/// <summary>
		/// States what this scan did not look at, right beside the headline numbers. A scoped scan
		/// and a clean project produce the same small numbers, and only this tells them apart.
		/// </summary>
		void BuildScopeNote(ScanResult result)
		{
			var scope = result.Scope;
			bool scopeIncomplete = scope != null && scope.Skipped > 0;
			bool hasCaveats = result.CompletenessCaveats != null && result.CompletenessCaveats.Count > 0;
			bool hasLimitations = result.AnalysisLimitations != null && result.AnalysisLimitations.Count > 0;
			if (!scopeIncomplete && !hasCaveats && !hasLimitations) return;

			var card = UiKit.Column("po-card");
			card.style.paddingTop = 8f;
			card.style.paddingBottom = 8f;
			card.style.paddingLeft = 12f;
			card.style.paddingRight = 12f;

			card.Add(UiKit.Text("Evidence coverage", "po-rule-title"));
			card.Add(UiKit.Text(result.IsComplete ? "Static scan complete; some optional evidence sources have limitations." : "This scan is incomplete. Do not interpret missing coverage as proof that no optimization opportunity exists.", "po-rule-why"));
			if (scopeIncomplete && scope != null)
			{
				card.Add(UiKit.Text(scope.Summary() + ".", "po-rule-why"));
				if (scope.SkippedTooLarge > 0) card.Add(UiKit.Text("Assets over the size limit were never opened; they are excluded from definitive absence claims.", "po-rule-why"));
			}

			int shown = 0;
			if (hasCaveats)
			{
				for (int i = 0; i < result.CompletenessCaveats.Count && shown < 4; i++, shown++) card.Add(UiKit.Text("Caveat: " + result.CompletenessCaveats[i], "po-rule-why"));
			}
			if (hasLimitations)
			{
				for (int i = 0; i < result.AnalysisLimitations.Count && shown < 8; i++, shown++) card.Add(UiKit.Text("Limitation: " + result.AnalysisLimitations[i], "po-rule-why"));
			}

			if (scopeIncomplete) card.Add(UiKit.Button("Change what gets scanned", () => m_Navigate?.Invoke("scope"), "po-button-small"));
			m_Scroll.Add(card);
		}

		VisualElement BuildOptimizationPlan(ScanResult result)
		{
			int verified = 0, review = 0, diagnostics = 0;
			long verifiedSavings = 0;
			foreach (var entry in result.Entries)
			{
				var safePools = new Dictionary<SavingsBasis, long>();
				foreach (var issue in entry.Issues)
				{
					if (issue.AutoFixable)
					{
						verified++;
						if (issue.EstimatedSavings > 0 && issue.Basis != SavingsBasis.Unknown)
						{
							safePools.TryGetValue(issue.Basis, out long existing);
							if (issue.EstimatedSavings > existing) safePools[issue.Basis] = issue.EstimatedSavings;
						}
					}
					else if (issue.ReviewFixable) review++;
					else diagnostics++;
				}
				foreach (var pool in safePools) verifiedSavings += Math.Max(0L, pool.Value);
			}

			var card = UiKit.Column("po-card");
			card.Add(UiKit.Text("Optimization plan", "po-section-title"));
			card.Add(UiKit.Text("The tool has separated changes it can verify automatically from changes that still need your judgment. Run the safe batch once; you do not need to visit each category manually.", "po-rule-why"));

			var row = UiKit.Row();
			row.style.flexWrap = Wrap.Wrap;
			row.Add(BuildPlanStat("Safe fixes", verified.ToString("N0"), verified > 0 ? "sev-savings" : null));
			row.Add(BuildPlanStat("Review", review.ToString("N0"), review > 0 ? "sev-warning" : null));
			row.Add(BuildPlanStat("Diagnostics", diagnostics.ToString("N0"), diagnostics > 0 ? "sev-info" : null));
			if (verifiedSavings > 0) row.Add(BuildPlanStat("Estimated safe savings", DisplayUtility.FormatBytes(verifiedSavings), "sev-savings"));
			card.Add(row);

			var actions = UiKit.Row();
			if (verified > 0)
				actions.Add(UiKit.Button("Optimize safe fixes", () => m_OptimizeSafe?.Invoke(result), "po-button-primary", "po-button-small"));
			if (review > 0)
				actions.Add(UiKit.Button("Review remaining", () => m_Navigate?.Invoke(FindFirstReviewTab(result)), "po-button-small"));
			actions.Add(UiKit.Button("Rules & thresholds", () => m_Navigate?.Invoke("rules"), "po-button-small"));
			card.Add(actions);
			return card;
		}

		static VisualElement BuildPlanStat(string label, string value, string severityClass)
		{
			var stat = UiKit.Column();
			stat.style.marginRight = 28f;
			stat.Add(UiKit.Text(label.ToUpperInvariant(), "po-tile-label"));
			var text = UiKit.Text(value, "po-tile-value");
			if (!string.IsNullOrEmpty(severityClass)) text.AddToClassList(severityClass);
			stat.Add(text);
			return stat;
		}

		static string FindFirstReviewTab(ScanResult result)
		{
			if (result == null) return "overview";
			foreach (var entry in result.Entries)
				foreach (var issue in entry.Issues)
					if (!issue.AutoFixable && issue.ReviewFixable)
						return TabForCategory(issue.Category);
			return "overview";
		}

		static string TabForCategory(AssetCategory category)
		{
			switch (category)
			{
				case AssetCategory.Texture: return "textures";
				case AssetCategory.Audio: return "audio";
				case AssetCategory.Video: return "video";
				case AssetCategory.Model: return "models";
				case AssetCategory.Material:
				case AssetCategory.Shader: return "materials";
				case AssetCategory.Animation:
				case AssetCategory.Controller: return "controllers";
				case AssetCategory.Font:
				case AssetCategory.SpriteAtlas: return "atlases";
				case AssetCategory.Prefab: return "prefabs";
				case AssetCategory.Scene: return "scenes";
				case AssetCategory.Script: return "scripts";
				case AssetCategory.UIToolkit: return "uitoolkit";
				case AssetCategory.Rendering: return "rendering";
				case AssetCategory.Duplicate: return "duplicates";
				case AssetCategory.Unused: return "unused";
				case AssetCategory.ProjectSetting: return "project";
				case AssetCategory.Addressables: return "addressables";
				case AssetCategory.Build: return "build";
				case AssetCategory.BuildStep: return "buildsteps";
				case AssetCategory.BuildFile: return "buildfiles";
				case AssetCategory.Package: return "packages";
				default: return "overview";
			}
		}

		VisualElement BuildBaseline(ScanResult result)
		{
			var comparison = OptimizationBaselineStore.Compare(result);
			if (!comparison.HasBaseline)
			{
				var prompt = UiKit.Column("po-card");
				prompt.Add(UiKit.Text("Optimization baseline", "po-section-title"));
				prompt.Add(UiKit.Text("Save this complete scan as a deliberate before/after reference from the Export menu or command palette. Baselines are never replaced automatically.", "po-rule-why"));
				return prompt;
			}

			var card = UiKit.Column("po-card");
			card.style.paddingTop = 10f;
			card.style.paddingBottom = 8f;
			card.style.paddingLeft = 12f;
			card.style.paddingRight = 12f;
			card.Add(UiKit.Text("Optimization baseline", "po-section-title"));
			var baseline = OptimizationBaselineStore.Load();
			card.Add(UiKit.Text(OptimizationBaselineStore.Describe(baseline), "po-hero-meta"));
			card.Add(UiKit.Text(comparison.CompatibilityNote, "po-rule-why"));

			var row = UiKit.Row();
			row.style.flexWrap = Wrap.Wrap;
			long baselineIssues = baseline.CriticalCount + baseline.WarningCount + baseline.InfoCount;
			row.Add(BuildDelta("Issues", baselineIssues, result.IssueCount, lowerIsBetter: true));
			row.Add(BuildDelta("Critical", baseline.CriticalCount, result.CriticalCount, lowerIsBetter: true));
			row.Add(BuildDelta("Warnings", baseline.WarningCount, result.WarningCount, lowerIsBetter: true));
			row.Add(BuildDelta("File size", baseline.TotalFileSize, result.TotalFileSize, lowerIsBetter: true, isBytes: true));
			row.Add(BuildDelta("Memory opportunities", baseline.PotentialMemorySavings, result.PotentialMemorySavings, lowerIsBetter: true, isBytes: true));
			card.Add(row);

			if (comparison.RuleDeltas.Count > 0)
			{
				card.Add(UiKit.Text("Largest rule deltas", "po-rule-title"));
				int shown = Mathf.Min(8, comparison.RuleDeltas.Count);
				for (int i = 0; i < shown; i++)
				{
					var delta = comparison.RuleDeltas[i];
					var deltaRow = UiKit.Row("po-row");
					deltaRow.Add(UiKit.Text(delta.RuleId, "po-row-name"));
					deltaRow.Add(UiKit.Text(delta.Before + " → " + delta.After, "po-slot-value"));
					var amount = UiKit.Text((delta.Delta > 0 ? "▲ " : "▼ ") + Math.Abs(delta.Delta), "po-row-dim");
					amount.AddToClassList(delta.Delta < 0 ? "sev-savings" : "sev-warning");
					deltaRow.Add(amount);
					card.Add(deltaRow);
				}
			}
			else
				card.Add(UiKit.Empty("No rule-count changes", "The current scan matches the stored baseline by rule occurrence count."));
			return card;
		}

		/// <summary>
		/// Compares the live result against the most recent entry in <see cref="ScanHistoryStore"/>,
		/// which is always the scan *before* this one: a full scan records its own entry only after
		/// this method has already run against it (see ProjectOptimizerWindow.OnScanCompleted), and a
		/// post-fix refresh never records one at all. Returns null - showing nothing rather than a
		/// comparison against a scan that does not exist - the very first time the tool is ever run.
		/// </summary>
		VisualElement BuildBuildDelta()
		{
			var delta = BuildReportStore.CompareLatestToPrevious();
			if (delta == null || !delta.HasPrevious) return null;
			var card = UiKit.Column("po-card");
			card.Add(UiKit.Text("Latest build change", "po-section-title"));
			card.Add(UiKit.Text(delta.CompatibilityNote + ". This is the stored BuildReport comparison; it does not claim a runtime performance improvement.", "po-rule-why"));
			var row = UiKit.Row();
			row.style.flexWrap = Wrap.Wrap;
			row.Add(BuildDelta("Build size", 0, delta.SizeDelta, lowerIsBetter: true, isBytes: true, showAbsolute: true));
			row.Add(BuildDelta("Build time", 0, (long)Math.Round(delta.DurationDelta), lowerIsBetter: true, isBytes: false, showAbsolute: true, suffix: " s"));
			row.Add(BuildDelta("Warnings", 0, delta.WarningDelta, lowerIsBetter: true, isBytes: false, showAbsolute: true));
			row.Add(BuildDelta("Errors", 0, delta.ErrorDelta, lowerIsBetter: true, isBytes: false, showAbsolute: true));
			card.Add(row);
			return card;
		}

		VisualElement BuildTrend(ScanResult result)
		{
			var history = ScanHistoryStore.LoadEntries();
			if (history.Count == 0) return null;

			var previous = history[history.Count - 1];

			var card = UiKit.Column("po-card");
			card.style.paddingTop = 10f;
			card.style.paddingBottom = 6f;
			card.style.paddingLeft = 12f;
			card.style.paddingRight = 12f;

			var row = UiKit.Row();
			row.style.flexWrap = Wrap.Wrap;
			row.Add(BuildDelta("Critical", previous.CriticalCount, result.CriticalCount, lowerIsBetter: true));
			row.Add(BuildDelta("Warnings", previous.WarningCount, result.WarningCount, lowerIsBetter: true));
			row.Add(BuildDelta("Info", previous.InfoCount, result.InfoCount, lowerIsBetter: true));
			row.Add(BuildDelta("Memory recoverable", previous.PotentialMemorySavings, result.PotentialMemorySavings,
				lowerIsBetter: false, isBytes: true));
			card.Add(row);

			var when = new DateTime(previous.TimestampTicks);
			card.Add(UiKit.Text("Since the scan from " + when.ToString("g"), "po-tile-sub"));

			card.Add(BuildSparkline(history, result));
			return card;
		}

		/// <summary>One "was X, now Y" stat: an arrow and a magnitude, coloured by whether the change
		/// is the direction someone fixing issues wants.</summary>
		static VisualElement BuildDelta(string label, long from, long to, bool lowerIsBetter, bool isBytes = false, bool showAbsolute = false, string suffix = "")
		{
			var tile = UiKit.Column();
			tile.style.marginRight = 22f;
			tile.style.marginBottom = 6f;
			tile.Add(UiKit.Text(label.ToUpperInvariant(), "po-tile-label"));

			long delta = to - from;
			var value = UiKit.Text(string.Empty, "po-tile-value");
			value.style.fontSize = 16f;

			if (delta == 0)
			{
				value.text = "no change";
			}
			else
			{
				bool better = lowerIsBetter ? delta < 0 : delta > 0;
				string amount = isBytes ? DisplayUtility.FormatBytes(Math.Abs(delta)) : Math.Abs(delta).ToString("N0");
				amount += suffix;
				value.text = showAbsolute ? (delta > 0 ? "+" : "-") + amount : (delta > 0 ? "▲ " : "▼ ") + amount;
				// Reuses the tile's own severity-colour classes rather than inventing a third colour
				// language: green for progress, amber for regression, exactly what those classes
				// already mean everywhere else on this page.
				value.AddToClassList(better ? "sev-savings" : "sev-warning");
			}
			tile.Add(value);
			return tile;
		}

		/// <summary>
		/// A small bar-per-scan history of total issue count, most recent (this scan) last and picked
		/// out in the accent colour. Plain VisualElements with explicit heights rather than a custom
		/// Painter2D chart - simpler, and nothing here needs to auto-size from content.
		/// </summary>
		static VisualElement BuildSparkline(List<ScanHistoryEntry> history, ScanResult current)
		{
			const int maxBars = 12;
			var values = new List<int>();
			int start = Mathf.Max(0, history.Count - (maxBars - 1));
			for (int i = start; i < history.Count; i++)
				values.Add(history[i].IssueCount);
			values.Add(current.IssueCount);

			int max = 1;
			foreach (var v in values) max = Mathf.Max(max, v);

			var row = UiKit.Row();
			row.style.alignItems = Align.FlexEnd;
			row.style.height = 34f;
			row.style.marginTop = 8f;
			row.style.marginBottom = 2f;

			for (int i = 0; i < values.Count; i++)
			{
				var bar = new VisualElement();
				bar.AddToClassList("po-spark-bar");
				if (i == values.Count - 1) bar.AddToClassList("po-spark-bar-current");
				bar.style.height = values[i] <= 0 ? 4f : Mathf.Max(4f, 30f * (values[i] / (float)max));
				bar.tooltip = values[i] + " issue(s)" + (i == values.Count - 1 ? " - this scan" : "");
				row.Add(bar);
			}
			return row;
		}

		VisualElement BuildHero(ScanResult result)
		{
			var hero = UiKit.Row("po-hero");
			var scoreColumn = UiKit.Column("po-hero-score");
			scoreColumn.Add(UiKit.Text("OPEN FINDINGS", "po-hero-kicker"));
			var scoreLabel = UiKit.Text(result.IssueCount.ToString("N0"), "po-health-score");
			scoreColumn.Add(scoreLabel);
			string state = result.Scope != null && result.Scope.Skipped > 0 ? "PARTIAL SCAN" : (result.IssueCount == 0 ? "NO FINDINGS" : "FINDINGS PRESENT");
			scoreColumn.Add(UiKit.Text(state, "po-health-state"));

			var right = UiKit.Column("po-hero-copy");
			right.Add(UiKit.Text(result.IssueCount == 0 ? "No findings in the scanned scope." : result.IssueCount + " finding(s) were detected in the scanned scope.", "po-hero-title"));
			right.Add(UiKit.Text(
				result.ScannedAssets.ToString("N0") + " assets analyzed in " + DisplayUtility.FormatDuration(result.DurationSeconds),
				"po-hero-subtitle"));

			var meta = UiKit.Row("po-hero-meta-row");
			meta.Add(UiKit.Meta("Scanned", result.ScannedAssets.ToString("N0")));
			meta.Add(UiKit.Meta("Issues", result.IssueCount.ToString("N0")));
			meta.Add(UiKit.Meta("Disk savings", DisplayUtility.FormatBytes(result.PotentialDiskSavings)));
			meta.Add(UiKit.Meta("Memory savings", DisplayUtility.FormatBytes(result.PotentialMemorySavings)));
			right.Add(meta);

			var status = UiKit.Row("po-hero-status");
			status.Add(UiKit.Pill(result.IssueCount == 0 ? "CLEAN" : "ACTIONABLE", result.IssueCount == 0 ? "sev-clean" : "sev-warning"));
			status.Add(UiKit.Text("Scan " + result.ScanTime.ToString("g"), "po-hero-status-text"));
			right.Add(status);

			hero.Add(scoreColumn);
			hero.Add(right);
			return hero;
		}

		VisualElement BuildTiles(ScanResult result)
		{
			var tiles = UiKit.Row("po-tiles");
			tiles.Add(UiKit.Tile("Assets scanned", result.ScannedAssets.ToString(),
				DisplayUtility.FormatDuration(result.DurationSeconds) + " scan", null));
			tiles.Add(UiKit.Tile("Critical", result.CriticalCount.ToString(),
				"fix these first", "sev-critical"));
			tiles.Add(UiKit.Tile("Warnings", result.WarningCount.ToString(),
				"worth reviewing", "sev-warning"));
			tiles.Add(UiKit.Tile("Info", result.InfoCount.ToString(),
				"opportunistic", "sev-info"));
			// Two tiles, not one: freeing memory and shrinking the download are different wins and
			// adding them produces a number that means nothing.
			tiles.Add(UiKit.Tile("Potential memory savings", DisplayUtility.FormatBytes(result.PotentialMemorySavings),
				"estimated across eligible findings", "sev-savings"));
			tiles.Add(UiKit.Tile("Potential disk savings", DisplayUtility.FormatBytes(result.PotentialDiskSavings),
				"estimated across eligible findings", "sev-savings"));
			tiles.Add(UiKit.Tile("On disk", DisplayUtility.FormatBytes(result.TotalFileSize),
				"scanned source assets", null));
			return tiles;
		}

		VisualElement BuildCategoryBreakdown(ScanResult result, IReadOnlyList<TabDefinition> tabs)
		{
			var card = UiKit.Column("po-card");
			int maxIssues = 1;
			var counts = new Dictionary<string, int[]>();

			foreach (var tab in tabs)
			{
				if (tab.Categories == null || tab.Categories.Length == 0) continue;
				var entries = result.For(tab.Categories);
				var byLevel = new int[3];
				foreach (var entry in entries)
					foreach (var issue in entry.Issues)
						byLevel[(int)issue.Severity]++;

				int total = byLevel[0] + byLevel[1] + byLevel[2];
				counts[tab.Id] = byLevel;
				maxIssues = Mathf.Max(maxIssues, total);
			}

			foreach (var tab in tabs)
			{
				if (!counts.TryGetValue(tab.Id, out var byLevel)) continue;
				int total = byLevel[0] + byLevel[1] + byLevel[2];

				var row = UiKit.Row("po-row");
				row.Add(UiKit.Text(tab.Title, "po-row-name"));

				string severityClass = byLevel[2] > 0 ? "sev-critical" : byLevel[1] > 0 ? "sev-warning" : "sev-info";
				row.Add(UiKit.Bar(total / (float)maxIssues, total > 0 ? severityClass : null));

				// One fixed slot per severity so the pills form real columns down the card.
				var counters = UiKit.Row("po-slot-counts");
				counters.Add(CountSlot(byLevel[2], "sev-critical"));
				counters.Add(CountSlot(byLevel[1], "sev-warning"));
				counters.Add(CountSlot(byLevel[0], "sev-info"));
				row.Add(counters);

				var id = tab.Id;
				var actions = UiKit.Row("po-slot-actions");
				actions.Add(UiKit.Button("Open", () => m_Navigate(id), "po-button-small"));
				row.Add(actions);
				card.Add(row);
			}
			return card;
		}

		/// <summary>A fixed-width cell holding a count pill, or nothing when the count is zero.</summary>
		static VisualElement CountSlot(int count, string severityClass)
		{
			var slot = UiKit.Column("po-slot-count");
			if (count > 0) slot.Add(UiKit.Pill(count.ToString(), severityClass));
			return slot;
		}

		VisualElement BuildTopOpportunities(ScanResult result)
		{
			var card = UiKit.Column("po-card");

			var pairs = new List<KeyValuePair<AssetEntry, OptimizationIssue>>();
			foreach (var entry in result.Entries)
				foreach (var issue in entry.Issues)
					pairs.Add(new KeyValuePair<AssetEntry, OptimizationIssue>(entry, issue));

			pairs.Sort((a, b) =>
			{
				int bySavings = b.Value.EstimatedSavings.CompareTo(a.Value.EstimatedSavings);
				if (bySavings != 0) return bySavings;
				return b.Value.Severity.CompareTo(a.Value.Severity);
			});

			int shown = Mathf.Min(pairs.Count, 12);
			if (shown == 0)
			{
				card.Add(UiKit.Empty("Nothing to fix", "Every enabled rule passed on every scanned asset."));
				return card;
			}

			for (int i = 0; i < shown; i++)
			{
				var entry = pairs[i].Key;
				var issue = pairs[i].Value;

				// Every part sits in its own fixed-width slot, so severity, title, path,
				// saving and the buttons all line up as columns regardless of text length.
				var row = UiKit.Row("po-row");

				var severity = UiKit.Row("po-slot-severity");
				severity.Add(UiKit.Pill(DisplayUtility.SeverityLabel(issue.Severity).ToUpperInvariant(),
					DisplayUtility.SeverityClass(issue.Severity)));
				row.Add(severity);

				row.Add(UiKit.Text(issue.Title, "po-slot-title"));
				row.Add(UiKit.Text(entry.Path, "po-slot-path"));

				var saving = UiKit.Text(issue.EstimatedSavings > 0
					? DisplayUtility.FormatBytes(issue.EstimatedSavings) : "-", "po-slot-value");
				if (issue.EstimatedSavings <= 0) saving.style.color = new Color(0.44f, 0.46f, 0.50f);
				row.Add(saving);

				var actions = UiKit.Row("po-slot-actions");
				var path = entry.Path;
				var settingsPage = entry.Field("settingsPage") as string;
				actions.Add(UiKit.Button("Select", () =>
				{
					if (!UnityEditorUtility.PingPath(path) && !string.IsNullOrEmpty(settingsPage))
						SettingsService.OpenProjectSettings(settingsPage);
				}, "po-button-small"));

				var capturedEntry = entry;
				var capturedIssue = issue;
				var fixButton = UiKit.Button("Fix", () =>
				{
					// Identical to the category page's path: FixEngine owns the transaction, and the
					// result is reported through the same summary. This button used to call Apply
					// bare and discard the report, so a refused or unverified fix looked like a
					// success on the one screen most likely to be trusted at a glance.
					var report = FixEngine.Apply(new List<FixRequest>
					{
						new FixRequest { Entry = capturedEntry, Issue = capturedIssue }
					});

					CategoryPage.LogFixReport(report);
					CategoryPage.ReportFixOutcome(report);

					var affected = new List<string>(report.TouchedPaths);
					affected.AddRange(report.RolledBackPaths);
					if (affected.Count > 0) m_AfterFix?.Invoke(affected);
				}, "po-button-primary", "po-button-small");

				if (!issue.AutoFixable)
				{
					// Kept in the layout but invisible, so Select lands in the same column on every row.
					fixButton.AddToClassList("po-button-placeholder");
					fixButton.SetEnabled(false);
				}
				actions.Add(fixButton);

				row.Add(actions);
				row.tooltip = issue.RuleId + " - " + issue.Why;
				card.Add(row);
			}
			return card;
		}

		VisualElement BuildFootprint(ScanResult result)
		{
			var card = UiKit.Column("po-card");
			var totals = new Dictionary<AssetCategory, long>();
			long max = 1;

			foreach (var entry in result.Entries)
			{
				if (entry.Category == AssetCategory.ProjectSetting) continue;
				totals.TryGetValue(entry.Category, out long current);
				current += entry.FileSize;
				totals[entry.Category] = current;
				max = Math.Max(max, current);
			}

			var ordered = new List<KeyValuePair<AssetCategory, long>>(totals);
			ordered.Sort((a, b) => b.Value.CompareTo(a.Value));

			foreach (var pair in ordered)
			{
				var row = UiKit.Row("po-row");
				row.Add(UiKit.Text(DisplayUtility.CategoryLabel(pair.Key), "po-row-name"));
				row.Add(UiKit.Bar(pair.Value / (float)max, null));
				var size = UiKit.Text(DisplayUtility.FormatBytes(pair.Value), "po-row-dim");
				size.style.width = 92f;
				size.style.marginLeft = 10f;
				size.style.unityTextAlign = TextAnchor.MiddleRight;
				row.Add(size);
				card.Add(row);
			}

			if (ordered.Count == 0)
				card.Add(UiKit.Empty("No data", "Run a scan to see how disk space is distributed."));

			return card;
		}
	}
}

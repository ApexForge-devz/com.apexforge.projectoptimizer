using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Describes one column of a category table.</summary>
	public sealed class ColumnSpec
	{
		public string Key;
		public string Title;
		public float Width = 90f;
		public float MinWidth = 44f;
		public float MaxWidth = 320f;
		public bool Stretch;
		public bool RightAlign;
		public Func<AssetEntry, string> Text;
		public Func<AssetEntry, IComparable> Sort;
		public Func<AssetEntry, string> CellClass;

		/// <summary>A column backed by an <see cref="AssetEntry.Fields"/> value.</summary>
		/// <summary>
		/// A column backed by an <see cref="AssetEntry.Fields"/> value.
		/// <para>
		/// <paramref name="sortKey"/> names a different field to sort on, for columns that display a
		/// formatted string. Sorting "9.80 s", "87.04 s" and "8.12 s" as text puts 87 between 8 and 9,
		/// which is what a length or a resolution column does unless it is told to sort on the number
		/// the string was built from.
		/// </para>
		/// </summary>
		public static ColumnSpec Field(string key, string title, float width, bool rightAlign = false,
			Func<object, string> format = null, string sortKey = null)
		{
			var sortField = sortKey ?? key;
			return new ColumnSpec
			{
				Key = key,
				Title = title,
				Width = width,
				RightAlign = rightAlign,
				Text = e =>
				{
					var value = e.Field(key);
					if (value == null) return "-";
					if (format != null) return format(value);
					if (value is long l) return DisplayUtility.FormatCount(l);
					if (value is double d) return d.ToString("0.##");
					return value.ToString();
				},
				Sort = e => e.Field(sortField) as IComparable
			};
		}

		/// <summary>A yes/no field column that colours "Yes" as a problem.</summary>
		public static ColumnSpec Flag(string key, string title, float width, bool yesIsBad = true)
		{
			var spec = Field(key, title, width);
			spec.CellClass = e =>
			{
				var value = e.Field(key) as string;
				if (value == null) return null;
				bool yes = value == "Yes";
				return yes == yesIsBad ? "po-cell-bad" : "po-cell-dim";
			};
			return spec;
		}
	}

	/// <summary>A filterable, sortable table of one or more asset categories, with a detail pane.</summary>
	public sealed class CategoryPage : VisualElement
	{
		const float k_RowHeight = 28f;

		/// <summary>Stable id for this tab, used to key its saved UI state. See <see cref="OptimizerWindowState"/>.</summary>
		public string TabId { get; }

		readonly AssetCategory[] m_Categories;
		readonly List<ColumnSpec> m_Columns;
		readonly Action<IReadOnlyList<string>> m_AfterFix;
		readonly Action m_OnStateChanged;
		readonly string m_Title;
		readonly bool m_IsInsights;
		readonly AssetMetricColumns m_AssetMetrics;
		readonly string m_PrimaryColumnTitle;
		bool m_InsightsIssuesOnly;

		readonly List<AssetEntry> m_All = new List<AssetEntry>();
		readonly List<AssetEntry> m_Filtered = new List<AssetEntry>();

		MultiColumnListView m_Table;
		VisualElement m_DetailsBody;
		Label m_CountLabel;
		Label m_Measurer;
		TextField m_Search;
		FilterDropdown m_RuleFilter;
		FilterDropdown m_FolderFilter;
		FilterDropdown m_FixKindFilter;
		VisualElement m_FilterBar;
		Button m_FixAllButton;
		Button m_FixSelectedButton;
		Button m_ReviewSelectedButton;
		Button m_ClearFiltersButton;
		FilterDropdown m_ReferencedFilter;

		// Held so "Clear filters" can put every control back, not just the ones with a field already.
		readonly List<UiKit.Chip> m_SeverityChips = new List<UiKit.Chip>();
		UiKit.Chip m_IssuesOnlyChip;

		/// <summary>
		/// Severity chips, "issues only" and the folder are shared by every tab.
		/// <para>
		/// They describe what the user is looking for, not what any one tab contains, so re-setting
		/// them on each tab makes the filter bar feel like it forgets. The rule filter is deliberately
		/// not shared: a rule id belongs to exactly one tab, so carrying it across would leave the
		/// next tab filtered by something that cannot match anything there.
		/// </para>
		/// </summary>
		static readonly bool[] s_Severity = { true, true, true }; // Info, Warning, Critical
		static bool s_IssuesOnly = true;
		static string s_FolderFilterValue; // null = every folder

		string m_RuleFilterValue;   // null = every rule, per tab
		string m_FixKindFilterValue; // null = every kind, per tab: "verified" | "review" | "nofix" | "stale"
		string m_ReferencedFilterValue;

		public CategoryPage(string tabId, string title, AssetCategory[] categories, IEnumerable<ColumnSpec> columns,
			Action<IReadOnlyList<string>> afterFix, Action onStateChanged = null, bool isInsights = false, AssetMetricColumns assetMetrics = AssetMetricColumns.All, string primaryColumnTitle = "Asset")
		{
			TabId = tabId;
			m_Title = title;
			m_IsInsights = isInsights;
			m_AssetMetrics = assetMetrics;
			m_PrimaryColumnTitle = string.IsNullOrWhiteSpace(primaryColumnTitle) ? "Asset" : primaryColumnTitle;
			m_InsightsIssuesOnly = false;
			m_Categories = categories;
			m_AfterFix = afterFix;
			m_OnStateChanged = onStateChanged;
			m_Columns = BuildColumns(columns, m_AssetMetrics, m_PrimaryColumnTitle);

			AddToClassList("po-page");
			style.flexDirection = FlexDirection.Column;

			BuildFilterBar();
			BuildSplit();
			BuildMeasurer();
		}

		static List<ColumnSpec> BuildColumns(IEnumerable<ColumnSpec> extra, AssetMetricColumns assetMetrics, string primaryColumnTitle)
		{
			var columns = new List<ColumnSpec>
			{
				new ColumnSpec
				{
					Key = "$severity", Title = "Severity", Width = 76f, MinWidth = 70f, MaxWidth = 90f,
					Text = e => e.HasIssues ? DisplayUtility.SeverityLabel(e.WorstSeverity).ToUpperInvariant() : "CLEAN",
					Sort = e => e.HasIssues ? (int)e.WorstSeverity : -1
				},
				new ColumnSpec
				{
					Key = "$asset", Title = primaryColumnTitle ?? "Asset", Width = 320f, MinWidth = 180f, MaxWidth = 900f, Stretch = true,
					Text = e => e.Name,
					Sort = e => e.Name
				},
				new ColumnSpec
				{
					Key = "$issues", Title = "Issues", Width = 52f, MinWidth = 46f, MaxWidth = 60f, RightAlign = true,
					Text = e => e.Issues.Count.ToString(),
					Sort = e => e.Issues.Count
				}
			};
			columns.AddRange(extra ?? Array.Empty<ColumnSpec>());
			if (assetMetrics == AssetMetricColumns.None) return columns;
			if ((assetMetrics & AssetMetricColumns.DiskSize) != 0)
				columns.Add(new ColumnSpec
				{
					Key = "$size", Title = "On disk", Width = 74f, MaxWidth = 100f, RightAlign = true,
					Text = e => DisplayUtility.FormatBytes(e.FileSize), Sort = e => e.FileSize, CellClass = _ => "po-cell-dim"
				});
			if ((assetMetrics & AssetMetricColumns.RuntimeSize) != 0)
				columns.Add(new ColumnSpec
				{
					Key = "$runtime", Title = "In memory", Width = 80f, MaxWidth = 110f, RightAlign = true,
					Text = e => e.RuntimeSize >= 0 ? DisplayUtility.FormatBytes(e.RuntimeSize) : "-", Sort = e => e.RuntimeSize, CellClass = _ => "po-cell-dim"
				});
			if ((assetMetrics & AssetMetricColumns.Savings) != 0)
				columns.Add(new ColumnSpec
				{
					Key = "$savings", Title = "Est. saving", Width = 84f, MaxWidth = 110f, RightAlign = true,
					Text = e => e.PotentialSavings > 0 ? DisplayUtility.FormatBytes(e.PotentialSavings) : "-", Sort = e => e.PotentialSavings,
					CellClass = e => e.PotentialSavings > 0 ? "po-cell-good" : "po-cell-dim"
				});
			return columns;
		}

		// --- filter bar ---------------------------------------------------------

		void BuildFilterBar()
		{
			m_FilterBar = UiKit.Row("po-filters");

			m_Search = new TextField { value = string.Empty };
			m_Search.AddToClassList("po-search");
			m_Search.textEdition.placeholder = "Search name, path or rule id...";
			m_Search.RegisterValueChangedCallback(_ => { ApplyFilters(); m_OnStateChanged?.Invoke(); });
			m_FilterBar.Add(m_Search);

			AddSeverityChip("Critical", IssueSeverity.Critical, "sev-critical");
			AddSeverityChip("Warning", IssueSeverity.Warning, "sev-warning");
			AddSeverityChip("Info", IssueSeverity.Info, "sev-info");

			bool issuesOnly = m_IsInsights ? m_InsightsIssuesOnly : s_IssuesOnly;
			m_IssuesOnlyChip = new UiKit.Chip("Issues only", issuesOnly);
			m_IssuesOnlyChip.tooltip = m_IsInsights
				? "Off shows the build/package data even when a row has no diagnostic issue. On shows only rows with findings.\n\nThis setting is local to Insights."
				: "Off also lists assets this tab found nothing wrong with.\n\nKept across tabs.";
			m_IssuesOnlyChip.Changed += on =>
			{
				if (m_IsInsights) m_InsightsIssuesOnly = on;
				else s_IssuesOnly = on;
				ApplyFilters(); m_OnStateChanged?.Invoke();
			};
			m_FilterBar.Add(m_IssuesOnlyChip);

			if (string.Equals(TabId, "duplicates", StringComparison.OrdinalIgnoreCase))
			{
				m_ReferencedFilter = new FilterDropdown("All reference states",
					"Filter duplicate findings by whether their copies have static project referrers.");
				var referenceTree = new FilterDropdown.Node();
				referenceTree.Add(new FilterDropdown.Node
				{ Id = "referenced", Label = "Referenced", ButtonLabel = "Referenced" });
				referenceTree.Add(new FilterDropdown.Node
				{ Id = "referencedCopies", Label = "Referenced Copies", ButtonLabel = "Referenced Copies" });
				m_ReferencedFilter.SetTree(referenceTree);
				m_ReferencedFilter.ValueChanged += value =>
				{
					m_ReferencedFilterValue = value;
					ApplyFilters();
					m_OnStateChanged?.Invoke();
				};
				m_FilterBar.Add(m_ReferencedFilter);
			}

			m_RuleFilter = new FilterDropdown("All rules", "Show only assets that break one specific rule.");
			m_RuleFilter.ValueChanged += value => { m_RuleFilterValue = value; ApplyFilters(); m_OnStateChanged?.Invoke(); };
			m_FilterBar.Add(m_RuleFilter);

			m_FixKindFilter = new FilterDropdown("All fix kinds",
				"Show only issues of one fix kind: Verified (Fix All eligible), Review (needs explicit "
				+ "approval), No fix, or Stale (rescan required).");
			var fixKindTree = new FilterDropdown.Node();
			fixKindTree.Add(new FilterDropdown.Node { Id = "verified", Label = "Verified Fix" });
			fixKindTree.Add(new FilterDropdown.Node { Id = "review", Label = "Review Fix" });
			fixKindTree.Add(new FilterDropdown.Node { Id = "nofix", Label = "No Fix" });
			fixKindTree.Add(new FilterDropdown.Node { Id = "stale", Label = "Stale / rescan required" });
			m_FixKindFilter.SetTree(fixKindTree);
			m_FixKindFilter.ValueChanged += value => { m_FixKindFilterValue = value; ApplyFilters(); m_OnStateChanged?.Invoke(); };
			m_FilterBar.Add(m_FixKindFilter);

			m_FolderFilter = new FilterDropdown("All folders",
				"Pick any folder to narrow the list. Selecting a folder includes everything beneath it.");
			m_FolderFilter.ValueChanged += value => { s_FolderFilterValue = value; ApplyFilters(); m_OnStateChanged?.Invoke(); };
			m_FilterBar.Add(m_FolderFilter);

			m_CountLabel = UiKit.Text(string.Empty, "po-result-count");
			m_FilterBar.Add(m_CountLabel);

			m_FilterBar.Add(UiKit.Spacer());

			var fit = UiKit.Button("Fit columns", RefitColumns, "po-button-small");
			fit.tooltip = "Re-measure every column against the values it currently shows, discarding "
						  + "any widths you set by dragging a divider.";
			m_FilterBar.Add(fit);

			m_ClearFiltersButton = UiKit.Button("Clear filters", ClearFilters, "po-button-small");
			m_ClearFiltersButton.tooltip = "Reset the search box, rule, folder and severity filters on this tab.";
			m_ClearFiltersButton.style.display = DisplayStyle.None;
			m_FilterBar.Add(m_ClearFiltersButton);

			// Both fix buttons carry their count, so the number affected is known before the click
			// rather than confirmed in the dialog after it.
			m_FixAllButton = UiKit.Button("Fix all shown", FixAllShown, "po-button-primary", "po-button-small");
			m_FilterBar.Add(m_FixAllButton);

			m_FixSelectedButton = UiKit.Button("Fix selected", FixSelected, "po-button-small");
			m_FilterBar.Add(m_FixSelectedButton);

			Add(m_FilterBar);
		}

		void AddSeverityChip(string label, IssueSeverity severity, string severityClass)
		{
			var chip = new UiKit.Chip(label, s_Severity[(int)severity], severityClass);
			chip.tooltip = "Show or hide " + label.ToLowerInvariant() + " findings.\n\nKept across tabs.";
			chip.Changed += on => { s_Severity[(int)severity] = on; ApplyFilters(); m_OnStateChanged?.Invoke(); };
			m_SeverityChips.Add(chip);
			m_FilterBar.Add(chip);
		}

		/// <summary>A hidden label used to measure real text widths for the column auto-fit.</summary>
		void BuildMeasurer()
		{
			m_Measurer = new Label();
			m_Measurer.style.position = Position.Absolute;
			m_Measurer.style.visibility = Visibility.Hidden;
			m_Measurer.pickingMode = PickingMode.Ignore;
			Add(m_Measurer);
		}

		// --- table + details -------------------------------------------------------

		void BuildSplit()
		{
			var split = new TwoPaneSplitView(1, 400f, TwoPaneSplitViewOrientation.Horizontal);
			split.style.flexGrow = 1;

			m_Table = new MultiColumnListView
			{
				fixedItemHeight = k_RowHeight,
				selectionType = SelectionType.Multiple,
				sortingMode = ColumnSortingMode.Custom,
				showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly
			};
			m_Table.AddToClassList("po-table");
			m_Table.style.flexGrow = 1;

			foreach (var spec in m_Columns)
				m_Table.columns.Add(MakeColumn(spec));

			m_Table.itemsSource = m_Filtered;
			m_Table.selectionChanged += _ =>
			{
				RefreshDetails();
				UpdateSelectionButton();
				m_OnStateChanged?.Invoke();
			};
			m_Table.itemsChosen += items =>
			{
				foreach (var item in items)
					if (item is AssetEntry entry) PingOrExplain(entry.Path);
			};
			m_Table.columnSortingChanged += () => { ApplySorting(); m_OnStateChanged?.Invoke(); };

			// Widths are handed out here rather than by Unity, which shrinks the last column to its
			// minimum before touching any other. See LayoutColumns.
			m_Table.RegisterCallback<GeometryChangedEvent>(OnTableGeometryChanged);

			foreach (var column in m_Table.columns)
			{
				var captured = column;
				captured.propertyChanged += (_, args) =>
				{
					if (args.propertyName == "width") OnColumnChanged(captured);
				};
			}

			var details = UiKit.Column("po-details");
			details.style.flexGrow = 1;
			var scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			scroll.style.flexGrow = 1;
			m_DetailsBody = UiKit.Column();
			scroll.Add(m_DetailsBody);
			details.Add(scroll);

			split.Add(m_Table);
			split.Add(details);
			Add(split);
		}

		Column MakeColumn(ColumnSpec spec)
		{
			var column = new Column
			{
				name = spec.Key,
				title = spec.Title,
				width = spec.Width,
				minWidth = spec.MinWidth,
				// Not stretchable, deliberately: the widths are worked out in LayoutColumns and
				// already add up to the space available, so Unity has nothing left to distribute
				// and cannot undo them.
				stretchable = false,
				sortable = true,
				resizable = true
			};

			if (spec.Key == "$severity")
			{
				column.makeCell = () =>
				{
					var cell = UiKit.Row("po-cell");
					cell.Add(UiKit.Pill(string.Empty, null));
					return cell;
				};
				column.bindCell = (element, index) =>
				{
					if (index < 0 || index >= m_Filtered.Count) return;
					var entry = m_Filtered[index];
					var pill = (Label)element[0];
					pill.text = entry.HasIssues ? DisplayUtility.SeverityLabel(entry.WorstSeverity).ToUpperInvariant() : "CLEAN";
					pill.RemoveFromClassList("sev-critical");
					pill.RemoveFromClassList("sev-warning");
					pill.RemoveFromClassList("sev-info");
					pill.RemoveFromClassList("sev-clean");
					pill.AddToClassList(entry.HasIssues ? DisplayUtility.SeverityClass(entry.WorstSeverity) : "sev-clean");
				};
				return column;
			}

			if (spec.Key == "$asset")
			{
				// Name and folder share one line so rows stay short without losing the path.
				column.makeCell = () =>
				{
					var cell = UiKit.Row("po-cell");
					cell.Add(UiKit.Icon(null, 16f));
					var name = UiKit.Text(string.Empty, "po-cell-text");
					name.style.marginLeft = 7f;
					name.style.flexShrink = 0f;
					cell.Add(name);
					cell.Add(UiKit.Text(string.Empty, "po-cell-sub"));

					// Cells are pooled and rebound as the table scrolls, so the entry a right-click
					// should act on has to be read back out of the cell at click time (via userData,
					// set in bindCell below) rather than captured here - by the time this fires, this
					// same cell object may have been recycled onto a completely different row.
					cell.RegisterCallback<ContextClickEvent>(evt =>
					{
						if (cell.userData is AssetEntry contextEntry)
							ShowRowContextMenu(evt.mousePosition, contextEntry);
						evt.StopPropagation();
					});
					return cell;
				};
				column.bindCell = (element, index) =>
				{
					if (index < 0 || index >= m_Filtered.Count) return;
					var entry = m_Filtered[index];
					element.userData = entry;
					((Image)element[0]).image = AssetDatabase.GetCachedIcon(entry.Path);
					((Label)element[1]).text = entry.Name;
					((Label)element[2]).text = entry.Directory;
					element.tooltip = entry.Path;
				};
				return column;
			}

			column.makeCell = () =>
			{
				var cell = UiKit.Row("po-cell");
				var label = UiKit.Text(string.Empty, "po-cell-text");
				label.style.flexGrow = 1f;
				cell.Add(label);
				return cell;
			};
			column.bindCell = (element, index) =>
			{
				if (index < 0 || index >= m_Filtered.Count) return;
				var entry = m_Filtered[index];
				var label = (Label)element[0];
				label.text = spec.Text != null ? spec.Text(entry) : "-";
				label.EnableInClassList("po-cell-num", spec.RightAlign);

				label.RemoveFromClassList("po-cell-dim");
				label.RemoveFromClassList("po-cell-good");
				label.RemoveFromClassList("po-cell-bad");
				var extra = spec.CellClass?.Invoke(entry);
				if (!string.IsNullOrEmpty(extra)) label.AddToClassList(extra);
			};
			return column;
		}

		// --- column widths -------------------------------------------------------------

		/// <summary>How many rows are sampled when working out how wide a column needs to be.</summary>
		const int k_MeasureSample = 500;

		/// <summary>Cell padding and borders the measured text has to sit inside.</summary>
		const float k_CellPadding = 16f;

		/// <summary>Header padding, including room for the sort arrow.</summary>
		const float k_HeaderPadding = 20f;

		/// <summary>
		/// Width each column needs to show its content in full, in column order. Recomputed when the
		/// data changes, not when the pane is dragged: what the values are does not depend on how
		/// much room there is to show them.
		/// </summary>
		readonly List<float> m_Desired = new List<float>();

		/// <summary>Widths the user set by dragging a divider, which override the measured ones.</summary>
		readonly Dictionary<string, float> m_UserWidths = new Dictionary<string, float>(StringComparer.Ordinal);

		float m_LastLayoutWidth = -1f;
		bool m_ApplyingLayout;
		bool m_RecheckQueued;
		ScrollView m_TableScroll;

		/// <summary>
		/// Works out how wide each column needs to be to show what is in it. Independent of the space
		/// available - that is LayoutColumns' problem.
		/// </summary>
		void MeasureColumns()
		{
			m_Desired.Clear();
			int sample = Mathf.Min(m_Filtered.Count, k_MeasureSample);

			for (int c = 0; c < m_Columns.Count; c++)
			{
				var spec = m_Columns[c];

				if (m_UserWidths.TryGetValue(spec.Key, out float pinned))
				{
					m_Desired.Add(pinned);
					continue;
				}

				float widest = MeasureWidth(spec.Title, 11f) + k_HeaderPadding;

				if (spec.Key == "$severity")
				{
					widest = Mathf.Max(widest, MeasureWidth("CRITICAL", 10f) + 24f);
				}
				else
				{
					for (int i = 0; i < sample; i++)
					{
						var text = spec.Text?.Invoke(m_Filtered[i]);
						if (string.IsNullOrEmpty(text)) continue;
						widest = Mathf.Max(widest, MeasureWidth(text, 12f) + k_CellPadding);
					}
				}

				m_Desired.Add(Mathf.Clamp(Mathf.Ceil(widest), spec.MinWidth, spec.MaxWidth));
			}
		}

		/// <summary>
		/// Hands out the width the table actually has.
		/// <para>
		/// The rule is that no column is truncated while another column is still holding space it
		/// does not need. Unity's own behaviour is to shrink the last column until it hits its
		/// minimum and only then move left, so dragging the details pane across chews through the
		/// right-hand columns one at a time while a half-empty column in the middle keeps its slack.
		/// </para>
		/// <para>
		/// Here every column starts at the width its own content needs. Surplus goes to the asset
		/// column; a shortfall is taken from the asset column first, because a path is still
		/// recognisable from its beginning and is the one value with no natural width. Only once that
		/// is spent does anything else lose characters, and then the widest column gives way first,
		/// so a four-character number keeps its four characters until nothing else is left to take.
		/// </para>
		/// </summary>
		void LayoutColumns()
		{
			if (m_Table == null || m_Table.columns.Count != m_Columns.Count) return;
			if (m_Desired.Count != m_Columns.Count) MeasureColumns();

			float available = AvailableWidth();
			if (available <= 1f) return;

			var width = new float[m_Columns.Count];
			float total = 0f;
			for (int i = 0; i < width.Length; i++)
			{
				width[i] = m_Desired[i];
				total += width[i];
			}

			int flexible = FlexibleColumn();

			if (total < available)
			{
				// Spare room all goes to the asset column: every other column is already showing
				// everything it has, so widening them would only add whitespace.
				if (flexible >= 0) width[flexible] += available - total;
			}
			else if (total > available)
			{
				float deficit = total - available;

				if (flexible >= 0)
				{
					float give = Mathf.Min(deficit, width[flexible] - m_Columns[flexible].MinWidth);
					width[flexible] -= give;
					deficit -= give;
				}

				if (deficit > 0.5f) ShaveWidest(width, flexible, deficit, m_Columns);
			}

			FitToWholePixels(width, available, flexible);
			m_LastLayoutWidth = available;
			Apply(width);
			QueueScrollbarRecheck();
		}

		/// <summary>
		/// Looks again on the next frame in case this layout is what made the vertical scrollbar
		/// appear. The scrollbar lives inside the table, so its arrival narrows the columns'
		/// share without raising a geometry event of its own - the layout would otherwise sit a
		/// scrollbar's width too wide and show a horizontal scrollbar it does not need. The width
		/// comparison is what keeps this from becoming a cycle.
		/// </summary>
		void QueueScrollbarRecheck()
		{
			if (m_RecheckQueued) return;
			m_RecheckQueued = true;

			schedule.Execute(() =>
			{
				m_RecheckQueued = false;
				float now = AvailableWidth();
				if (now > 1f && Mathf.Abs(now - m_LastLayoutWidth) >= 0.5f) LayoutColumns();
			});
		}

		/// <summary>
		/// Rounds the widths to whole pixels and hands the remainder out, so the columns add up to
		/// exactly the space available. Without this the leftover shows either as a sliver of dead
		/// space against the right edge or as a one-pixel overflow, which is enough to raise a
		/// horizontal scrollbar and start the whole layout again a little narrower.
		/// </summary>
		void FitToWholePixels(float[] width, float available, int flexible)
		{
			float total = 0f;
			for (int i = 0; i < width.Length; i++)
			{
				width[i] = Mathf.Round(width[i]);
				total += width[i];
			}

			int remainder = Mathf.RoundToInt(available - total);
			if (remainder == 0) return;

			// Short: the flexible column takes it if it can, otherwise it is spread a pixel at a
			// time over the columns still below the width their content wants.
			if (remainder > 0)
			{
				if (flexible >= 0 && width[flexible] < m_Desired[flexible])
				{
					float room = m_Desired[flexible] - width[flexible];
					float give = Mathf.Min(remainder, room);
					width[flexible] += give;
					remainder -= Mathf.RoundToInt(give);
				}

				for (int pass = 0; pass < 2 && remainder > 0; pass++)
				{
					for (int i = 0; i < width.Length && remainder > 0; i++)
					{
						// Second pass ignores the content width: at that point every column is
						// already truncated and the space has to go somewhere.
						if (pass == 0 && width[i] >= m_Desired[i]) continue;
						width[i] += 1f;
						remainder--;
					}
				}
				return;
			}

			// Over by a pixel or two after rounding: take it back off the widest columns.
			for (int over = -remainder; over > 0;)
			{
				int widest = -1;
				for (int i = 0; i < width.Length; i++)
					if (width[i] > m_Columns[i].MinWidth && (widest < 0 || width[i] > width[widest])) widest = i;
				if (widest < 0) return;

				width[widest] -= 1f;
				over--;
			}
		}

		/// <summary>
		/// Reclaims <paramref name="deficit"/> pixels by capping every column at a common ceiling and
		/// lowering that ceiling until enough has been given up, never taking a column below its own
		/// minimum. One ceiling for all of them is what makes the widest column pay first, and makes
		/// equally wide columns pay equally.
		/// </summary>
		static void ShaveWidest(float[] width, int skip, float deficit, IReadOnlyList<ColumnSpec> specs)
		{
			float target = 0f;
			float highest = 0f;
			for (int i = 0; i < width.Length; i++)
			{
				if (i == skip) continue;
				target += width[i];
				highest = Mathf.Max(highest, width[i]);
			}
			target -= deficit;

			// Binary search for the ceiling. Solving it directly would mean sorting the columns and
			// walking them; over a dozen columns this is simpler and lands within a fraction of a pixel.
			float low = 0f;
			float high = highest;
			for (int step = 0; step < 40; step++)
			{
				float cap = (low + high) * 0.5f;
				float sum = 0f;
				for (int i = 0; i < width.Length; i++)
				{
					if (i == skip) continue;
					sum += Mathf.Max(specs[i].MinWidth, Mathf.Min(width[i], cap));
				}
				if (sum > target) high = cap;
				else low = cap;
			}

			for (int i = 0; i < width.Length; i++)
			{
				if (i == skip) continue;
				width[i] = Mathf.Max(specs[i].MinWidth, Mathf.Min(width[i], low));
			}
		}

		void Apply(float[] width)
		{
			m_ApplyingLayout = true;
			try
			{
				for (int i = 0; i < width.Length; i++)
				{
					float rounded = Mathf.Round(width[i]);
					if (Mathf.Abs(m_Table.columns[i].width.value - rounded) < 0.5f) continue;
					m_Table.columns[i].width = rounded;
				}
			}
			finally
			{
				m_ApplyingLayout = false;
			}
		}

		/// <summary>The column allowed to absorb both surplus and shortfall. -1 when there is none.</summary>
		int FlexibleColumn()
		{
			for (int i = 0; i < m_Columns.Count; i++)
				if (m_Columns[i].Stretch) return i;
			return -1;
		}

		/// <summary>
		/// The width the columns actually have to share. The vertical scrollbar sits inside the
		/// table's box and is not theirs to use, and it is measured rather than assumed because its
		/// width differs between skins.
		/// </summary>
		float AvailableWidth()
		{
			if (m_Table == null) return 0f;

			float width = m_Table.contentRect.width;
			if (float.IsNaN(width) || width <= 1f) return 0f;

			if (m_TableScroll == null) m_TableScroll = m_Table.Q<ScrollView>();
			var scroller = m_TableScroll?.verticalScroller;
			if (scroller != null && scroller.resolvedStyle.display == DisplayStyle.Flex)
			{
				float bar = scroller.resolvedStyle.width;
				if (!float.IsNaN(bar) && bar > 0f) width -= bar;
			}

			// A pixel left in hand, so rounding can never push the total past the edge and produce a
			// horizontal scrollbar that would then narrow the table further.
			return width - 1f;
		}

		/// <summary>Re-measures from the data and lays out again. This is what "Fit columns" does.</summary>
		void RefitColumns()
		{
			m_UserWidths.Clear();
			MeasureColumns();
			LayoutColumns();
		}

		void OnTableGeometryChanged(GeometryChangedEvent evt)
		{
			float available = AvailableWidth();
			if (available <= 1f) return;

			// Applying widths changes the geometry again. Laying out only when the space to fill has
			// actually changed is what stops that from looping.
			if (Mathf.Abs(available - m_LastLayoutWidth) < 0.5f) return;
			m_LastLayoutWidth = available;
			LayoutColumns();
		}

		/// <summary>
		/// A width that changed without this class setting it came from the user dragging a divider.
		/// That is a deliberate choice, so it is kept and used in place of the measured width until
		/// they press Fit columns.
		/// </summary>
		void OnColumnChanged(Column column)
		{
			if (m_ApplyingLayout || column == null || string.IsNullOrEmpty(column.name)) return;

			m_UserWidths[column.name] = column.width.value;
			for (int i = 0; i < m_Columns.Count; i++)
			{
				if (m_Columns[i].Key != column.name) continue;
				if (i < m_Desired.Count) m_Desired[i] = column.width.value;
				break;
			}
			m_OnStateChanged?.Invoke();
		}

		/// <summary>Measures with the real font when the panel can, and falls back to a character estimate.</summary>
		float MeasureWidth(string text, float fontSize)
		{
			if (string.IsNullOrEmpty(text)) return 0f;
			if (m_Measurer != null && m_Measurer.panel != null)
			{
				m_Measurer.style.fontSize = fontSize;
				var size = m_Measurer.MeasureTextSize(text, 0f, MeasureMode.Undefined, 0f, MeasureMode.Undefined);
				if (size.x > 1f) return size.x;
			}
			return text.Length * fontSize * 0.56f;
		}

		// --- sorting -------------------------------------------------------------------

		void ApplySorting()
		{
			var sorted = m_Table.sortedColumns?.ToList();
			if (sorted == null || sorted.Count == 0)
			{
				m_Table.RefreshItems();
				return;
			}

			var specs = new List<KeyValuePair<ColumnSpec, bool>>();
			foreach (var description in sorted)
			{
				var spec = m_Columns.Find(c => c.Key == description.columnName);
				if (spec?.Sort != null)
					specs.Add(new KeyValuePair<ColumnSpec, bool>(spec, description.direction == SortDirection.Ascending));
			}
			SortFilteredBy(specs);
		}

		/// <summary>
		/// The actual row-ordering half of <see cref="ApplySorting"/>, split out so a restored sort -
		/// see <see cref="RestoreSelectionAndSort"/> - can order <see cref="m_Filtered"/> directly from
		/// saved column keys without going through <c>m_Table.sortedColumns</c>. That collection is
		/// resolved by the view's own header/column controller, and nothing here can guarantee it has
		/// finished doing so by the time a restore runs.
		/// </summary>
		void SortFilteredBy(List<KeyValuePair<ColumnSpec, bool>> specs)
		{
			if (specs == null || specs.Count == 0)
			{
				m_Table.RefreshItems();
				return;
			}

			m_Filtered.Sort((a, b) =>
			{
				foreach (var pair in specs)
				{
					int result = CompareValues(pair.Key.Sort(a), pair.Key.Sort(b));
					if (result != 0) return pair.Value ? result : -result;
				}
				return string.Compare(a.Path, b.Path, StringComparison.Ordinal);
			});
			m_Table.RefreshItems();
		}

		static int CompareValues(IComparable a, IComparable b)
		{
			if (a == null && b == null) return 0;
			if (a == null) return -1;
			if (b == null) return 1;

			// A long against a double throws rather than comparing, and the string fallback then
			// orders 87 before 9. Anything numeric is compared as a number first.
			if (IsNumeric(a) && IsNumeric(b))
			{
				double x = Convert.ToDouble(a);
				double y = Convert.ToDouble(b);
				return x.CompareTo(y);
			}

			if (a.GetType() == b.GetType())
			{
				try { return a.CompareTo(b); }
				catch { /* fall through to text */ }
			}
			return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
		}

		static bool IsNumeric(object value)
		{
			return value is long || value is int || value is double || value is float
				   || value is short || value is byte || value is decimal;
		}

		// --- data ------------------------------------------------------------------

		public void SetData(ScanResult result)
		{
			m_All.Clear();
			if (result != null) m_All.AddRange(result.For(m_Categories));

			RebuildFilterChoices();
			ApplyFilters();

			// Wait a frame so styles are resolved and text measurement is meaningful.
			schedule.Execute(() =>
			{
				MeasureColumns();
				LayoutColumns();
			});
		}

		void RebuildFilterChoices()
		{
			m_RuleFilter.SetTree(BuildRuleTree());
			m_FolderFilter.SetTree(BuildFolderTree());

			// A rule or folder picked before the rescan may no longer exist in the new data.
			m_RuleFilterValue = m_RuleFilter.Value;

			// The shared folder may not exist on this tab. Re-applied when it does, dropped when it
			// does not, rather than silently filtering the tab down to nothing.
			m_FolderFilter.SetValue(s_FolderFilterValue, false);
			if (m_FolderFilter.Value != s_FolderFilterValue && !string.IsNullOrEmpty(s_FolderFilterValue))
				m_FolderFilter.SetValue(null, false);
		}

		/// <summary>Flat list of the rules that actually fired on this tab, worst first.</summary>
		FilterDropdown.Node BuildRuleTree()
		{
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			var titles = new Dictionary<string, string>(StringComparer.Ordinal);
			var severities = new Dictionary<string, IssueSeverity>(StringComparer.Ordinal);

			foreach (var entry in m_All)
			{
				foreach (var issue in entry.Issues)
				{
					counts.TryGetValue(issue.RuleId, out int count);
					counts[issue.RuleId] = count + 1;
					titles[issue.RuleId] = issue.Title;
					severities[issue.RuleId] = issue.Severity;
				}
			}

			var ids = new List<string>(counts.Keys);
			// ids.Sort((a, b) =>
			// {
			// 	int bySeverity = severities[b].CompareTo(severities[a]);
			// 	if (bySeverity != 0) return bySeverity;
			// 	int byCount = counts[b].CompareTo(counts[a]);
			// 	return byCount != 0 ? byCount : string.Compare(a, b, StringComparison.Ordinal);
			// });
			ids.Sort();

			var root = new FilterDropdown.Node();
			foreach (var id in ids)
			{
				root.Add(new FilterDropdown.Node
				{
					Id = id,
					Label = id + "  " + titles[id],
					Count = counts[id].ToString(),
					PillClass = DisplayUtility.SeverityClass(severities[id])
				});
			}
			return root;
		}

		/// <summary>
		/// Folders only, and only those holding assets this tab cares about. Ancestors are included
		/// so a parent folder can be picked directly, and every entry is counted by its whole subtree.
		/// </summary>
		FilterDropdown.Node BuildFolderTree()
		{
			var direct = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var entry in m_All)
			{
				if (string.IsNullOrEmpty(entry.Directory)) continue;
				direct.TryGetValue(entry.Directory, out int count);
				direct[entry.Directory] = count + 1;
			}

			var paths = new SortedSet<string>(StringComparer.Ordinal);
			var totals = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var pair in direct)
			{
				var path = pair.Key;
				while (!string.IsNullOrEmpty(path))
				{
					paths.Add(path);
					totals.TryGetValue(path, out int running);
					totals[path] = running + pair.Value;

					int slash = path.LastIndexOf('/');
					if (slash <= 0) break;
					path = path.Substring(0, slash);
				}
			}

			var root = new FilterDropdown.Node();
			var nodes = new Dictionary<string, FilterDropdown.Node>(StringComparer.Ordinal);

			// The sorted set yields parents before children, so a parent node always exists first.
			foreach (var path in paths)
			{
				int slash = path.LastIndexOf('/');
				var node = new FilterDropdown.Node
				{
					Id = path,
					Label = slash >= 0 ? path.Substring(slash + 1) : path,
					Count = totals.TryGetValue(path, out int total) ? total.ToString() : null
				};
				nodes[path] = node;

				if (slash > 0 && nodes.TryGetValue(path.Substring(0, slash), out var parentNode)) parentNode.Add(node);
				else root.Add(node);
			}
			return root;
		}

		/// <summary>
		/// Fuzzy scores for the current search, keyed by entry. Rebuilt every filter pass rather than
		/// cached across keystrokes - the per-entry candidate strings are already precomputed (see
		/// <see cref="AssetEntry.SearchCandidates"/>), so what runs per keystroke is just scoring
		/// against however many rows are already on the tab, not re-deriving anything from the asset
		/// database.
		/// </summary>
		readonly Dictionary<AssetEntry, int> m_SearchScores = new Dictionary<AssetEntry, int>();

		static readonly string[] k_FixKindOperators = { "verified:", "review:", "nofix:", "stale:" };

		/// <summary>
		/// A leading "review:", "verified:", "nofix:" or "stale:" in the search box is a fix-kind
		/// filter, not part of the fuzzy query - so "review: read" searches for "read" among Review
		/// Fix issues only. Never mandatory: the same narrowing is available as the fix-kind dropdown,
		/// this is just a faster way to type it. See P2.14 of the pass 6 TODO.
		/// </summary>
		static (string kind, string rest) ParseFixKindOperator(string search)
		{
			foreach (var prefix in k_FixKindOperators)
			{
				if (search.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					return (prefix.Substring(0, prefix.Length - 1), search.Substring(prefix.Length).Trim());
			}
			return (null, search);
		}

		static bool EntryMatchesFixKind(AssetEntry entry, string kind)
		{
			foreach (var issue in entry.Issues)
			{
				switch (kind)
				{
					case "verified": if (issue.FixKind == FixKind.Verified) return true; break;
					case "review": if (issue.FixKind == FixKind.Review) return true; break;
					case "nofix": if (issue.FixKind == FixKind.None && !issue.Stale) return true; break;
					case "stale": if (issue.Stale) return true; break;
				}
			}
			return false;
		}

		void ApplyFilters()
		{
			string rawSearch = m_Search != null ? m_Search.value.Trim() : string.Empty;
			var (operatorKind, remainder) = ParseFixKindOperator(rawSearch);
			string search = operatorKind != null ? remainder : rawSearch;
			string fixKindFilter = operatorKind ?? m_FixKindFilterValue;
			bool searching = !string.IsNullOrEmpty(search);
			string ruleId = m_RuleFilterValue;
			string folder = m_FolderFilter != null ? m_FolderFilter.Value : s_FolderFilterValue;
			string folderPrefix = string.IsNullOrEmpty(folder) ? null : folder + "/";

			m_SearchScores.Clear();
			m_Filtered.Clear();
			Dictionary<string, int> referencedCopyCounts = null;
			if (string.Equals(m_ReferencedFilterValue, "referencedCopies", StringComparison.Ordinal))
				referencedCopyCounts = BuildReferencedCopyCounts();
			foreach (var entry in m_All)
			{
				if ((m_IsInsights ? m_InsightsIssuesOnly : s_IssuesOnly) && !entry.HasIssues) continue;
				if (entry.HasIssues && !MatchesAnySeverity(entry)) continue;
				if (fixKindFilter != null && !EntryMatchesFixKind(entry, fixKindFilter)) continue;
				if (!MatchesReferencedFilter(entry, referencedCopyCounts)) continue;
				if (searching)
				{
					var best = FuzzyMatch.BestScore(search, entry.SearchCandidates);
					if (best == null) continue;
					m_SearchScores[entry] = best.Value;
				}
				if (ruleId != null && !HasRule(entry, ruleId)) continue;
				// A folder selection includes everything nested under it.
				if (folderPrefix != null && !entry.Path.StartsWith(folderPrefix, StringComparison.Ordinal)) continue;
				m_Filtered.Add(entry);
			}

			m_Filtered.Sort((a, b) =>
			{
				// A non-empty query ranks its own matches first; the usual severity/savings/path
				// order is still the tie-breaker, and is still the whole order when not searching.
				if (searching)
				{
					int byScore = m_SearchScores[b].CompareTo(m_SearchScores[a]);
					if (byScore != 0) return byScore;
				}
				int bySeverity = (b.HasIssues ? (int)b.WorstSeverity : -1).CompareTo(a.HasIssues ? (int)a.WorstSeverity : -1);
				if (bySeverity != 0) return bySeverity;
				int bySavings = b.PotentialSavings.CompareTo(a.PotentialSavings);
				return bySavings != 0 ? bySavings : string.Compare(a.Path, b.Path, StringComparison.Ordinal);
			});

			int fixable = CountFixable(m_Filtered);
			int reviewable = CountReviewOnly(m_Filtered);
			m_CountLabel.text = m_Filtered.Count + " of " + m_All.Count + " assets  -  " + fixable + " verified fix(es)"
								 + (reviewable > 0 ? ", " + reviewable + " review fix(es)" : "");

			m_FixAllButton.text = fixable > 0 ? "Fix " + fixable : "Fix";
			m_FixAllButton.SetEnabled(fixable > 0);
			m_FixAllButton.tooltip = fixable > 0
				? "Applies " + fixable + " Verified fix(es) to the " + m_Filtered.Count + " row(s) currently listed. "
				  + "Everything it touches is backed up first. Review Fixes are never included here - open an "
				  + "issue's Review Fix button to apply one."
				: "Nothing currently listed has a Verified fix this tool can apply without asking first.";

			// A filtered-to-nothing list and a genuinely clean tab look identical, so the way back
			// is offered only when filters are actually the reason.
			bool filtering = HasActiveFilters();
			m_ClearFiltersButton.style.display =
				(filtering && m_Filtered.Count == 0) ? DisplayStyle.Flex : DisplayStyle.None;

			UpdateSelectionButton();

			m_Table.itemsSource = m_Filtered;
			m_Table.ClearSelection();
			m_Table.RefreshItems();
			RefreshDetails();
		}

		bool MatchesAnySeverity(AssetEntry entry)
		{
			foreach (var issue in entry.Issues)
				if (s_Severity[(int)issue.Severity]) return true;
			return false;
		}

		Dictionary<string, int> BuildReferencedCopyCounts()
		{
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < m_All.Count; i++)
			{
				var entry = m_All[i];
				if (!HasReferenceEvidence(entry)) continue;
				string groupKey = DuplicateGroupKey(entry);
				if (string.IsNullOrEmpty(groupKey)) continue;
				counts.TryGetValue(groupKey, out int count);
				counts[groupKey] = count + 1;
			}
			return counts;
		}

		bool MatchesReferencedFilter(AssetEntry entry, Dictionary<string, int> referencedCopyCounts)
		{
			if (string.IsNullOrEmpty(m_ReferencedFilterValue)) return true;
			if (!string.Equals(TabId, "duplicates", StringComparison.OrdinalIgnoreCase)) return true;

			// Both reference modes intentionally exclude rows that have no actual referrer.
			if (!HasReferenceEvidence(entry)) return false;
			if (!string.Equals(m_ReferencedFilterValue, "referencedCopies", StringComparison.Ordinal)) return true;

			// "Referenced only copies" is a duplicate-group filter: at least two distinct copies from
			// the same byte-identical group must themselves have references. A referenced keeper paired
			// with an unreferenced copy does not qualify, and the unreferenced copy is never displayed.
			string groupKey = DuplicateGroupKey(entry);
			return !string.IsNullOrEmpty(groupKey)
				&& referencedCopyCounts != null
				&& referencedCopyCounts.TryGetValue(groupKey, out int count)
				&& count >= 2;
		}

		static bool HasReferenceEvidence(AssetEntry entry)
		{
			if (entry == null || entry.Issues == null) return false;
			for (int i = 0; i < entry.Issues.Count; i++)
			{
				var issue = entry.Issues[i];
				if (issue == null || issue.RuleId != "DUP001" || issue.Details == null) continue;
				for (int d = 0; d < issue.Details.Count; d++)
				{
					var detail = issue.Details[d];
					if (detail != null && string.Equals(detail.Label, "Referenced by", StringComparison.OrdinalIgnoreCase))
						return !string.IsNullOrEmpty(detail.Text)
							&& !detail.Text.StartsWith("and ", StringComparison.OrdinalIgnoreCase);
				}
			}
			return false;
		}

		static string DuplicateGroupKey(AssetEntry entry)
		{
			if (entry == null) return null;
			// DuplicateAnalyzer stores the keeper path in the machine-readable "original" field for
			// every row, including the keeper itself. Use that instead of parsing presentation details.
			object value = entry.Field("original");
			return value as string;
		}

		static bool HasRule(AssetEntry entry, string ruleId)
		{
			foreach (var issue in entry.Issues)
				if (issue.RuleId == ruleId) return true;
			return false;
		}

		static int CountFixable(IEnumerable<AssetEntry> entries)
		{
			int count = 0;
			foreach (var entry in entries)
				foreach (var issue in entry.Issues)
					if (issue.AutoFixable) count++;
			return count;
		}

		/// <summary>Issues that only offer a Review Fix - i.e. never eligible for Fix All.</summary>
		static int CountReviewOnly(IEnumerable<AssetEntry> entries)
		{
			int count = 0;
			foreach (var entry in entries)
				foreach (var issue in entry.Issues)
					if (issue.FixKind == FixKind.Review) count++;
			return count;
		}

		// --- details ----------------------------------------------------------------

		/// <summary>True when anything is narrowing the list beyond "show everything on this tab".</summary>
		bool HasActiveFilters()
		{
			if (m_Search != null && !string.IsNullOrEmpty(m_Search.value.Trim())) return true;
			if (!string.IsNullOrEmpty(m_RuleFilterValue)) return true;
			if (!string.IsNullOrEmpty(m_FixKindFilterValue)) return true;
			if (!string.IsNullOrEmpty(m_ReferencedFilterValue)) return true;
			if (m_FolderFilter != null && !string.IsNullOrEmpty(m_FolderFilter.Value)) return true;
			for (int i = 0; i < s_Severity.Length; i++)
				if (!s_Severity[i]) return true;
			return false;
		}

		/// <summary>
		/// Puts every filter control back to "show everything", then refilters once. "Issues only" is
		/// left alone: it is the tab's normal state rather than a filter someone applied.
		/// </summary>
		void ClearFilters()
		{
			m_Search?.SetValueWithoutNotify(string.Empty);

			for (int i = 0; i < m_SeverityChips.Count; i++) m_SeverityChips[i].On = true;
			for (int i = 0; i < s_Severity.Length; i++) s_Severity[i] = true;

			m_RuleFilter?.SetValue(null, false);
			m_RuleFilterValue = null;
			m_FixKindFilter?.SetValue(null, false);
			m_FixKindFilterValue = null;
			m_ReferencedFilterValue = null;
			m_ReferencedFilter?.SetValue(null, false);
			m_FolderFilter?.SetValue(null, false);
			s_FolderFilterValue = null;

			ApplyFilters();
			Feedback.Show("Filters cleared on this tab.");
			m_OnStateChanged?.Invoke();
		}

		// --- persistence ------------------------------------------------------------
		//
		// This page never touches disk itself - see OptimizerWindowState and ScanResultStore. It only
		// captures and re-applies its own logical state, on request from ProjectOptimizerWindow, which
		// is what actually owns when and where that state is written.

		/// <summary>
		/// Applies saved filter/search/column-width state before any data has been shown. Sequenced
		/// this way relative to <see cref="SetData"/> for one reason: the rule filter's value is
		/// re-validated by <see cref="FilterDropdown.SetTree"/> inside <see cref="RebuildFilterChoices"/>,
		/// which runs from <see cref="SetData"/> - so seeding it here, first, lets that existing
		/// validation drop a rule id that no longer fires on this tab, rather than this class having to
		/// duplicate that check.
		/// </summary>
		public void SeedUiState(TabUiState state)
		{
			if (state == null) return;

			if (!string.IsNullOrEmpty(state.Search)) m_Search?.SetValueWithoutNotify(state.Search);

			// Not synced through RebuildFilterChoices like the rule filter - the fix-kind tree is fixed
			// at construction time, so there is nothing for a rescan to invalidate it against.
			if (!string.IsNullOrEmpty(state.FixKindFilter))
			{
				m_FixKindFilterValue = state.FixKindFilter;
				m_FixKindFilter?.SetValue(state.FixKindFilter, false);
			}

			if (string.Equals(TabId, "duplicates", StringComparison.OrdinalIgnoreCase))
			{
				string referencedFilter = !string.IsNullOrEmpty(state.ReferencedFilter)
					? state.ReferencedFilter
					: (state.ReferencedOnly ? "referenced" : null); // migrate the previous boolean filter state
				if (!string.IsNullOrEmpty(referencedFilter))
				{
					m_ReferencedFilterValue = referencedFilter;
					m_ReferencedFilter?.SetValue(referencedFilter, false);
				}
			}

			// Seeded without notifying: RebuildFilterChoices reads this back out once the tree it
			// belongs to actually exists.
			if (!string.IsNullOrEmpty(state.RuleFilter)) m_RuleFilter?.SetValue(state.RuleFilter, false);

			if (state.ColumnWidths != null)
				foreach (var column in state.ColumnWidths)
					if (!string.IsNullOrEmpty(column.Key)) m_UserWidths[column.Key] = column.Width;
		}

		/// <summary>
		/// Applies saved sort and selection once real data is on screen. Both need the table's columns
		/// and <see cref="m_Filtered"/> to already reflect the restored scan, so this runs strictly
		/// after <see cref="SetData"/> - never combined with <see cref="SeedUiState"/>.
		/// </summary>
		public void RestoreSelectionAndSort(TabUiState state)
		{
			if (state == null || m_Table == null) return;

			if (state.Sort != null && state.Sort.Count > 0)
			{
				// sortColumnDescriptions has no public setter, only a public getter over the view's own
				// live collection - confirmed by mutating it and reading it back. Cleared and rebuilt in
				// place so the header shows the same sort arrows a user setting it would produce.
				var descriptions = m_Table.sortColumnDescriptions;
				descriptions.Clear();

				var specs = new List<KeyValuePair<ColumnSpec, bool>>();
				foreach (var saved in state.Sort)
				{
					if (string.IsNullOrEmpty(saved.Key)) continue;

					var spec = m_Columns.Find(c => c.Key == saved.Key);
					if (spec?.Sort == null) continue;

					var direction = saved.Ascending ? SortDirection.Ascending : SortDirection.Descending;
					descriptions.Add(new SortColumnDescription(saved.Key, direction));
					specs.Add(new KeyValuePair<ColumnSpec, bool>(spec, saved.Ascending));
				}

				// The rows are ordered directly from the saved column keys rather than by reading back
				// m_Table.sortedColumns: that collection is resolved by the view's own header/column
				// controller, which has no documented guarantee of having done so by the time a restore
				// - running as part of window construction - reaches this point.
				if (specs.Count > 0) SortFilteredBy(specs);
			}

			bool hasGuids = state.SelectedGuids != null && state.SelectedGuids.Count > 0;
			bool hasPaths = state.SelectedPaths != null && state.SelectedPaths.Count > 0;
			if (!hasGuids && !hasPaths) return;

			var wanted = new HashSet<string>(StringComparer.Ordinal);
			if (hasGuids)
			{
				foreach (var guid in state.SelectedGuids)
				{
					if (string.IsNullOrEmpty(guid)) continue;
					var resolved = AssetDatabase.GUIDToAssetPath(guid);
					// A blank result means the asset is gone; the identity simply drops out of the
					// restored selection instead of failing the rest of it - see class remarks on
					// selection identity in the persistence TODO this implements.
					if (!string.IsNullOrEmpty(resolved)) wanted.Add(resolved);
				}
			}
			if (hasPaths)
				foreach (var path in state.SelectedPaths)
					if (!string.IsNullOrEmpty(path)) wanted.Add(path);

			if (wanted.Count == 0) return;

			var indices = new List<int>();
			for (int i = 0; i < m_Filtered.Count; i++)
				if (wanted.Contains(m_Filtered[i].Path)) indices.Add(i);

			if (indices.Count == 0) return;

			// WithoutNotify: this is a restore, not a user action, and selectionChanged would otherwise
			// fire mid-restoration - see ProjectOptimizerWindow's own restoration guard.
			m_Table.SetSelectionWithoutNotify(indices);
			RefreshDetails();
			UpdateSelectionButton();
		}

		/// <summary>Everything about this tab worth writing down, in the shape <see cref="OptimizerWindowState"/> stores.</summary>
		public TabUiState CaptureUiState()
		{
			var state = new TabUiState
			{
				TabId = TabId,
				Search = m_Search != null ? m_Search.value : string.Empty,
				RuleFilter = m_RuleFilterValue,
				FixKindFilter = m_FixKindFilterValue,
				ReferencedFilter = m_ReferencedFilterValue
			};

			foreach (var pair in m_UserWidths)
				state.ColumnWidths.Add(new ColumnWidthEntry { Key = pair.Key, Width = pair.Value });

			if (m_Table != null)
			{
				foreach (var description in m_Table.sortedColumns)
				{
					state.Sort.Add(new SortEntry
					{
						Key = description.columnName,
						Ascending = description.direction == SortDirection.Ascending
					});
				}
			}

			foreach (var entry in SelectedEntries())
			{
				if (!string.IsNullOrEmpty(entry.Guid)) state.SelectedGuids.Add(entry.Guid);
				else if (!string.IsNullOrEmpty(entry.Path)) state.SelectedPaths.Add(entry.Path);
			}

			return state;
		}

		/// <summary>
		/// The severity chips, "issues only" and the folder filter are shared by every tab (see the
		/// remarks on <see cref="s_Severity"/>), so they are restored once for the whole window rather
		/// than per page. Must run before any <see cref="CategoryPage"/> is constructed: the severity
		/// chips and the issues-only chip read these statics in their own constructors.
		/// </summary>
		public static void RestoreSharedFilters(bool critical, bool warning, bool info, bool issuesOnly, string folder)
		{
			s_Severity[(int)IssueSeverity.Critical] = critical;
			s_Severity[(int)IssueSeverity.Warning] = warning;
			s_Severity[(int)IssueSeverity.Info] = info;
			s_IssuesOnly = issuesOnly;
			s_FolderFilterValue = folder;
		}

		public static (bool critical, bool warning, bool info, bool issuesOnly, string folder) CaptureSharedFilters()
		{
			return (s_Severity[(int)IssueSeverity.Critical], s_Severity[(int)IssueSeverity.Warning],
				s_Severity[(int)IssueSeverity.Info], s_IssuesOnly, s_FolderFilterValue);
		}

		void UpdateSelectionButton()
		{
			if (m_FixSelectedButton == null) return;

			var selected = SelectedEntries();
			int fixable = CountFixable(selected);
			m_FixSelectedButton.text = fixable > 0 ? "Fix selected (" + fixable + ")" : "Fix selected";
			m_FixSelectedButton.SetEnabled(fixable > 0);
			m_FixSelectedButton.tooltip = fixable > 0
				? "Applies " + fixable + " Verified fix(es) to the selected row(s)."
				: "Select rows that have a Verified fix to enable this.";

			// Keep the review control independently guarded so an incomplete UI rebuild
			// can never make a scan fail just because this optional action is unavailable.
			if (m_ReviewSelectedButton == null) return;

			int reviewActions = ReviewActionCatalog.DetectCommonActions(selected).Count;
			m_ReviewSelectedButton.text = reviewActions > 0
				? "Review " + reviewActions : "Review";
			m_ReviewSelectedButton.SetEnabled(reviewActions > 0);
			m_ReviewSelectedButton.tooltip = reviewActions > 0
				? reviewActions + " distinct Review Fix action(s) are offered somewhere in this selection. "
				  + "Pick one to see exactly how many selected assets are eligible."
				: "Select rows that share a Review Fix action to enable this.";
		}

		void ReviewSelected()
		{
			var selected = SelectedEntries();
			if (selected.Count == 0)
			{
				Feedback.Warn("Select one or more rows first.");
				return;
			}
			ReviewActionPickerDialog.Open(selected, m_AfterFix);
		}

		List<AssetEntry> SelectedEntries()
		{
			return m_Table.selectedItems?.OfType<AssetEntry>().ToList() ?? new List<AssetEntry>();
		}

		void RefreshDetails()
		{
			m_DetailsBody.Clear();
			var selected = SelectedEntries();

			if (selected.Count == 0)
			{
				m_DetailsBody.Add(UiKit.Empty("Nothing selected",
					"Pick a row to see every issue found on it, why it matters, and what the fix changes. "
					+ "Select several rows for a combined summary."));
				return;
			}

			if (selected.Count == 1) BuildSingleDetails(selected[0]);
			else BuildMultiDetails(selected);
		}

		/// <summary>
		/// Why the in-memory figure is so often many times the file on disk. This is the single most
		/// common thing to mistake for a bug in this window, and the answer is different per asset,
		/// so it is spelled out rather than left as a ratio to wonder about.
		/// </summary>
		static string RuntimeSizeExplanation(AssetEntry entry)
		{
			var text = "What the asset costs once loaded, measured with the settings it has now.";

			if (entry.Category == AssetCategory.Texture)
			{
				text += "\n\nPNG, JPG and PSD are compressed file formats, but the GPU cannot sample them: "
						+ "a texture is expanded to raw pixels when it loads. An uncompressed RGBA texture "
						+ "costs 4 bytes per pixel however small the file was, and a mip chain adds about "
						+ "another third on top. That is where a multiple of ten comes from.";

				var dimensions = entry.Field("dimensions") as string;
				bool mips = (entry.Field("mipmaps") as string) == "Yes";
				if (!string.IsNullOrEmpty(dimensions))
				{
					text += "\n\nHere: " + dimensions + " at "
							+ ((entry.Field("compression") as string) == "Uncompressed" ? "4 bytes" : "well under a byte")
							+ " per pixel" + (mips ? ", plus mip maps" : ", no mip maps") + ".";
				}

				text += "\n\nCompressing it is what closes the gap - that is TEX002.";
			}
			else if (entry.Category == AssetCategory.Audio)
			{
				text += "\n\nA compressed clip set to Decompress On Load is stored as raw PCM once loaded, "
						+ "which is far larger than the encoded file. Streaming or Compressed In Memory "
						+ "keeps the encoded form resident instead.";
			}
			else if (entry.Category == AssetCategory.Model)
			{
				text += "\n\nVertex data is expanded on load: mesh compression is a storage format, so the "
						+ "file shrinks but memory does not.";
			}

			return text;
		}

		/// <summary>
		/// Breaks the headline saving into the two pools it comes from, and says so explicitly when
		/// the per-fix figures add up to more than the asset can actually give back.
		/// </summary>
		static string SavingsExplanation(AssetEntry entry)
		{
			var text = "What applying every fix listed below would recover.";

			var parts = new List<string>();
			if (entry.PotentialMemorySavings > 0)
				parts.Add(DisplayUtility.FormatBytes(entry.PotentialMemorySavings) + " of runtime memory, out of "
						  + DisplayUtility.FormatBytes(entry.RuntimeSize) + " resident");
			if (entry.PotentialDiskSavings > 0)
				parts.Add(DisplayUtility.FormatBytes(entry.PotentialDiskSavings) + " off the file, out of "
						  + DisplayUtility.FormatBytes(entry.FileSize) + " on disk");
			if (parts.Count > 0)
			{
				text += "\n\nMemory and disk are separate pools and are never added together: memory "
						+ "you free is not download size you save."
						+ "\n\n" + string.Join("\n", parts.ToArray());
			}

			// The interesting case: several fixes acting on the same texture or clip overlap, so the
			// individual estimates are each true on their own and wrong when added together.
			if (entry.NaiveSavingsSum > entry.PotentialSavings + 1024)
			{
				text += "\n\nThe individual estimates below come to "
						+ DisplayUtility.FormatBytes(entry.NaiveSavingsSum)
						+ ", but several of them act on the same bytes - shrinking a texture and then "
						+ "compressing it does not save both amounts - so the real figure is lower.";
			}

			return text;
		}

		/// <summary>
		/// Pings the asset, and says so when it cannot. A ping that silently does nothing is the
		/// exact failure this tool already had once, when the Project window was behind the window.
		/// </summary>
		static void PingOrExplain(string path)
		{
			if (UnityEditorUtility.PingPath(path)) return;
			Feedback.Warn("Could not open " + System.IO.Path.GetFileName(path)
						  + " - it is no longer in the project. Rescan to refresh this list.");
		}

		/// <summary>
		/// Right-click menu for a table row. Dropped at the cursor rather than shown with
		/// <c>GenericMenu.ShowAsContext</c> - see <see cref="ShowExportMenu"/> in
		/// ProjectOptimizerWindow for why: that call needs a live IMGUI event, which a UI Toolkit
		/// callback never has. <paramref name="position"/> is <see cref="ContextClickEvent.mousePosition"/>,
		/// already in panel space (the same space as a VisualElement's <c>worldBound</c>), so it can be
		/// used as a zero-size anchor rect directly instead of anchoring on the whole (wide) row cell.
		/// </summary>
		static void ShowRowContextMenu(Vector2 position, AssetEntry entry)
		{
			var menu = new GenericMenu();

			menu.AddItem(new GUIContent("Ping in Project"), false, () => PingOrExplain(entry.Path));

			menu.AddItem(new GUIContent("Copy Name"), false, () => EditorGUIUtility.systemCopyBuffer = entry.Name);
			menu.AddItem(new GUIContent("Copy Path"), false, () => EditorGUIUtility.systemCopyBuffer = entry.Path);

			if (!string.IsNullOrEmpty(entry.Guid))
				menu.AddItem(new GUIContent("Copy GUID"), false, () => EditorGUIUtility.systemCopyBuffer = entry.Guid);
			else
				menu.AddDisabledItem(new GUIContent("Copy GUID"));

			menu.AddItem(new GUIContent("Show in Explorer"), false, () =>
			{
				var full = AssetPathUtility.ToAbsolutePath(entry.Path);
				if (System.IO.File.Exists(full) || System.IO.Directory.Exists(full))
					EditorUtility.RevealInFinder(full);
				else
					Feedback.Warn(System.IO.Path.GetFileName(entry.Path) + " is no longer on disk.");
			});

			if (entry.Category == AssetCategory.Script)
				menu.AddItem(new GUIContent("Open"), false, () => OpenScript(entry.Path, -1));

			menu.DropDown(new Rect(position, Vector2.zero));
		}

		/// <summary>Categories with an image worth showing bigger than the generic asset icon.</summary>
		static readonly HashSet<AssetCategory> k_PreviewableCategories = new HashSet<AssetCategory>
		{
			AssetCategory.Texture, AssetCategory.Material, AssetCategory.SpriteAtlas,
			AssetCategory.Model, AssetCategory.Font, AssetCategory.Prefab
		};

		/// <summary>
		/// A real thumbnail for the categories it is worth generating one for, or null to fall back to
		/// the plain type icon. Unity builds previews asynchronously, so a null the first time round is
		/// not necessarily "no preview" - <see cref="AssetPreview.IsLoadingAssetPreview"/> says which,
		/// and while it is still cooking this schedules exactly one re-check rather than polling
		/// forever: by the time that fires the preview is either cached (the retry succeeds) or it is
		/// never coming, and there is nothing more to gain from asking again.
		/// </summary>
		Texture GetPreviewTexture(AssetEntry entry)
		{
			if (!k_PreviewableCategories.Contains(entry.Category)) return null;

			var asset = entry.LoadAsset();
			if (asset == null) return null;

			var preview = AssetPreview.GetAssetPreview(asset);
			if (preview != null) return preview;

			if (AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID()))
				schedule.Execute(RefreshDetails).StartingIn(200);

			return null;
		}

		void BuildSingleDetails(AssetEntry entry)
		{
			var header = UiKit.Column("po-details-header");
			var titleRow = UiKit.Row();
			titleRow.style.alignItems = Align.Center;

			var previewTexture = GetPreviewTexture(entry);
			var iconTexture = previewTexture != null ? previewTexture : AssetDatabase.GetCachedIcon(entry.Path);
			titleRow.Add(UiKit.Icon(iconTexture, previewTexture != null ? 40f : 22f, "po-details-icon"));

			var title = UiKit.Text(entry.Name, "po-details-title");
			title.style.marginLeft = 8f;
			titleRow.Add(title);
			header.Add(titleRow);
			header.Add(UiKit.Text(entry.Path, "po-details-path"));

			var actions = UiKit.Row("po-details-actions");
			actions.Add(UiKit.Button("Select", () => PingOrExplain(entry.Path), "po-button-small"));

			if (entry.Category == AssetCategory.Script)
			{
				var open = UiKit.Button("Open", () => OpenScript(entry.Path, -1), "po-button-small");
				open.tooltip = "Opens the script in Unity's configured external script editor.";
				actions.Add(open);
			}

			var settingsPage = entry.Field("settingsPage") as string;
			if (!string.IsNullOrEmpty(settingsPage))
				actions.Add(UiKit.Button("Open Settings", () => SettingsService.OpenProjectSettings(settingsPage), "po-button-small"));

			int fixableHere = 0;
			foreach (var issue in entry.Issues) if (issue.AutoFixable) fixableHere++;
			if (fixableHere > 0)
			{
				actions.Add(UiKit.Button("Fix " + fixableHere,
					() => ApplyFixes(CollectRequests(new[] { entry })), "po-button-primary", "po-button-small"));
			}
			header.Add(actions);

			var meta = UiKit.Row("po-meta-row");
			meta.Add(UiKit.Meta("Type", entry.TypeLabel));
			meta.Add(UiKit.Meta("On disk", DisplayUtility.FormatBytes(entry.FileSize),
				"Size of the source file. This is what version control and the build carry, and for "
				+ "a compressed format such as PNG or OGG it says nothing about how big the asset is "
				+ "once loaded."));

			if (entry.RuntimeSize >= 0)
			{
				meta.Add(UiKit.Meta("In memory", DisplayUtility.FormatBytes(entry.RuntimeSize),
					RuntimeSizeExplanation(entry)));
			}

			if (entry.PotentialSavings > 0)
				meta.Add(UiKit.Meta("Est. saving", DisplayUtility.FormatBytes(entry.PotentialSavings),
					SavingsExplanation(entry)));
			header.Add(meta);
			m_DetailsBody.Add(header);

			if (entry.Issues.Count == 0)
			{
				m_DetailsBody.Add(UiKit.Empty("No issues", "This asset passes every enabled rule for " + m_Title + "."));
				return;
			}

			var ordered = new List<OptimizationIssue>(entry.Issues);
			ordered.Sort((a, b) => b.Severity.CompareTo(a.Severity));
			foreach (var issue in ordered)
				m_DetailsBody.Add(BuildIssueCard(entry, issue));
		}

		/// <summary>Aggregate view for a multi-row selection: totals, then the issues grouped by rule.</summary>
		void BuildMultiDetails(List<AssetEntry> selected)
		{
			long disk = 0, memory = 0, savings = 0;
			int issues = 0, fixable = 0, reviewable = 0;
			var byRule = new Dictionary<string, List<KeyValuePair<AssetEntry, OptimizationIssue>>>();

			foreach (var entry in selected)
			{
				disk += entry.FileSize;
				if (entry.RuntimeSize > 0) memory += entry.RuntimeSize;
				savings += entry.PotentialSavings;
				foreach (var issue in entry.Issues)
				{
					issues++;
					if (issue.AutoFixable) fixable++;
					else if (issue.FixKind == FixKind.Review) reviewable++;
					if (!byRule.TryGetValue(issue.RuleId, out var list))
					{
						list = new List<KeyValuePair<AssetEntry, OptimizationIssue>>();
						byRule[issue.RuleId] = list;
					}
					list.Add(new KeyValuePair<AssetEntry, OptimizationIssue>(entry, issue));
				}
			}

			var header = UiKit.Column("po-details-header");
			header.Add(UiKit.Text(selected.Count + " assets selected", "po-details-title"));
			header.Add(UiKit.Text(issues + " issue(s) across the selection", "po-details-path"));

			var actions = UiKit.Row("po-details-actions");
			actions.Add(UiKit.Button("Select in Project", () =>
			{
				var objects = new List<UnityEngine.Object>();
				foreach (var entry in selected)
				{
					var obj = AssetDatabase.LoadMainAssetAtPath(entry.Path);
					if (obj != null) objects.Add(obj);
				}
				Selection.objects = objects.ToArray();
				EditorUtility.FocusProjectWindow();

				// Rows can outlive their assets - a previous fix may have deleted one - and a
				// selection that quietly comes up short is worse than one that says so.
				if (objects.Count == 0)
					Feedback.Warn("None of those assets could be loaded. They may have been deleted.");
				else if (objects.Count < selected.Count)
					Feedback.Warn("Selected " + objects.Count + " of " + selected.Count
								  + "; the rest are no longer in the project.");
			}, "po-button-small"));

			if (fixable > 0)
			{
				actions.Add(UiKit.Button("Fix " + fixable,
					() => ApplyFixes(CollectRequests(selected), true), "po-button-primary", "po-button-small"));
			}

			int reviewActions = ReviewActionCatalog.DetectCommonActions(selected).Count;
			if (reviewActions > 0)
			{
				actions.Add(UiKit.Button("Review " + reviewActions,
					() => ReviewSelected(), "po-button-review", "po-button-small"));
			}
			header.Add(actions);

			var meta = UiKit.Row("po-meta-row");
			meta.Add(UiKit.Meta("On disk", DisplayUtility.FormatBytes(disk)));
			meta.Add(UiKit.Meta("In memory", memory > 0 ? DisplayUtility.FormatBytes(memory) : "-"));
			meta.Add(UiKit.Meta("Est. saving", DisplayUtility.FormatBytes(savings)));
			meta.Add(UiKit.Meta("Verified fixes", fixable + " / " + issues));
			if (reviewable > 0)
				meta.Add(UiKit.Meta("Review fixes", reviewable + " / " + issues,
					"Review actions require a human decision. Use Review selected above to choose one action "
					+ "and apply it across all eligible selected assets."));
			header.Add(meta);
			m_DetailsBody.Add(header);

			if (issues == 0)
			{
				m_DetailsBody.Add(UiKit.Empty("No issues", "None of the selected assets breaks an enabled rule."));
				return;
			}

			var groups = byRule.ToList();
			groups.Sort((a, b) =>
			{
				int bySeverity = b.Value[0].Value.Severity.CompareTo(a.Value[0].Value.Severity);
				return bySeverity != 0 ? bySeverity : b.Value.Count.CompareTo(a.Value.Count);
			});

			m_DetailsBody.Add(SectionTitle("Issues by rule"));
			foreach (var group in groups)
				m_DetailsBody.Add(BuildRuleGroup(group.Key, group.Value));

			m_DetailsBody.Add(SectionTitle("Selected assets"));
			int shown = Mathf.Min(selected.Count, 40);
			for (int i = 0; i < shown; i++)
			{
				var entry = selected[i];
				var row = UiKit.Row("po-summary-row");
				row.Add(UiKit.Pill(entry.HasIssues ? DisplayUtility.SeverityLabel(entry.WorstSeverity).ToUpperInvariant() : "CLEAN",
					entry.HasIssues ? DisplayUtility.SeverityClass(entry.WorstSeverity) : "sev-clean"));
				var path = UiKit.Text(entry.Path, "po-summary-path");
				path.style.marginLeft = 8f;
				row.Add(path);
				m_DetailsBody.Add(row);
			}
			if (selected.Count > shown)
			{
				var more = UiKit.Row("po-summary-row");
				more.Add(UiKit.Text("and " + (selected.Count - shown) + " more", "po-summary-path"));
				m_DetailsBody.Add(more);
			}
		}

		static Label SectionTitle(string text)
		{
			var label = UiKit.Text(text, "po-section-title");
			label.style.marginLeft = 12f;
			return label;
		}

		VisualElement BuildRuleGroup(string ruleId, List<KeyValuePair<AssetEntry, OptimizationIssue>> items)
		{
			var first = items[0].Value;
			var group = UiKit.Row("po-rule-group", DisplayUtility.SeverityClass(first.Severity));
			// Severity
			group.Add(UiKit.Pill(DisplayUtility.SeverityLabel(first.Severity).ToUpperInvariant(),
				DisplayUtility.SeverityClass(first.Severity)));

			// Title
			var title = UiKit.Text(first.Title, "po-rule-group-title");
			title.tooltip = first.Title;
			group.Add(title);

			long groupSavings = 0;
			int groupFixable = 0;
			int groupReviewable = 0;
			foreach (var item in items)
			{
				groupSavings += item.Value.EstimatedSavings;
				if (item.Value.AutoFixable) groupFixable++;
				else if (item.Value.FixKind == FixKind.Review) groupReviewable++;
			}

			var actionSlot = UiKit.Row("po-rule-group-action");

			if (groupFixable > 0)
			{
				actionSlot.Add(UiKit.Button("Fix " + groupFixable, () =>
				{
					var requests = new List<FixRequest>();
					foreach (var item in items)
						if (item.Value.AutoFixable)
							requests.Add(new FixRequest { Entry = item.Key, Issue = item.Value });
					ApplyFixes(requests, true);
				}, "po-button-primary", "po-button-small"));
			}
			else if (groupReviewable > 0)
			{
				// A rule-group containing Review Fixes must expose a real batch action, not
				// merely a green status label. Restrict the picker to the assets represented
				// by this rule group so the resulting Review Batch is scoped correctly.
				var groupEntries = new List<AssetEntry>();
				foreach (var item in items)
					groupEntries.Add(item.Key);

				int groupReviewActions = ReviewActionCatalog.DetectCommonActions(groupEntries).Count;
				string reviewLabel = groupReviewActions > 0
					? "Review " + groupReviewable
					: "Review unavailable";

				if (groupReviewActions > 0)
				{
					var reviewButton = UiKit.Button(reviewLabel,
						() => ReviewActionPickerDialog.Open(groupEntries, m_AfterFix),
						"po-button-review", "po-button-small");
					reviewButton.tooltip = groupReviewActions == 1
						? "Open the Review Fix for these " + groupReviewable
						  + " findings and choose the change to apply across eligible assets."
						: "Open the Review Fix picker for these " + groupReviewable
						  + " findings. Choose one action before applying it across eligible assets.";
					actionSlot.Add(reviewButton);
				}
				else
				{
					var reviewStatus = UiKit.Text(reviewLabel, "po-rule-group-action-status");
					actionSlot.Add(reviewStatus);
				}
			}
			else
			{
				var count = UiKit.Text(items.Count.ToString(), "po-rule-group-count");
				count.tooltip = items.Count + " asset(s) affected by this rule.";
				actionSlot.Add(count);
			}
			group.Add(actionSlot);
			group.tooltip = ruleId + ": " + first.Why;
			return group;
		}

		/// <summary>
		/// Lays out an issue's evidence: a dim label on the left, the value on the right, coloured by
		/// what it is. Paths get the filename in full and the folders dimmed behind it, and clicking
		/// one selects that asset - which is the whole reason for showing a path at all.
		/// </summary>
		VisualElement BuildIssueDetails(OptimizationIssue issue)
		{
			var block = UiKit.Column("po-detail-block");

			// The label gutter is only worth its width when something is in it. A rule whose whole
			// detail is one line of prose keeps its text against the left edge instead of being
			// pushed in by an empty column.
			bool labelled = false;
			foreach (var detail in issue.Details)
				if (!string.IsNullOrEmpty(detail.Label)) { labelled = true; break; }

			foreach (var detail in issue.Details)
			{
				var row = UiKit.Row("po-detail-row");

				if (labelled)
				{
					// Only the first line of a list carries the label; the rest align under it.
					row.Add(UiKit.Text(string.IsNullOrEmpty(detail.Label)
						? string.Empty
						: detail.Label.ToUpperInvariant(), "po-detail-label"));
				}

				row.Add(BuildDetailValue(detail));
				block.Add(row);
			}

			return block;
		}

		VisualElement BuildDetailValue(IssueDetail detail)
		{
			string kindClass = detail.Kind == DetailKind.Good ? "po-detail-good"
				: detail.Kind == DetailKind.Bad ? "po-detail-bad"
				: "po-detail-note";

			if (string.IsNullOrEmpty(detail.AssetPath))
			{
				var text = UiKit.Text(detail.Text, "po-detail-value", kindClass);
				return text;
			}

			// A path is read from the right: the file name is what identifies it, the folders are
			// context. Splitting them lets the name stay legible when the row has to wrap.
			var value = UiKit.Row("po-detail-value", "po-detail-path", kindClass);

			int slash = detail.Text.LastIndexOf('/');
			if (slash > 0)
			{
				var folder = UiKit.Text(detail.Text.Substring(0, slash + 1), "po-detail-folder");
				value.Add(folder);
			}

			var name = UiKit.Text(slash >= 0 ? detail.Text.Substring(slash + 1) : detail.Text, "po-detail-name");
			value.Add(name);

			value.tooltip = detail.AssetPath + "\n\nClick to select this asset in the Project window.";
			var path = detail.AssetPath;
			value.RegisterCallback<ClickEvent>(_ => PingOrExplain(path));
			return value;
		}

		VisualElement BuildIssueCard(AssetEntry entry, OptimizationIssue issue)
		{
			var card = UiKit.Column("po-issue-card", DisplayUtility.SeverityClass(issue.Severity));

			var head = UiKit.Row("po-issue-head");
			head.Add(UiKit.Pill(DisplayUtility.SeverityLabel(issue.Severity).ToUpperInvariant(),
				DisplayUtility.SeverityClass(issue.Severity)));
			head.Add(UiKit.Text(issue.Title, "po-issue-title"));

			// How sure the tool is, next to how much it costs. These answer different questions and
			// the severity pill alone was being read as both - which is how a filename heuristic
			// ends up looking as solid as a measured fact.
			var confidence = UiKit.Text(DisplayUtility.ConfidenceLabel(issue.Confidence), "po-issue-confidence");
			confidence.tooltip = DisplayUtility.ConfidenceExplanation(issue.Confidence);
			head.Add(confidence);

			// Fix kind answers a different question from confidence: not "how sure", but "what the
			// product lets you do about it". Shown here so both are visible on the same line rather
			// than one being read as a proxy for the other.
			if (issue.FixKind != FixKind.None)
			{
				var kind = UiKit.Text(issue.FixKind == FixKind.Verified ? "VERIFIED FIX" : "REVIEW FIX",
					"po-issue-confidence");
				kind.tooltip = issue.FixKind == FixKind.Verified
					? "Safe enough to apply without asking first. Included in Fix All."
					: "A real, executable action that still needs a human to look and approve it. "
					  + "Never included in Fix All.";
				head.Add(kind);
			}

			head.Add(UiKit.Text(issue.RuleId, "po-issue-code"));
			card.Add(head);
			card.tooltip = RuleCatalog.Describe(issue.RuleId, issue.Severity);

			if (issue.Stale && !string.IsNullOrEmpty(issue.StaleReason))
			{
				var staleNote = UiKit.Text("Out of date: " + issue.StaleReason, "po-issue-scope", "sev-warning");
				card.Add(staleNote);
			}

			// Where a quality-level finding came from. Without it "4 cascades" names a value and not
			// the place it was read, which is neither reproducible nor checkable.
			if (issue.QualityLevelIndex >= 0)
			{
				var where = UiKit.Text(
					"Measured on " + (issue.TargetPlatform ?? "this platform")
					+ ", quality level " + issue.QualityLevelIndex
					+ (string.IsNullOrEmpty(issue.QualityLevelName) ? "" : " (" + issue.QualityLevelName + ")"),
					"po-issue-scope");
				where.tooltip = "Most Quality Settings belong to one quality level rather than to the project. "
								+ "This says which level and platform this reading came from.";
				card.Add(where);
			}

			if (!string.IsNullOrEmpty(issue.Current) || !string.IsNullOrEmpty(issue.Recommended))
			{
				var change = UiKit.Row("po-change");
				change.Add(UiKit.Text(string.IsNullOrEmpty(issue.Current) ? "-" : issue.Current, "po-change-from"));
				change.Add(UiKit.Text("\u2192", "po-change-arrow"));
				change.Add(UiKit.Text(string.IsNullOrEmpty(issue.Recommended) ? "-" : issue.Recommended, "po-change-to"));
				card.Add(change);
			}

			card.Add(UiKit.Text(issue.Why, "po-issue-why"));

			// Evidence sits between the explanation and the advice: what the rule is, then what it
			// found here, then what to do about it.
			if (issue.Details.Count > 0) card.Add(BuildIssueDetails(issue));

			if (!string.IsNullOrEmpty(issue.FixHint))
				card.Add(UiKit.Text(issue.FixHint, "po-issue-fixhint"));

			var footer = UiKit.Row("po-issue-footer");
			var saving = UiKit.Text(issue.EstimatedSavings > 0
				? "Saves about " + DisplayUtility.FormatBytes(issue.EstimatedSavings) + " on its own"
				: string.Empty, "po-saving");
			if (issue.EstimatedSavings > 0)
			{
				saving.tooltip =
					"What this one fix saves if nothing else on this asset is changed, out of "
					+ (issue.Basis == SavingsBasis.Disk ? "the file on disk." : "runtime memory.")
					+ "\n\nFixes that draw on the same pool overlap, so these figures do not add up - "
					+ "the total in the header is what applying all of them actually recovers.";
			}
			else if (issue.Basis == SavingsBasis.Unknown)
			{
				saving.text = "Saving not measurable";
				saving.tooltip = "This fix helps, but by an amount the analyzer cannot compute for your project. "
								 + "It reports nothing rather than a number that would look precise and be invented.";
			}
			footer.Add(saving);

			if (!issue.Stale)
			{
				footer.Add(UiKit.Button("Suppress finding", () =>
				{
					string prompt = "Hide only " + issue.RuleId + " for this path:\n\n" + entry.Path + "\n\nOptional reason:";
					string reason = EditorUtility.DisplayDialog("Suppress finding", prompt, "Suppress", "Cancel")
						? "Intentional / accepted by project" : null;
					if (reason == null) return;
					var settings = OptimizerSettings.Instance;
					if (settings.Suppress(issue.RuleId, entry.Path, reason))
					{
						settings.Save();
						Feedback.Show("Suppressed " + issue.RuleId + " for this path.", FeedbackKind.Success);
						m_AfterFix?.Invoke(new List<string> { entry.Path });
					}
				}, "po-button-small"));
			}

			if (entry.Category == AssetCategory.Script && issue.SourceLine > 0)
			{
				footer.Add(UiKit.Button("Open line " + issue.SourceLine,
					() => OpenScript(entry.Path, issue.SourceLine), "po-button-small"));
			}

			if (issue.Stale)
			{
				// The finding is probably still true; what is gone is the tool's right to act on it
				// without looking again. Saying "no automated action" here would be wrong in both
				// directions - the button existed and will again once a fresh pass confirms it.
				var note = UiKit.Text("Rescan required", "po-issue-rule");
				note.tooltip = issue.StaleReason;
				footer.Add(note);
			}
			else if (issue.FixKind == FixKind.Verified)
			{
				footer.Add(UiKit.Button("Verified Fix", () => ApplyFixes(new List<FixRequest>
				{
					new FixRequest { Entry = entry, Issue = issue }
				}), "po-button-primary", "po-button-small"));
			}
			else if (issue.FixKind == FixKind.Review)
			{
				var reviewButton = UiKit.Button("Review Fix",
					() => ReviewFixDialog.Open(entry, issue, m_AfterFix), "po-button-small");
				reviewButton.tooltip = "A real action the tool can construct, but that needs a human to read "
									   + "and explicitly approve - it is never included in Fix All.";
				footer.Add(reviewButton);
			}
			else
			{
				footer.Add(UiKit.Text("No automated action", "po-issue-rule"));
			}
			card.Add(footer);
			return card;
		}

		/// <summary>
		/// Opens a script in Unity's configured external editor (Preferences > External Tools), at a
		/// line when one is known. Never a specific IDE - <c>AssetDatabase.OpenAsset</c> is Unity's own
		/// indirection for that, and this never touches the source itself.
		/// </summary>
		static void OpenScript(string path, int line)
		{
			var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
			if (asset == null)
			{
				Feedback.Warn("Could not open " + System.IO.Path.GetFileName(path)
							  + " - it is no longer in the project. Rescan to refresh this list.");
				return;
			}

			bool opened = line > 0 ? AssetDatabase.OpenAsset(asset, line) : AssetDatabase.OpenAsset(asset);
			if (!opened)
				Feedback.Warn("Unity could not open this file in the configured external script editor.");
		}

		// --- fixing --------------------------------------------------------------------

		static List<FixRequest> CollectRequests(IEnumerable<AssetEntry> entries)
		{
			var requests = new List<FixRequest>();
			foreach (var entry in entries)
				foreach (var issue in entry.Issues)
					if (issue.AutoFixable) requests.Add(new FixRequest { Entry = entry, Issue = issue });
			return requests;
		}

		void FixAllShown() => ApplyFixes(CollectRequests(m_Filtered), true);

		void FixSelected()
		{
			var selected = SelectedEntries();
			if (selected.Count == 0)
			{
				Feedback.Warn("Select one or more rows first.");
				return;
			}

			var requests = CollectRequests(selected);
			if (requests.Count == 0)
			{
				Feedback.Warn(selected.Count == 1
					? "That row has no issue this tool can fix automatically."
					: "None of those " + selected.Count + " rows has an automatic fix.");
				return;
			}
			ApplyFixes(requests, true);
		}

		void ApplyFixes(List<FixRequest> requests, bool confirm = false)
		{
			if (requests.Count == 0)
			{
				Feedback.Warn("There is nothing here with an automatic fix.");
				return;
			}

			// Confirmation happens here, before anything is locked or changed. It used to be asked
			// from inside the asset-editing scope, which meant cancelling had already altered the
			// editor's import settings and suspended auto-refresh for the length of the dialog.
			if (confirm)
			{
				var paths = new HashSet<string>(StringComparer.Ordinal);
				foreach (var request in requests) paths.Add(request.Entry.Path);

				bool proceed = EditorUtility.DisplayDialog("Apply fixes",
					$"Apply {requests.Count} fix(es) across {paths.Count} asset(s)?" +
					"Affected files are backed up first, so the whole batch can be reverted from the toolbar.",
					"Apply", "Cancel");
				if (!proceed)
				{
					Feedback.Show("Nothing was changed.");
					return;
				}
			}

			// FixEngine owns the transaction from here: the editing scope, the import settings it
			// tunes and restores, and when saves and reimports happen. This page used to set some of
			// that up itself while the Overview page did not, so the same fix behaved differently
			// depending on which button started it.
			var report = FixEngine.Apply(requests);

			LogFixReport(report);

			ReportFixOutcome(report);

			// Re-analyze what changed - including anything changed and then rolled back, whose rows
			// were written before either happened.
			var affected = new List<string>(report.TouchedPaths);
			affected.AddRange(report.RolledBackPaths);
			if (affected.Count > 0) m_AfterFix?.Invoke(affected);
		}

		/// <summary>
		/// Turns a fix report into one sentence. Partial failure is the case worth getting right:
		/// "applied 40" on its own would hide that 3 did not, and the console is not where someone
		/// who has just pressed a button is looking.
		/// </summary>
		internal static void ReportFixOutcome(FixReport report)
		{
			string assets = report.AssetsTouched == 1 ? "1 asset" : report.AssetsTouched + " assets";

			// A rollback that did not complete is the worst outcome this tool can produce: files on
			// disk are in a state nobody chose. It is reported first and never softened.
			if (report.RollbackFailed > 0)
			{
				Feedback.Error(report.RollbackFailed + " asset(s) could not be rolled back after a failed fix. "
							   + "Restore them from version control or from the backup folder - the console "
							   + "names them.", "Open console", OpenConsole);
				return;
			}

			// Blocked is worded as a refusal, not a failure. Nothing was attempted and nothing was
			// changed, and that is a deliberate stop rather than something that went wrong halfway.
			if (report.Blocked > 0 && report.Applied == 0 && report.Failed == 0)
			{
				Feedback.Error(report.Blocked + " fix(es) were refused because the affected files could not "
							   + "be backed up. Nothing was changed.", "Open console", OpenConsole);
				return;
			}

			if (report.VerificationFailed > 0)
			{
				// These were applied and then undone, because the project did not end up the way the
				// fix said it would. Saying so plainly is the point: an unverified change left in
				// place is exactly what this tool must not do.
				Feedback.Warn(report.VerificationFailed + " change(s) did not take effect and were rolled back"
							  + (report.Applied > 0 ? ", " + report.Applied + " other(s) applied across " + assets : "")
							  + ". The console says which.", "Open console", OpenConsole);
				return;
			}

			if (report.Applied == 0 && (report.Failed > 0 || report.Blocked > 0))
			{
				Feedback.Error("Every fix failed or was refused. The console says why.", "Open console", OpenConsole);
				return;
			}

			if (report.Failed > 0 || report.Blocked > 0 || report.RolledBack>0)
			{
				Feedback.Warn(Applied(report) + " across " + assets + ", but "
							  + (report.Failed + report.Blocked) + " did not run"
							  + (report.RolledBack > 0
								  ? " and " + report.RolledBack + " change(s) were rolled back."
								  : ".")
							  + " The console says why.", "Open console", OpenConsole);
				return;
			}

			if (report.Applied == 0)
			{
				Feedback.Show("Nothing needed changing.");
				return;
			}

			if (report.NoOp == report.Applied)
			{
				Feedback.Show(report.NoOp + " asset(s) were already in the state the rule wanted; nothing changed.");
				return;
			}

			// "Fixed" is reserved for changes that were confirmed afterwards. Where some were not,
			// the sentence says so rather than folding them into one reassuring number.
			if (!report.FullyVerified)
			{
				Feedback.Warn(Applied(report) + " across " + assets
							  + ". The unverified ones ran without error, but this tool cannot confirm what "
							  + "they did.", "Undo", Undo);
				return;
			}

			Feedback.Success(Applied(report) + " across " + assets + ".", "Undo", Undo);
		}

		/// <summary>
		/// How many fixes landed, split by how well the tool can vouch for them. Verified and
		/// unverified are never added together in a sentence shown to the user: they are different
		/// claims, and one number covering both is the more flattering of the two.
		/// </summary>
		static string Applied(FixReport report)
		{
			var parts = new List<string>();
			if (report.AppliedVerified > 0) parts.Add("Fixed and verified " + report.AppliedVerified);
			if (report.AppliedUnverified > 0) parts.Add((parts.Count == 0 ? "Applied " : "") + report.AppliedUnverified + " unverified");
			if (report.NoOp > 0) parts.Add(report.NoOp + " already correct");
			return parts.Count == 0 ? "Applied nothing" : string.Join(", ", parts.ToArray());
		}

		static void Undo()
		{
			if (!FixEngine.CanRevert)
			{
				Feedback.Warn("That batch is no longer the newest, so undo the newer one first.");
				return;
			}

			var result = FixEngine.RevertLastBatch();
			if (result.Complete)
			{
				Feedback.Success("Reverted " + result.Restored + " file(s).");
				return;
			}

			// A partial revert leaves the batch pending on purpose, so it can be retried. Saying
			// "reverted" here would be a lie about a half-restored project.
			Feedback.Error("Only " + result.Restored + " of " + result.Expected + " file(s) could be restored. "
						   + "The batch is still available to retry once the cause is dealt with.",
						   "Open console", OpenConsole);
		}

		/// <summary>Puts the full detail in the console, where the one-line summary cannot fit it.</summary>
		internal static void LogFixReport(FixReport report)
		{
			var summary = "verified " + report.AppliedVerified
						  + ", unverified " + report.AppliedUnverified
						  + ", already correct " + report.NoOp
						  + ", failed " + report.Failed
						  + ", blocked " + report.Blocked
						  + ", rolled back " + report.RolledBack
						  + " across " + report.AssetsTouched + " asset(s)";

			if (report.Errors.Count == 0)
			{
				Debug.Log(ProjectOptimizerConstants.LogPrefix + " Fix batch: " + summary + ".");
				return;
			}

			var detail = ProjectOptimizerConstants.LogPrefix + " Fix batch: " + summary + "." + string.Join("", report.Errors.ToArray());

			if (report.RollbackFailed > 0 || report.Failed > 0) Debug.LogError(detail);
			else Debug.LogWarning(detail);
		}

		internal static void OpenConsole()
		{
			EditorApplication.ExecuteMenuItem("Window/General/Console");
		}
	}
}

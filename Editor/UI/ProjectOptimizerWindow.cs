using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One entry in the left navigation rail.</summary>
	[Flags]
	public enum AssetMetricColumns
	{
		None = 0,
		DiskSize = 1,
		RuntimeSize = 2,
		Savings = 4,
		All = DiskSize | RuntimeSize | Savings
	}

	public sealed class TabDefinition
	{
		public string Id;
		public string Title;
		public string Section;
		public AssetCategory[] Categories;
		public ColumnSpec[] Columns;
		public AssetMetricColumns AssetMetrics = AssetMetricColumns.All;
		public string PrimaryColumnTitle = "Asset";
	}

	/// <summary>
	/// The Project Optimizer window: scans every asset in the project against the rule catalog,
	/// then lets you filter, sort, inspect and batch-fix the findings, tab by tab.
	/// </summary>
	public sealed partial class ProjectOptimizerWindow : EditorWindow
	{
		const string k_OverviewId = "overview";
		const string k_RuntimeId = "runtime";
		const string k_ScopeId = "scope";
		const string k_RulesId = "rules";

		ProjectScanner m_Scanner;
		ScanResult m_Result;
		List<TabDefinition> m_Tabs;

		VisualElement m_Root;
		VisualElement m_NavContainer;
		VisualElement m_PageContainer;
		VisualElement m_ProgressFill;
		StyleSheet m_ThemeStyleSheet;
		Label m_StatusLabel;
		VisualElement m_StatsContainer;
		Button m_ScanButton;
		Button m_RevertButton;
		Button m_LoadBuildReportButton;
		Button m_LoadScanReportButton;

		readonly Dictionary<string, VisualElement> m_Pages = new Dictionary<string, VisualElement>();
		readonly Dictionary<string, VisualElement> m_NavItems = new Dictionary<string, VisualElement>();
		string m_ActiveTab = k_OverviewId;

		OverviewPage m_Overview;
		RuntimeEvidencePage m_RuntimePage;
		RulesPage m_RulesPage;
		ScanScopePage m_ScopePage;

		ToastHost m_Toasts;
		CommandPalette m_Palette;
		VisualElement m_StaleBanner;
		Label m_StaleLabel;

		/// <summary>Throttles the progress readout: the scan reports far faster than anyone can read.</summary>
		double m_NextProgressRepaint;

		/// <summary>Flips every tick so the progress fill breathes gently while a scan is running.</summary>
		bool m_ProgressPulseOn;
		bool m_AmbientPhase;

		/// <summary>
		/// True while CreateGUI is applying saved state. Suppresses <see cref="RequestSaveWindowState"/>
		/// so that seeding a filter, a selection or a tab during restoration cannot itself be mistaken
		/// for a user change and written straight back out - see PART 6 of the persistence TODO this
		/// implements.
		/// </summary>
		bool m_RestoringState;

		/// <summary>Set by <see cref="RequestSaveWindowState"/>, cleared by the poll that actually writes it.</summary>
		bool m_WindowStateDirty;

		[MenuItem(ProjectOptimizerConstants.MenuPath)]
		public static void Open()
		{
			var window = GetWindow<ProjectOptimizerWindow>();
			window.titleContent = new GUIContent(ProjectOptimizerConstants.WindowTitle, EditorGUIUtility.IconContent("d_UnityEditor.ProfilerWindow").image);
			window.minSize = new Vector2(1020f, 560f);
			window.Show();
		}

		void OnEnable()
		{
			// Constructing the scanner instantiates every analyzer, which registers their rules.
			m_Scanner = new ProjectScanner();
			m_Scanner.Completed += OnScanCompleted;
			EditorApplication.update += OnEditorUpdate;
			Feedback.Posted += OnFeedback;
			OptimizerThemes.Changed += OnThemeChanged;
			FixEngine.PruneOldBackups();
		}

		void OnDisable()
		{
			m_RestoreStateDeferred = false;
			m_RestoreOperation = null;
			EditorApplication.update -= TickRestoreOperation;
			EditorApplication.update -= OnEditorUpdate;
			Feedback.Posted -= OnFeedback;
			OptimizerThemes.Changed -= OnThemeChanged;
			if (m_Scanner != null)
			{
				m_Scanner.Completed -= OnScanCompleted;
				m_Scanner.Cancel();
			}
		}

		void OnFeedback(Feedback.Message message)
		{
			m_Toasts?.Post(message);
		}

		void OnEditorUpdate()
		{
			if (m_Scanner == null || !m_Scanner.IsRunning) return;
			m_Scanner.Tick(24.0);

			// That tick may have been the last one. The completion handler has already written the
			// summary by now, so painting progress over it would replace "Scanned 7,430 assets in
			// 3 min" with a stale "100% ... Done" a fraction of a second later.
			if (!m_Scanner.IsRunning) return;

			// Pushing a new status string every frame repaints the whole window on every tick, which
			// on a large project costs more than the analysis it is reporting on.
			double now = EditorApplication.timeSinceStartup;
			if (now < m_NextProgressRepaint) return;
			m_NextProgressRepaint = now + 0.1;
			UpdateProgress();
		}

		// --- construction ---------------------------------------------------------

		void CreateGUI()
		{
			m_Root = new VisualElement();
			m_Root.AddToClassList("po-root");

			// Ambient decoration is intentionally lightweight: two non-picking accent glows are
			// repositioned at a very low cadence so the shell feels alive without becoming a per-frame
			// rendering cost.
			var ambient = new VisualElement();
			ambient.AddToClassList("po-ambient");
			ambient.pickingMode = PickingMode.Ignore;
			var glowA = new VisualElement();
			glowA.AddToClassList("po-ambient-glow-a");
			var glowB = new VisualElement();
			glowB.AddToClassList("po-ambient-glow-b");
			ambient.Add(glowA);
			ambient.Add(glowB);
			m_Root.Add(ambient);
			m_Root.schedule.Execute(() =>
			{
				if (m_Root == null) return;
				m_AmbientPhase = !m_AmbientPhase;
				glowA.style.translate = m_AmbientPhase ? new Translate(12f, 7f) : new Translate(-10f, -5f);
				glowB.style.translate = m_AmbientPhase ? new Translate(-12f, -6f) : new Translate(10f, 8f);
				glowA.style.scale = m_AmbientPhase ? new Scale(new Vector3(1.04f, 1.04f, 1f)) : new Scale(Vector3.one);
				glowB.style.scale = m_AmbientPhase ? new Scale(new Vector3(0.97f, 0.97f, 1f)) : new Scale(new Vector3(1.03f, 1.03f, 1f));
			}).Every(1800);

			m_Root.focusable = true;
			m_Root.tabIndex = -1;

			var styleSheet = LoadStyleSheet();
			if (styleSheet != null) m_Root.styleSheets.Add(styleSheet);

			m_Root.Add(BuildToolbar());
			m_Root.Add(BuildProgressBar());
			m_Root.Add(BuildStaleBanner());

			var body = UiKit.Row("po-body");
			m_NavContainer = UiKit.Column("po-nav");
			m_PageContainer = UiKit.Column();
			m_PageContainer.style.flexGrow = 1;
			body.Add(m_NavContainer);
			body.Add(m_PageContainer);
			m_Root.Add(body);

			rootVisualElement.Add(m_Root);

			// Sits over the whole window rather than inside a page, so a confirmation posted while
			// switching tabs is still shown, and always in the same place. Added inside m_Root, not
			// beside it: the stylesheet is attached to m_Root, so a sibling would get none of the
			// theme variables. Added last so it draws over the body.
			m_Toasts = new ToastHost();
			m_Root.Add(m_Toasts);

			// Above the toasts too - a command should always be reachable, even with a toast on screen.
			m_Palette = new CommandPalette();
			m_Root.Add(m_Palette);

			// Trickle-down on the root: this has to see Ctrl+K before whatever currently has focus
			// does, or typing "k" while a search box elsewhere is focused would never reach here.
			m_Root.RegisterCallback<KeyDownEvent>(OnGlobalKeyDown, TrickleDown.TrickleDown);

			// Do not restore the previous scan synchronously from CreateGUI. A large persisted result
			// can require thousands of live analyzer calls, and doing that here prevents Unity from
			// painting the window at all, which appears as an endless "Opening window" operation.
			// Build the shell first, then restore state on the next editor turn.
			EditorApplication.delayCall += RestoreStateDeferred;

			// Polled rather than pushed: settings are changed from four different pages plus the
			// platform dropdown, and a poll cannot be forgotten when a fifth is added. Saving window
			// state is polled for the same reason, plus one more: it coalesces a burst of small changes
			// - dragging a column border fires several - into a single write.
			rootVisualElement.schedule.Execute(UpdateStaleBanner).Every(500);
			rootVisualElement.schedule.Execute(FlushWindowStateIfDirty).Every(500);
			rootVisualElement.schedule.Execute(PulseProgressWhileScanning).Every(650);
		}

		// --- command palette -----------------------------------------------------------

		void OnGlobalKeyDown(KeyDownEvent evt)
		{
			bool backQuoteShortcut = evt.keyCode == KeyCode.BackQuote;

			bool commandShortcut =
				(evt.ctrlKey || evt.commandKey) &&
				evt.keyCode == KeyCode.K;

			if (!backQuoteShortcut && !commandShortcut)
				return;

			ToggleCommandPalette();

			evt.StopImmediatePropagation();
		}

		void ToggleCommandPalette()
		{
			if (m_Palette == null)
				return;

			if (m_Palette.IsOpen)
			{
				m_Palette.Close();

				// Important: the palette's search field may still own keyboard focus
				// even after the palette has been hidden.
				m_Root?.Focus();
			}
			else
			{
				m_Palette.Open(BuildPaletteCommands());
			}
		}

		/// <summary>
		/// The full command list, rebuilt fresh on every open rather than cached: whether a scan is
		/// running, whether there is a result to export, whether a fix batch can be reverted, and which
		/// theme is current all change constantly, and a stale list would offer actions that no longer
		/// apply or omit ones that just became available.
		/// </summary>
		List<PaletteCommand> BuildPaletteCommands()
		{
			var commands = new List<PaletteCommand>();

			foreach (var tab in m_Tabs)
			{
				var id = tab.Id;
				var title = tab.Title;
				commands.Add(new PaletteCommand
				{
					Title = "Go to " + title,
					Category = "Navigate",
					Execute = () => Select(id)
				});
			}

			commands.Add(new PaletteCommand
			{
				Title = m_Scanner.IsRunning ? "Cancel scan" : "Scan project",
				Subtitle = m_Scanner.IsRunning ? "Stop the running scan" : "Analyze every asset in the current scan scope",
				Category = "Scan",
				Execute = ToggleScan
			});

			if (!m_Scanner.IsRunning && ScanResultStore.HasSnapshot)
			{
				commands.Add(new PaletteCommand
				{
					Title = "Load saved scan report",
					Subtitle = "Load the last completed scan snapshot from " + ProjectOptimizerConstants.ScanSnapshotPath,
					Category = "Scan",
					Execute = LoadScanReportIntoWindow
				});
			}

			if (m_Result != null)
			{
				commands.Add(new PaletteCommand
				{
					Title = "Rescan with current settings",
					Category = "Scan",
					Execute = RequestRescan
				});
				commands.Add(new PaletteCommand
				{
					Title = "Export CSV",
					Category = "Export",
					Execute = () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Csv)
				});
				commands.Add(new PaletteCommand
				{
					Title = "Export JSON",
					Category = "Export",
					Execute = () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Json)
				});
				commands.Add(new PaletteCommand
				{
					Title = "Export Markdown",
					Category = "Export",
					Execute = () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Markdown)
				});
				commands.Add(new PaletteCommand
				{
					Title = "Export SARIF",
					Subtitle = "Machine-readable findings for CI and code-scanning systems",
					Category = "Export",
					Execute = () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Sarif)
				});
			}

			if (m_Result != null && m_Result.IsComplete)
			{
				commands.Add(new PaletteCommand { Title = "Save optimization baseline", Subtitle = "Capture this complete scan as the explicit before/after reference", Category = "Baseline", Execute = SaveBaseline });
			}
			if (OptimizationBaselineStore.Exists)
				commands.Add(new PaletteCommand { Title = "Clear optimization baseline", Category = "Baseline", Execute = ClearBaseline });

			if (FixEngine.CanRevert)
			{
				commands.Add(new PaletteCommand
				{
					Title = "Revert last fixes",
					Subtitle = FixEngine.PendingBackupCount + " batch(es) available to revert",
					Category = "Fix",
					Execute = RevertLastFixes
				});
			}

			commands.Add(new PaletteCommand
			{
				Title = ProjectOptimizerConstants.RefreshAddressablesAnalyzeCommand,
				Subtitle = "Run the configured Addressables Analyze evidence pass and refresh its freshness state",
				Category = "Evidence",
				Execute = RefreshAddressablesAnalyzeEvidence
			});
			commands.Add(new PaletteCommand { Title = ProjectOptimizerConstants.RebuildProjectStateCommand, Category = "Diagnostics", Execute = RebuildProjectState });
			commands.Add(new PaletteCommand { Title = ProjectOptimizerConstants.ReconcileProjectStateCommand, Category = "Diagnostics", Execute = RequestProjectStateReconcile });
			commands.Add(new PaletteCommand { Title = ProjectOptimizerConstants.FlushProjectStateCommand, Category = "Diagnostics", Execute = FlushProjectStateSnapshot });
			commands.Add(new PaletteCommand { Title = ProjectOptimizerConstants.LogProjectStateDiagnosticsCommand, Category = "Diagnostics", Execute = LogProjectStateDiagnostics });
			commands.Add(new PaletteCommand { Title = ProjectOptimizerConstants.RunIntegrityAuditCommand, Category = "Diagnostics", Execute = RunIntegrityAudit });

			foreach (var theme in OptimizerThemes.All)
			{
				var id = theme.Id;
				commands.Add(new PaletteCommand
				{
					Title = "Theme: " + theme.DisplayName,
					Subtitle = theme.Description,
					Category = "Appearance",
					Execute = () => OptimizerThemes.Select(id)
				});
			}

			return commands;
		}

		/// <summary>
		/// Gives the progress fill a slow breathing opacity while a scan is running, so a long stretch
		/// at the same percentage still reads as "working" rather than "stuck". Purely visual: driven by
		/// toggling one class on a poll, the same idiom already used for the stale banner and the window
		/// state flush, and it costs nothing while idle since the early-out below skips the toggle.
		/// </summary>
		void PulseProgressWhileScanning()
		{
			if (m_ProgressFill == null) return;
			if (m_Scanner == null || !m_Scanner.IsRunning)
			{
				if (m_ProgressPulseOn)
				{
					m_ProgressPulseOn = false;
					m_ProgressFill.RemoveFromClassList("po-progress-pulse-dim");
				}
				return;
			}
			m_ProgressPulseOn = !m_ProgressPulseOn;
			m_ProgressFill.EnableInClassList("po-progress-pulse-dim", m_ProgressPulseOn);
		}

		ScanResultStore.RestoreOperation m_RestoreOperation;
		OptimizerWindowState m_RestoreState;
		bool m_RestoreStateDeferred;

		void RestoreStateDeferred()
		{
			if (this == null || m_RestoreStateDeferred) return;
			m_RestoreStateDeferred = true;
			try
			{
				RestoreState();
			}
			catch (Exception e)
			{
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Failed while restoring window state: " + e);
				if (m_StatusLabel != null) m_StatusLabel.text = "Ready - window state could not be restored";
				UpdateReportButtons();
			}
		}

		// --- state persistence -----------------------------------------------------------
		//
		// Everything the window shows that has meaningful continuity - which tab, the filters, sort,
		// column widths, selection, and a completed scan - is restored here after a domain reload
		// instead of reverting to a blank window. See OptimizerWindowState (small UI/logical state,
		// ScriptableSingleton-backed) and ScanResultStore (the scan itself, a dedicated Library cache)
		// for where each half actually lives.

		/// <summary>
		/// Applies saved UI/window state without loading the scan snapshot. The snapshot is a separate,
		/// explicit user action so opening the editor window remains deterministic and cheap.
		/// </summary>
		void RestoreState()
		{
			m_RestoringState = true;
			try
			{
				var state = OptimizerWindowState.instance;
				state.EnsureLoaded();

				ApplyTheme(state.ThemeId);

				// Read by CategoryPage's own constructor (the severity chips, the issues-only chip), so
				// this has to happen before BuildTabs/BuildPages creates any of them.
				CategoryPage.RestoreSharedFilters(state.SeverityCritical, state.SeverityWarning,
					state.SeverityInfo, state.IssuesOnly, state.FolderFilter);
				m_StaleAcknowledged = state.StaleAcknowledgedRevision;

				m_Tabs = BuildTabs();
				BuildPages();
				BuildNav();

				foreach (var tab in m_Tabs)
				{
					if (m_Pages.TryGetValue(tab.Id, out var page) && page is CategoryPage categoryPage)
						categoryPage.SeedUiState(state.FindTab(tab.Id));
				}

				m_ActiveTab = !string.IsNullOrEmpty(state.ActiveTab) && m_Pages.ContainsKey(state.ActiveTab)
					? state.ActiveTab
					: k_OverviewId;
				Select(m_ActiveTab);

				UpdateStats();
				UpdateStaleBanner();

				// Scan snapshots are deliberately not loaded on window open. Opening the tool should be
				// a cheap, predictable UI operation; the persisted scan is an explicit user action via
				// Load Scan Report. UI preferences/layout are still restored immediately.
				m_RestoreState = state;
				m_StatusLabel.text = ScanResultStore.HasSnapshot
					? "Ready - saved scan available"
					: "Ready - run a scan or load a saved scan report";
				UpdateReportButtons();
			}
			finally
			{
				m_RestoringState = false;
			}
		}

		/// <summary>Processes an explicitly requested saved scan report load in editor-friendly slices.</summary>
		void TickRestoreOperation()
		{
			if (m_RestoreOperation == null)
			{
				EditorApplication.update -= TickRestoreOperation;
				return;
			}

			try
			{
				m_RestoreOperation.Tick(8.0);
			}
			catch (Exception e)
			{
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Saved scan report load failed: " + e);
				m_RestoreOperation = null;
				EditorApplication.update -= TickRestoreOperation;
				m_StatusLabel.text = "Ready - saved scan report could not be loaded";
				m_Root?.RemoveFromClassList("po-restoring");
				return;
			}

			if (!m_RestoreOperation.IsComplete)
			{
				if (m_StatusLabel != null)
					m_StatusLabel.text = m_RestoreOperation.Status;
				return;
			}

			var restored = m_RestoreOperation.Result;
			m_RestoreOperation = null;
			EditorApplication.update -= TickRestoreOperation;

			if (restored == null)
			{
				m_StatusLabel.text = "Ready";
				m_Root?.RemoveFromClassList("po-restoring");
				return;
			}

			m_Result = restored;
			PublishResult(restored);

			OptimizerWindowState state = m_RestoreState ?? OptimizerWindowState.instance;
			foreach (var tab in m_Tabs)
			{
				if (m_Pages.TryGetValue(tab.Id, out var page) && page is CategoryPage categoryPage)
					categoryPage.RestoreSelectionAndSort(state.FindTab(tab.Id));
			}

			m_Root?.RemoveFromClassList("po-restoring");
			m_StatusLabel.text = "Loaded scan report from " + restored.ScanTime.ToString("g") + ".";
			Feedback.Show(
				restored.CrossAssetPassesStale
					? "Loaded " + restored.IssueCount + " finding(s) from your saved scan report. Duplicate/unused/"
					  + "cross-material findings need a rescan to confirm; everything else is unaffected."
					: "Loaded " + restored.IssueCount + " finding(s) from your saved scan report.");
			UpdateStats();
			UpdateStaleBanner();
			UpdateReportButtons();
		}

		/// <summary>
		/// Marks window state dirty for the next poll to write out. Never called directly from a UI
		/// callback into a file write: see the poll in CreateGUI for why, and see PART 7 of the
		/// persistence TODO this implements for why saving is triggered from many call sites rather
		/// than only from OnDisable.
		/// </summary>
		void RequestSaveWindowState()
		{
			if (m_RestoringState) return;
			m_WindowStateDirty = true;
		}

		void FlushWindowStateIfDirty()
		{
			if (!m_WindowStateDirty) return;
			m_WindowStateDirty = false;
			SaveWindowState();
		}

		void SaveWindowState()
		{
			var state = OptimizerWindowState.instance;
			state.ActiveTab = m_ActiveTab;
			state.StaleAcknowledgedRevision = m_StaleAcknowledged;

			var shared = CategoryPage.CaptureSharedFilters();
			state.SeverityCritical = shared.critical;
			state.SeverityWarning = shared.warning;
			state.SeverityInfo = shared.info;
			state.IssuesOnly = shared.issuesOnly;
			state.FolderFilter = shared.folder;

			state.Tabs.Clear();
			if (m_Tabs != null)
			{
				foreach (var tab in m_Tabs)
				{
					if (m_Pages.TryGetValue(tab.Id, out var page) && page is CategoryPage categoryPage)
						state.Tabs.Add(categoryPage.CaptureUiState());
				}
			}

			state.Persist();
		}

		/// <summary>
		/// The strip that appears when the settings on screen no longer match the ones the visible
		/// findings were produced with.
		/// </summary>
		VisualElement BuildStaleBanner()
		{
			m_StaleBanner = UiKit.Row("po-banner");
			m_StaleLabel = UiKit.Text(string.Empty, "po-banner-text");
			m_StaleBanner.Add(m_StaleLabel);
			m_StaleBanner.Add(UiKit.Spacer());
			m_StaleBanner.Add(UiKit.Button("Rescan now", RequestRescan, "po-button-small", "po-button-primary"));
			m_StaleBanner.Add(UiKit.Button("Dismiss", () =>
			{
				// Acknowledged rather than fixed: the results stay stale, the reminder stops.
				m_StaleAcknowledged = OptimizerSettings.Revision;
				UpdateStaleBanner();
				RequestSaveWindowState();
			}, "po-button-small"));
			m_StaleBanner.style.display = DisplayStyle.None;
			return m_StaleBanner;
		}

		/// <summary>Revision the user last said they were happy to ignore.</summary>
		int m_StaleAcknowledged = -1;

		void UpdateStaleBanner()
		{
			if (m_StaleBanner == null) return;

			bool settingsChanged = m_Result != null
								&& m_Result.SettingsRevision != OptimizerSettings.Revision
								&& m_StaleAcknowledged != OptimizerSettings.Revision;

			bool crossAssetStale = m_Result != null
								&& m_Result.CrossAssetPassesStale
								&& m_StaleAcknowledged != OptimizerSettings.Revision;

			bool stale = settingsChanged || crossAssetStale;

			m_StaleBanner.style.display = stale ? DisplayStyle.Flex : DisplayStyle.None;
			if (!stale) return;

			if (crossAssetStale && settingsChanged)
				m_StaleLabel.text = "Settings have changed since this scan, and its duplicate, unused and "
									+ "cross-material findings cannot be acted on until you rescan.";
			else if (crossAssetStale)
			{
				// CrossAssetStaleReason names the actual cause - an edited/deleted file, or (after a
				// restore) a domain reload the comparison could not survive - rather than the one fixed
				// sentence that used to run regardless of which of those it actually was.
				m_StaleLabel.text = !string.IsNullOrEmpty(m_Result.CrossAssetStaleReason)
					? m_Result.CrossAssetStaleReason
					: "Duplicate, unused and cross-material findings are out of date. Rescan to act on them.";
			}
			else
				m_StaleLabel.text = "Settings have changed since this scan, so what you are looking at was "
									+ "produced with the old ones.";
		}

		static StyleSheet LoadStyleSheet()
		{
			var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
				ProjectOptimizerConstants.PackageAssetPath("Editor/UI/ProjectOptimizerStyles.uss"));
			if (sheet != null) return sheet;
			Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " ProjectOptimizerStyles.uss was not found; the window will use default styling.");
			return null;
		}

		// --- themes ------------------------------------------------------------------

		void OnThemeChanged(string themeId) => ApplyTheme(themeId);

		/// <summary>
		/// Swaps in the USS file for <paramref name="themeId"/> as the window's one active theme
		/// stylesheet, replacing whichever was loaded before. The base stylesheet
		/// (ProjectOptimizerStyles.uss) stays loaded throughout and is written entirely in terms of the
		/// --po-* custom properties a theme file defines, so swapping this one stylesheet is enough to
		/// re-colour the whole window - nothing else needs to change or rebuild.
		/// </summary>
		void ApplyTheme(string themeId)
		{
			if (m_Root == null) return;

			var theme = OptimizerThemes.Get(themeId);
			var sheet = LoadThemeStyleSheet(theme.AssetName);

			m_Root.style.opacity = 0.94f;
			if (m_ThemeStyleSheet != null) m_Root.styleSheets.Remove(m_ThemeStyleSheet);
			m_ThemeStyleSheet = sheet;
			if (sheet != null) m_Root.styleSheets.Add(sheet);
			m_Root.schedule.Execute(() =>
			{
				if (m_Root != null) m_Root.style.opacity = 1f;
			}).StartingIn(16);
		}

		static StyleSheet LoadThemeStyleSheet(string assetName)
		{
			var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
				ProjectOptimizerConstants.PackageAssetPath("Editor/UI/Themes/" + assetName + ".uss"));
			if (sheet != null) return sheet;
			Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Theme stylesheet '" + assetName + "' was not found; the base "
							  + "stylesheet's own fallback colours will be used instead.");
			return null;
		}

		VisualElement BuildToolbar()
		{
			var toolbar = UiKit.Row("po-toolbar");

			toolbar.Add(UiKit.Text(ProjectOptimizerConstants.ProductName, "po-title"));
			toolbar.Add(UiKit.Text("//", "po-title-mark"));

			m_ScanButton = UiKit.Button("Scan Project", ToggleScan, "po-button-primary");
			m_ScanButton.tooltip =
				"Scans everything the Scan Scope page selects.\n\n"
				+ "Script compilation and automatic asset importing are paused while a scan runs, so that "
				+ "Unity cannot reload the domain part-way through and discard it. Both resume the moment the "
				+ "scan finishes or is cancelled.";
			toolbar.Add(m_ScanButton);

			// Explicit report loaders keep opening the window cheap and make persisted data a conscious action.
			m_LoadScanReportButton = UiKit.Button(
				"Load Scan Report",
				LoadScanReportIntoWindow,
				"po-button-small", "po-button-report");
			m_LoadScanReportButton.tooltip =
				"Load the last completed Project Optimizer scan snapshot from "
				+ ProjectOptimizerConstants.ScanSnapshotPath + ".\n\n"
				+ "Opening the window never loads this automatically, and loading it does not run a new scan.";
			toolbar.Add(m_LoadScanReportButton);

			m_LoadBuildReportButton = UiKit.Button(
				"Load Build Report",
				LoadBuildReportIntoWindow,
				"po-button-small", "po-button-report");
			m_LoadBuildReportButton.tooltip =
				"Load the latest persisted Player BuildReport from "
				+ ProjectOptimizerConstants.BuildReportLatestPath + " and show its "
				+ "Build Hotspots, Build Steps, Build Output and Packages data.\n\n"
				+ "This does not perform a full project scan.";
			toolbar.Add(m_LoadBuildReportButton);

			var platformField = UiKit.PlatformSelector(() =>
			{
				m_RulesPage?.Rebuild();

				if (m_Result != null)
					RequestRescan();
			});

			platformField.style.marginLeft = 8f;
			toolbar.Add(platformField);

			m_StatusLabel = UiKit.Text(string.Empty, "po-status");
			toolbar.Add(m_StatusLabel);

			toolbar.Add(UiKit.Spacer());

			m_StatsContainer = UiKit.Row();
			m_StatsContainer.style.alignItems = Align.Center;
			toolbar.Add(m_StatsContainer);

			var commandsButton = UiKit.Button(
				"Commands",
				() => m_Palette.Open(BuildPaletteCommands()),
				"po-button-small");

			commandsButton.tooltip =
				"Jump to any tab, scan, export, revert or switch theme from one search box.\n\n"
				+ "Shortcuts: ` or Ctrl+K (Cmd+K on macOS).";

			toolbar.Add(commandsButton);

			Button exportButton = null;
			exportButton = UiKit.Button(
				"Export",
				() => ShowExportMenu(exportButton),
				"po-button-small");

			toolbar.Add(exportButton);

			m_RevertButton = UiKit.Button(
				"Revert last fixes",
				RevertLastFixes,
				"po-button-danger",
				"po-button-small");

			toolbar.Add(m_RevertButton);

			return toolbar;
		}

		void UpdateReportButtons()
		{
			bool canLoadScan = !m_Scanner.IsRunning && ScanResultStore.HasSnapshot;
			m_LoadScanReportButton?.SetEnabled(canLoadScan);
			m_LoadBuildReportButton?.SetEnabled(!m_Scanner.IsRunning);
		}

		void LoadScanReportIntoWindow()
		{
			if (m_Scanner == null)
			{
				Feedback.Error("The scanner has not been initialized yet.");
				return;
			}

			if (m_Scanner.IsRunning || m_RestoreOperation != null)
			{
				Feedback.Warn("Finish or cancel the current operation before loading a saved scan report.");
				return;
			}

			if (!ScanResultStore.HasSnapshot)
			{
				Feedback.Warn("No saved scan report exists yet. Run a scan first.", "Scan project", StartScan);
				UpdateReportButtons();
				return;
			}

			try
			{
				m_RestoreState = OptimizerWindowState.instance;
				m_StatusLabel.text = "Loading saved scan report...";
				m_RestoreOperation = ScanResultStore.BeginRestore(m_Scanner, OptimizerSettings.Instance);
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not start scan report load: " + e.Message);
				m_RestoreOperation = null;
				Feedback.Error("The saved scan report could not be loaded. See the Console for details.");
				UpdateReportButtons();
				return;
			}

			if (m_RestoreOperation == null)
			{
				m_StatusLabel.text = "Ready - saved scan report unavailable";
				Feedback.Warn("The saved scan report is missing, invalid, or from an incompatible Unity/tool version.");
				UpdateReportButtons();
				return;
			}

			EditorApplication.update -= TickRestoreOperation;
			EditorApplication.update += TickRestoreOperation;
			UpdateReportButtons();
		}

		void LoadBuildReportIntoWindow()
		{
			if (m_Scanner == null)
			{
				Feedback.Error("The scanner has not been initialized yet.");
				return;
			}

			if (m_Scanner.IsRunning)
			{
				Feedback.Warn("Cannot load the BuildReport while a project scan is running.");
				return;
			}

			try
			{
				// -------------------------------------------------------------
				// 1. Load the persisted BuildReport.
				// -------------------------------------------------------------
				var buildReport = BuildReportStore.LoadOrCaptureLatest();

				if (buildReport == null)
				{
					m_StatusLabel.text = "No BuildReport found";

					Feedback.Warn(
						"No persisted BuildReport was found.\n\n"
						+ "Make a Player build first, then press Load Build Report.");

					return;
				}

				var analyzer = new ProjectAuditorSupplementAnalyzer();

				var ctx = new ScanContext
				{
					Settings = OptimizerSettings.Instance
				};

				// -------------------------------------------------------------
				// 2. IMPORTANT:
				// Do NOT call analyzer.Analyze(ctx).
				// That runs unrelated project-settings analysis.
				// -------------------------------------------------------------
				var entries = new List<AssetEntry>();

				foreach (var entry in analyzer.AnalyzeBuild(ctx))
				{
					if (entry != null)
						entries.Add(entry);
				}

				// -------------------------------------------------------------
				// 3. Packages are NOT BuildReport data.
				// Explicitly load them as their own Insights category.
				// -------------------------------------------------------------
				foreach (var package in analyzer.AnalyzePackagesOnly(ctx))
				{
					if (package != null)
						entries.Add(package);
				}

				// -------------------------------------------------------------
				// 4. Count what we actually loaded.
				// -------------------------------------------------------------
				int buildCount = 0;
				int buildStepCount = 0;
				int buildFileCount = 0;
				int packageCount = 0;

				foreach (var entry in entries)
				{
					switch (entry.Category)
					{
						case AssetCategory.Build:
							buildCount++;
							break;

						case AssetCategory.BuildStep:
							buildStepCount++;
							break;

						case AssetCategory.BuildFile:
							buildFileCount++;
							break;

						case AssetCategory.Package:
							packageCount++;
							break;
					}
				}

				// -------------------------------------------------------------
				// 5. Create a ScanResult if there isn't one yet.
				// -------------------------------------------------------------
				if (m_Result == null)
				{
					m_Result = new ScanResult
					{
						ScanTime = DateTime.Now,
						DurationSeconds = 0.0,
						SettingsRevision = OptimizerSettings.Revision
					};
				}

				// -------------------------------------------------------------
				// 6. Replace only BuildReport + Package categories.
				// Leave all normal project scan data untouched.
				// -------------------------------------------------------------
				var replaceCategories = new HashSet<AssetCategory>
		{
			AssetCategory.Build,
			AssetCategory.BuildStep,
			AssetCategory.BuildFile,
			AssetCategory.Package
		};

				m_Result.Entries.RemoveAll(
					entry => entry != null && replaceCategories.Contains(entry.Category));

				foreach (var pair in m_Result.ByCategory)
				{
					pair.Value.RemoveAll(
						entry => entry != null && replaceCategories.Contains(entry.Category));
				}

				foreach (var entry in entries)
				{
					if (entry == null)
						continue;

					m_Result.Add(entry);
				}

				m_Result.Recount();

				// -------------------------------------------------------------
				// 7. Refresh every page and navigation badge.
				// -------------------------------------------------------------
				PublishResult(m_Result);

				// -------------------------------------------------------------
				// 8. Save the updated combined result.
				// -------------------------------------------------------------
				try
				{
					ScanResultStore.Save(m_Result);
				}
				catch (Exception e)
				{
					Debug.LogWarning(
						ProjectOptimizerConstants.LogPrefix + " Could not persist the BuildReport/package "
						+ "Insights result: " + e.Message);
				}

				// -------------------------------------------------------------
				// 9. Update toolbar status.
				// -------------------------------------------------------------
				string resultText =
					buildReport.summary != null &&
					!string.IsNullOrEmpty(buildReport.summary.result)
						? buildReport.summary.result
						: "Unknown";

				long buildSize =
					buildReport.summary != null
						? buildReport.summary.totalSize
						: 0;

				double buildDuration =
					buildReport.summary != null
						? buildReport.summary.totalTimeSeconds
						: 0.0;

				m_StatusLabel.text =
					"BuildReport loaded | "
					+ DisplayUtility.FormatBytes(buildSize)
					+ " | "
					+ DisplayUtility.FormatDuration(buildDuration)
					+ " | "
					+ resultText;

				Feedback.Success(
					"Loaded "
					+ buildCount + " hotspots, "
					+ buildStepCount + " build steps, "
					+ buildFileCount + " output files, "
					+ packageCount + " packages.",
					"Packages",
					() => Select("packages"));
			}
			catch (Exception e)
			{
				Debug.LogException(e);

				m_StatusLabel.text = "BuildReport load failed";

				Feedback.Error(
					"Failed to load BuildReport/Package Insights: " + e.Message,
					"Open console",
					() => EditorApplication.ExecuteMenuItem("Window/General/Console"));
			}
		}

		VisualElement BuildProgressBar()
		{
			var track = new VisualElement();
			track.AddToClassList("po-progress");
			m_ProgressFill = new VisualElement();
			m_ProgressFill.AddToClassList("po-progress-fill");
			track.Add(m_ProgressFill);
			return track;
		}

		List<TabDefinition> BuildTabs()
		{
			return new List<TabDefinition>
			{
				new TabDefinition { Id = k_OverviewId, Title = "Overview", Section = "Summary" },

				new TabDefinition
				{
					Id = "textures", Title = "Textures", Section = "Assets",
					Categories = new[] { AssetCategory.Texture },
					Columns = new[]
					{
						ColumnSpec.Field("dimensions", "Size", 90, false, null, "pixels"),
						ColumnSpec.Field("format", "Format", 110),
						ColumnSpec.Field("maxSize", "Max", 54, true),
						ColumnSpec.Field("compression", "Compression", 100),
						ColumnSpec.Flag("crunch", "Crunch", 62, false),
						ColumnSpec.Flag("mipmaps", "Mips", 52, false),
						ColumnSpec.Flag("readWrite", "R/W", 48),
						ColumnSpec.Flag("streaming", "Stream", 58, false),
						ColumnSpec.Field("override", "Override", 78)
					}
				},
				new TabDefinition
				{
					Id = "audio", Title = "Audio", Section = "Assets", PrimaryColumnTitle = "Audio Clip",
					Categories = new[] { AssetCategory.Audio },
					Columns = new[]
					{
						ColumnSpec.Field("lengthText", "Length", 68, true, null, "length"),
						ColumnSpec.Field("channels", "Ch", 40, true),
						ColumnSpec.Field("frequency", "Rate", 60, true),
						ColumnSpec.Field("loadType", "Load type", 132),
						ColumnSpec.Field("format", "Format", 78),
						ColumnSpec.Field("quality", "Quality", 58, true),
						ColumnSpec.Flag("preload", "Preload", 62),
						ColumnSpec.Flag("mono", "Mono", 52, false)
					}
				},
				new TabDefinition
				{
					Id = "video", Title = "Video", Section = "Assets", PrimaryColumnTitle = "Video Clip",
					Categories = new[] { AssetCategory.Video },
					Columns = new[]
					{
						ColumnSpec.Field("lengthText", "Length", 68, true, null, "length"),
						ColumnSpec.Field("dimensions", "Resolution", 92, false, null, "pixels"),
						ColumnSpec.Field("frameRate", "FPS", 46, true),
						ColumnSpec.Field("audioTracks", "Audio", 52, true),
						ColumnSpec.Flag("transcode", "Transcode", 76)
					}
				},
				new TabDefinition
				{
					Id = "models", Title = "Models & Meshes", Section = "Assets", PrimaryColumnTitle = "Model",
					Categories = new[] { AssetCategory.Model },
					Columns = new[]
					{
						ColumnSpec.Field("vertices", "Verts", 62, true),
						ColumnSpec.Field("triangles", "Tris", 62, true),
						ColumnSpec.Field("meshes", "Meshes", 58, true),
						ColumnSpec.Field("submeshes", "Sub", 46, true),
						ColumnSpec.Flag("readWrite", "R/W", 48),
						ColumnSpec.Field("compression", "Compression", 90),
						ColumnSpec.Field("rig", "Rig", 78),
						ColumnSpec.Field("optimize", "Optimize", 92),
						ColumnSpec.Field("indexFormat", "Index", 60)
					}
				},
				new TabDefinition
				{
					Id = "materials", Title = "Materials & Shaders", Section = "Assets", PrimaryColumnTitle = "Material / Shader", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Material, AssetCategory.Shader },
					Columns = new[]
					{
						ColumnSpec.Field("shader", "Shader", 230),
						ColumnSpec.Flag("instancing", "Instancing", 76, false),
                        // Two usage columns, not one total. A slot counted inside a prefab asset and
                        // a slot counted inside a scene are different units - see MaterialUsageIndex.
                        ColumnSpec.Field("sceneSlots", "In scenes", 76, true),
						ColumnSpec.Field("prefabSlots", "In prefabs", 80, true),
						ColumnSpec.Field("keywords", "Keywords", 72, true),
						ColumnSpec.Field("passes", "Passes", 58, true)
					}
				},
				new TabDefinition
				{
					Id = "animations", Title = "Animations", Section = "Assets", PrimaryColumnTitle = "Animation", AssetMetrics = AssetMetricColumns.DiskSize | AssetMetricColumns.RuntimeSize,
					Categories = new[] { AssetCategory.Animation },
					Columns = new[]
					{
						ColumnSpec.Field("lengthText", "Length", 68, true, null, "length"),
						ColumnSpec.Field("curves", "Curves", 62, true),
						ColumnSpec.Field("keyframes", "Keys", 62, true),
						ColumnSpec.Field("frameRate", "FPS", 46, true),
						ColumnSpec.Field("compression", "Compression", 96),
						ColumnSpec.Flag("legacy", "Legacy", 60)
					}
				},
				new TabDefinition
				{
					Id = "controllers", Title = "Animator Controllers", Section = "Assets", PrimaryColumnTitle = "Controller", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Controller },
					Columns = new[]
					{
						ColumnSpec.Field("clips", "Clips", 48, true),
						ColumnSpec.Field("layers", "Layers", 58, true),
						ColumnSpec.Field("states", "States", 58, true),
						ColumnSpec.Field("parameters", "Params", 60, true)
					}
				},
				new TabDefinition
				{
					Id = "fonts", Title = "Fonts", Section = "Assets", PrimaryColumnTitle = "Font", AssetMetrics = AssetMetricColumns.DiskSize | AssetMetricColumns.RuntimeSize,
					Categories = new[] { AssetCategory.Font },
					Columns = new[]
					{
						ColumnSpec.Field("atlas", "Atlas", 92, false, null, "atlasPixels"),
						ColumnSpec.Field("pages", "Pages", 52, true),
						ColumnSpec.Field("format", "Format", 84),
						ColumnSpec.Field("padding", "Padding", 62, true),
						ColumnSpec.Field("fallbacks", "Fallbacks", 70, true)
					}
				},
				new TabDefinition
				{
					Id = "atlases", Title = "Sprite Atlases", Section = "Assets", PrimaryColumnTitle = "Sprite Atlas", AssetMetrics = AssetMetricColumns.DiskSize | AssetMetricColumns.RuntimeSize,
					Categories = new[] { AssetCategory.SpriteAtlas },
					Columns = new[]
					{
						ColumnSpec.Field("packables", "Packables", 74, true),
						ColumnSpec.Field("maxSize", "Max", 54, true),
						ColumnSpec.Field("format", "Format", 84),
						ColumnSpec.Flag("readWrite", "R/W", 48),
						ColumnSpec.Flag("tight", "Tight", 52, false)
					}
				},
				new TabDefinition
				{
					Id = "world", Title = "World Data", Section = "Assets", PrimaryColumnTitle = "World Asset", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Other },
					Columns = new[]
					{
						ColumnSpec.Field("heightmap", "Heightmap", 84, true),
						ColumnSpec.Field("alphamap", "Alphamap", 84, true),
						ColumnSpec.Field("detail", "Detail", 72, true),
						ColumnSpec.Field("layers", "Layers", 58, true),
						ColumnSpec.Field("trees", "Trees", 58, true),
						ColumnSpec.Field("details", "Details", 64, true),
						ColumnSpec.Field("size", "Terrain Size", 110)
					}
				},
				new TabDefinition
				{
					Id = "uitoolkit", Title = "UI Toolkit", Section = "Content", PrimaryColumnTitle = "UXML / USS", AssetMetrics = AssetMetricColumns.DiskSize,
					Categories = new[] { AssetCategory.UIToolkit },
					Columns = new[]
					{
						ColumnSpec.Field("elements", "Elements", 72, true),
						ColumnSpec.Field("maxDepth", "Depth", 54, true),
						ColumnSpec.Field("selectors", "Selectors", 72, true),
						ColumnSpec.Field("templates", "Templates", 76, true),
						ColumnSpec.Field("lines", "Lines", 58, true)
					}
				},
				new TabDefinition
				{
					Id = "scripts", Title = "Scripts", Section = "Content", PrimaryColumnTitle = "Script", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Script },
					Columns = new[]
					{
						ColumnSpec.Field("lines", "Lines", 58, true),
						ColumnSpec.Field("perFrameIssues", "Per-frame", 74, true),
						ColumnSpec.Field("logs", "Logs", 52, true)
					}
				},
				new TabDefinition
				{
					Id = "prefabs", Title = "Prefabs", Section = "Content", PrimaryColumnTitle = "Prefab", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Prefab },
					Columns = new[]
					{
						ColumnSpec.Field("objects", "Objects", 66, true),
						ColumnSpec.Field("depth", "Depth", 54, true),
						ColumnSpec.Field("renderers", "Renderers", 74, true),
						ColumnSpec.Field("missing", "Missing", 64, true)
					}
				},
				new TabDefinition
				{
					Id = "scenes", Title = "Scenes", Section = "Content", PrimaryColumnTitle = "Scene", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Scene },
					Columns = new[]
					{
						ColumnSpec.Field("objects", "Objects", 66, true),
						ColumnSpec.Field("depth", "Depth", 54, true),
						ColumnSpec.Field("renderers", "Renderers", 74, true),
						ColumnSpec.Field("lights", "Lights", 56, true),
						ColumnSpec.Field("missing", "Missing", 64, true)
					}
				},
				new TabDefinition
				{
					Id = "duplicates", Title = "Duplicates", Section = "Content", PrimaryColumnTitle = "Duplicate / Group",
					AssetMetrics = AssetMetricColumns.DiskSize | AssetMetricColumns.Savings,
					Categories = new[] { AssetCategory.Duplicate },
					Columns = new[]
					{
						ColumnSpec.Field("role", "Role", 74),
						ColumnSpec.Field("copies", "Copies", 58, true),
						ColumnSpec.Field("original", "Kept copy", 260),
						ColumnSpec.Field("referenceProof", "Reference proof", 116),
						ColumnSpec.Field("fixState", "Fix state", 100)
					}
				},
				new TabDefinition
				{
					Id = "unused", Title = "Unused", Section = "Content", PrimaryColumnTitle = "Unused Asset",
					AssetMetrics = AssetMetricColumns.DiskSize | AssetMetricColumns.Savings,
					Categories = new[] { AssetCategory.Unused },
					Columns = new[]
					{
						ColumnSpec.Flag("referenced", "Referenced", 78, false),
						ColumnSpec.Flag("reachable", "In build", 68, false),
						ColumnSpec.Flag("resources", "Resources", 76),
						ColumnSpec.Field("proof", "Proof", 110),
						ColumnSpec.Field("loaderRisk", "Load risk", 150)
					}
				},
				new TabDefinition
				{
					Id = "project", Title = "Project Settings", Section = "Project", PrimaryColumnTitle = "Setting", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.ProjectSetting },
					Columns = new[]
					{
						ColumnSpec.Field("stripping", "Stripping", 88),
						ColumnSpec.Field("backend", "Backend", 78),
						ColumnSpec.Field("batching", "Batching", 140),
						ColumnSpec.Field("pipeline", "Pipeline", 150),
						ColumnSpec.Field("srpBatcher", "SRP Batcher", 90)
					}
				},
				new TabDefinition { Id = k_RuntimeId, Title = "Runtime Evidence", Section = "Insights" },
				new TabDefinition
				{
					Id = "rendering", Title = "Rendering Evidence", Section = "Insights", PrimaryColumnTitle = "Source", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Rendering },
					Columns = new[]
					{
						ColumnSpec.Field("evidence", "Evidence", 220),
						ColumnSpec.Field("issueCount", "Findings", 70, true),
						ColumnSpec.Field("fileSize", "Source", 84, true, v => DisplayUtility.FormatBytes(Convert.ToInt64(v)))
					}
				},
				new TabDefinition
				{
					Id = "addressables", Title = "Addressables", Section = "Insights", PrimaryColumnTitle = "Bundle / Finding", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Addressables },
					Columns = new[]
					{
						ColumnSpec.Field("fileSize", "File size", 92, true, v => DisplayUtility.FormatBytes(Convert.ToInt64(v))),
						ColumnSpec.Field("dependencyCount", "Deps", 54, true),
						ColumnSpec.Field("dependencySize", "Dep size", 92, true, v => DisplayUtility.FormatBytes(Convert.ToInt64(v))),
						ColumnSpec.Field("worstEfficiency", "Worst eff.", 82, true, v => Convert.ToSingle(v).ToString("0.#") + "%"),
						ColumnSpec.Field("assetCount", "Assets", 62, true),
						ColumnSpec.Field("uncompressedSize", "Uncompressed", 100, true, v => DisplayUtility.FormatBytes(Convert.ToInt64(v)))
					}
				},
				new TabDefinition
				{
					Id = "build", Title = "Build Hotspots", Section = "Insights", PrimaryColumnTitle = "Build Asset", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Build },
					Columns = new[]
					{
						ColumnSpec.Field("packedSize", "Packed", 88, true),
						ColumnSpec.Field("share", "Build %", 72, true, v => (v is double d ? d.ToString("0.##") : v.ToString()) + "%"),
						ColumnSpec.Field("runtimeType", "Runtime Type", 150),
						ColumnSpec.Field("importer", "Importer", 130),
						ColumnSpec.Field("buildFile", "Build File", 220)
					}
				},
				new TabDefinition
				{
					Id = "buildsteps", Title = "Build Steps", Section = "Insights", PrimaryColumnTitle = "Build Step", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.BuildStep },
					Columns = new[]
					{
						ColumnSpec.Field("duration", "Duration", 88, true, v => (v is double d ? d.ToString("0.##") : v.ToString()) + " s"),
						ColumnSpec.Field("depth", "Depth", 54, true),
						ColumnSpec.Field("messages", "Messages", 72, true)
					}
				},
				new TabDefinition
				{
					Id = "buildfiles", Title = "Build Output", Section = "Insights", PrimaryColumnTitle = "Build File", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.BuildFile },
					Columns = new[]
					{
						ColumnSpec.Field("outputSize", "Size", 88, true, v => DisplayUtility.FormatBytes(Convert.ToInt64(v))),
						ColumnSpec.Field("share", "Build %", 72, true, v => (v is double d ? d.ToString("0.##") : v.ToString()) + "%"),
						ColumnSpec.Field("role", "Role", 170),
						ColumnSpec.Field("path", "Path", 300)
					}
				},
				new TabDefinition
				{
					Id = "packages", Title = "Packages", Section = "Insights", PrimaryColumnTitle = "Package", AssetMetrics = AssetMetricColumns.None,
					Categories = new[] { AssetCategory.Package },
					Columns = new[]
					{
						ColumnSpec.Field("version", "Version", 120),
						ColumnSpec.Field("source", "Source", 110),
						ColumnSpec.Field("dependencyType", "Direct / Transitive", 112),
						ColumnSpec.Field("depth", "Depth", 58, true),
						ColumnSpec.Field("status", "Status", 86)
					}
				},
				new TabDefinition { Id = k_ScopeId, Title = "Scan Scope", Section = "Settings" },
				new TabDefinition { Id = k_RulesId, Title = "Rules & Thresholds", Section = "Settings" },
			};
		}

		void BuildPages()
		{
			m_Overview = new OverviewPage(Select, RefreshAfterFix, OptimizeSafeFixes);
			AddPage(k_OverviewId, m_Overview);
			m_RuntimePage = new RuntimeEvidencePage();
			AddPage(k_RuntimeId, m_RuntimePage);

			foreach (var tab in m_Tabs)
			{
				if (tab.Categories == null) continue;
				bool isInsights = string.Equals(tab.Section, "Insights", StringComparison.Ordinal);
				AddPage(tab.Id, new CategoryPage(tab.Id, tab.Title, tab.Categories, tab.Columns,
					RefreshAfterFix, RequestSaveWindowState, isInsights, tab.AssetMetrics, tab.PrimaryColumnTitle));
			}

			m_ScopePage = new ScanScopePage(RequestRescan);
			AddPage(k_ScopeId, m_ScopePage);

			m_RulesPage = new RulesPage(RequestRescan);
			AddPage(k_RulesId, m_RulesPage);
		}

		void AddPage(string id, VisualElement page)
		{
			page.AddToClassList("po-page-hidden");
			m_Pages[id] = page;
			m_PageContainer.Add(page);
		}

		void BuildNav()
		{
			m_NavContainer.Clear();
			m_NavItems.Clear();

			string section = null;
			foreach (var tab in m_Tabs)
			{
				if (tab.Section != section)
				{
					section = tab.Section;
					m_NavContainer.Add(UiKit.Text(section.ToUpperInvariant(), "po-nav-section"));
				}

				var item = UiKit.Row("po-nav-item");
				item.Add(UiKit.Text(tab.Title, "po-nav-label"));

				// Reads "assets affected | total issues".
				var badge = UiKit.Text(string.Empty, "po-nav-badge");
				badge.style.display = DisplayStyle.None;
				item.Add(badge);

				var id = tab.Id;
				item.RegisterCallback<ClickEvent>(_ => Select(id));
				m_NavItems[id] = item;
				m_NavContainer.Add(item);
			}
			UpdateNavBadges();
		}

		// --- navigation ------------------------------------------------------------

		void Select(string tabId)
		{
			if (!m_Pages.TryGetValue(tabId, out var incoming)) return;
			bool changed = m_ActiveTab != tabId;
			m_ActiveTab = tabId;

			foreach (var pair in m_Pages)
				pair.Value.EnableInClassList("po-page-hidden", pair.Key != tabId);

			foreach (var pair in m_NavItems)
				pair.Value.EnableInClassList("po-selected", pair.Key == tabId);

			// Only when the tab actually changes: re-selecting the current one (e.g. a stray click)
			// should not restart the settle-in every time.
			if (changed) AnimatePageIn(incoming);

			RequestSaveWindowState();
		}

		/// <summary>
		/// Eases a newly shown page in from slightly below and transparent, instead of the flat
		/// instant swap a display-toggle alone gives. <c>display</c> itself cannot be transitioned, so
		/// this animates opacity/translate on top of it - the same two-state trick already used for the
		/// toasts: set the "before" values, then move to the "after" ones a scheduled tick later so the
		/// USS transition on <c>.po-page</c> has something to animate between.
		/// </summary>
		static void AnimatePageIn(VisualElement page)
		{
			page.style.opacity = 0f;
			page.style.translate = new Translate(0, 8);
			page.schedule.Execute(() =>
			{
				page.style.opacity = 1f;
				page.style.translate = new Translate(0, 0);
			}).StartingIn(0);
		}

		// --- scanning ----------------------------------------------------------------

		void ToggleScan()
		{
			if (m_Scanner.IsRunning)
			{
				m_Scanner.Cancel();
				m_ScanButton.text = "Scan Project";
				m_StatusLabel.text = ScanResultStore.HasSnapshot ? "Cancelled - saved scan available" : "Cancelled";
				UpdateReportButtons();
				m_ProgressFill.style.width = Length.Percent(0f);
				m_ProgressFill.RemoveFromClassList("po-progress-pulse");
				m_ProgressFill.RemoveFromClassList("po-progress-pulse-dim");
				m_Root?.RemoveFromClassList("po-scanning");
				Feedback.Warn("Scan cancelled. Script compilation is back on.");
				return;
			}
			StartScan();
		}

		void StartScan()
		{
			m_ScanButton.text = "Cancel";
			m_StatusLabel.text = "Starting - script compilation paused until the scan ends";
			m_Root?.AddToClassList("po-scanning");
			UpdateReportButtons();
			m_NextProgressRepaint = 0.0;
			m_ProgressFill.AddToClassList("po-progress-pulse");

			// The scan refuses to start unless it can hold the editor still, so the button has to be
			// put back rather than left saying "Cancel" over a scan that never began.
			if (!m_Scanner.Begin(OptimizerSettings.Instance))
			{
				m_ScanButton.text = "Scan";
				m_StatusLabel.text = "Could not start";
				m_Root?.RemoveFromClassList("po-scanning");
				Feedback.Error("The scan could not start: Unity would not let the tool pause script "
							   + "compilation and asset refreshing. The console says which. Nothing was scanned.",
							   "Open console", () => EditorApplication.ExecuteMenuItem("Window/General/Console"));
			}
		}

		void OptimizeSafeFixes(ScanResult result)
		{
			if (result == null) return;
			var requests = new List<FixRequest>();
			var paths = new HashSet<string>(StringComparer.Ordinal);
			foreach (var entry in result.Entries)
			{
				foreach (var issue in entry.Issues)
				{
					if (!issue.AutoFixable) continue;
					requests.Add(new FixRequest { Entry = entry, Issue = issue });
					paths.Add(entry.Path);
				}
			}
			if (requests.Count == 0)
			{
				Feedback.Show("There are no verified fixes ready to run.");
				return;
			}

			bool proceed = EditorUtility.DisplayDialog("Optimize safe fixes",
				"Apply " + requests.Count + " verified fix(es) across " + paths.Count + " asset(s)?\n\n"
				+ "Each action has a post-fix verifier. Failed verification is rolled back where possible. Review-only findings remain untouched.",
				"Optimize", "Cancel");
			if (!proceed)
			{
				Feedback.Show("Nothing was changed.");
				return;
			}

			var report = FixEngine.Apply(requests);
			CategoryPage.LogFixReport(report);
			CategoryPage.ReportFixOutcome(report);
			var affected = new List<string>(report.TouchedPaths);
			affected.AddRange(report.RolledBackPaths);
			if (affected.Count > 0) RefreshAfterFix(affected);
		}

		void RequestRescan()
		{
			// Accepted straight away rather than when the scan finishes: the banner is asking the
			// user to do exactly this, so leaving it up for the length of the scan reads as if the
			// button did nothing.
			m_StaleAcknowledged = OptimizerSettings.Revision;
			UpdateStaleBanner();
			EditorApplication.delayCall += StartScan;
		}

		/// <summary>
		/// After a fix, re-analyze just the assets that changed and patch them into the existing
		/// result. On a large project this is the difference between an instant update and a
		/// two-minute full rescan.
		/// </summary>
		void RefreshAfterFix(IReadOnlyList<string> paths)
		{
			if (m_Result == null || paths == null || paths.Count == 0)
			{
				RequestRescan();
				return;
			}

			var watch = System.Diagnostics.Stopwatch.StartNew();
			int rebuilt = m_Scanner.RefreshPaths(paths);
			PublishResult(m_Result);
			ScanResultStore.Save(m_Result);
			UpdateStaleBanner();
			bool crossAssetStale = m_Result.CrossAssetPassesStale;
			m_StatusLabel.text = $"Updated {rebuilt} entr{(rebuilt == 1 ? "y" : "ies")} in {watch.ElapsedMilliseconds} ms"
								 + (crossAssetStale ? " - refreshing project-wide evidence…" : string.Empty);
			if (crossAssetStale) EditorApplication.delayCall += RequestRescan;
		}

		void OnScanCompleted(ScanResult result)
		{
			m_Result = result;
			m_ScanButton.text = "Scan Project";
			m_Root?.RemoveFromClassList("po-scanning");
			m_ProgressFill.style.width = Length.Percent(100f);
			UpdateReportButtons();
			m_ProgressFill.RemoveFromClassList("po-progress-pulse");
			m_ProgressFill.RemoveFromClassList("po-progress-pulse-dim");

			var status = "Scanned " + result.ScannedAssets + " assets in "
						 + DisplayUtility.FormatDuration(result.DurationSeconds);
			if (result.Scope != null && result.Scope.Skipped > 0)
				status += " - " + result.Scope.Skipped + " skipped by the scan scope";
			m_StatusLabel.text = status;
			m_StatusLabel.tooltip =
				"Total scan time " + DisplayUtility.FormatDuration(result.DurationSeconds)
				+ ", of which " + DisplayUtility.FormatDuration(result.Timing.TotalWorkMs / 1000.0)
				+ " was work; the rest is time handed back to the editor to stay responsive."
				+ (result.Scope != null ? "\n\n" + result.Scope.Summary() : string.Empty);

			ReportScanNotes(result);

			// Validate the concrete actions produced by this scan. Rule registration alone cannot
			// see Review options because analyzers create those on individual findings. Keep this
			// audit out of the normal rule-audit console spam: only concrete action errors/warnings
			// are reported here.
			var actionAudit = RuleAudit.AuditIssues(result.Entries);
			int actionErrors = 0;
			int actionWarnings = 0;
			for (int i = 0; i < actionAudit.Count; i++)
			{
				if (actionAudit[i].IsError) actionErrors++;
				else actionWarnings++;
			}
			if (actionAudit.Count > 0)
			{
				var auditText = new System.Text.StringBuilder();
				auditText.AppendLine(ProjectOptimizerConstants.LogPrefix + " Concrete fix-action audit for this scan");
				for (int i = 0; i < actionAudit.Count; i++)
					auditText.AppendLine(actionAudit[i].ToString());
				auditText.Append("Summary: ").Append(actionErrors).Append(" error(s), ")
						 .Append(actionWarnings).AppendLine(" warning(s).");
				if (actionErrors > 0) Debug.LogError(auditText.ToString());
				else Debug.LogWarning(auditText.ToString());
			}

			PublishResult(result);
			ScanResultStore.Save(result);
			ScanHistoryStore.Record(result);

			// The toast says what came of it; the console keeps the detail.
			if (result.IssueCount == 0)
			{
				Feedback.Success("Scan finished. Nothing to report in " + result.ScannedAssets + " assets.");
			}
			else
			{
				Feedback.Success(
					result.IssueCount + " issue(s) across " + result.ScannedAssets + " assets in "
					+ result.DurationSeconds.ToString("0.0") + " s",
					"See overview", () => Select(k_OverviewId));
			}
		}

		/// <summary>
		/// Puts the two things a long scan leaves you wondering about into the console: what was left
		/// out, and which files were responsible for the stalls. Both are otherwise invisible - a
		/// scan that skipped half the project looks exactly like one that found nothing.
		/// </summary>
		static void ReportScanNotes(ScanResult result)
		{
			if (result.Scope != null && result.Scope.Skipped > 0)
				Debug.Log(ProjectOptimizerConstants.LogPrefix + " Scan scope: " + result.Scope.Summary());

			if (result.Scope != null && result.Scope.SkippedTooLarge > 0)
			{
				Debug.LogWarning(
					ProjectOptimizerConstants.LogPrefix + " " + result.Scope.SkippedTooLarge + " asset(s) were over the size limit "
					+ "and were not opened. Raise or clear the limit on the Scan Scope page to include them.");
			}

			if (result.SlowAssets.Count == 0) return;
		}

		/// <summary>Pushes a result into every page and refreshes the toolbar and nav counters.</summary>
		void PublishResult(ScanResult result)
		{
			m_Overview.SetData(result, m_Tabs);
			m_RuntimePage?.Refresh();
			foreach (var tab in m_Tabs)
			{
				if (tab.Categories == null) continue;
				if (m_Pages.TryGetValue(tab.Id, out var page) && page is CategoryPage categoryPage)
					categoryPage.SetData(result);
			}

			UpdateStats();
			UpdateNavBadges();
		}

		void UpdateProgress()
		{
			if (m_ProgressFill == null) return;
			m_ProgressFill.style.width = Length.Percent(m_Scanner.Progress * 100f);
			if (m_StatusLabel == null) return;

			// Reads as a sentence rather than a row of figures: what it is doing, how far along, and
			// how much longer. The percentage is redundant with the bar directly above it, so it sits
			// after the countdown rather than leading.
			double remaining = m_Scanner.RemainingSeconds;

			var line = (m_Scanner.Progress * 100f).ToString("0") + "% | " + DisplayUtility.FormatDuration(m_Scanner.ElapsedSeconds);

			if (remaining >= 1.0) line += " | Est:" + DisplayUtility.FormatCountdown(remaining);
			else if (m_Scanner.Progress > 0.02f) line += " | finishing up";

			line += " | " + Shorten(m_Scanner.Status, 250);

			m_StatusLabel.text = line;
		}

		static string Shorten(string value, int max)
		{
			if (string.IsNullOrEmpty(value) || value.Length <= max) return value;

			// Cut at max, not max + 3. The old form threw whenever the string was longer than max
			// but shorter than max + 3 - a three-character window that "Analyzing <path>" lands in
			// regularly, so the status line could take the whole window down mid-scan.
			return value.Substring(0, max) + "...";
		}

		void UpdateStats()
		{
			if (m_StatsContainer == null) return;
			m_StatsContainer.Clear();
			m_RevertButton?.SetEnabled(FixEngine.CanRevert);

			if (m_Result == null)
			{
				m_StatsContainer.Add(UiKit.Text("No scan yet", "po-stat-label"));
				return;
			}

			AddAnimatedStat("critical", m_Result.CriticalCount, "sev-critical", v => v.ToString());
			AddAnimatedStat("warnings", m_Result.WarningCount, "sev-warning", v => v.ToString());
			AddAnimatedStat("info", m_Result.InfoCount, "sev-info", v => v.ToString());
			AddAnimatedStat("memory", m_Result.PotentialMemorySavings, "sev-savings", DisplayUtility.FormatBytes);
			AddAnimatedStat("disk savings", m_Result.PotentialDiskSavings, "sev-savings", DisplayUtility.FormatBytes);
		}

		/// <summary>
		/// Adds one toolbar stat that counts up from zero to its real value instead of just appearing,
		/// so the moment a scan finishes reads as a result landing rather than a label being swapped.
		/// </summary>
		void AddAnimatedStat(string label, long value, string dotClass, Func<long, string> format)
		{
			var stat = UiKit.Stat(label, format(0), dotClass);
			m_StatsContainer.Add(stat);
			UiKit.AnimateCountUp(stat[1] as Label, value, format);
		}

		void UpdateNavBadges()
		{
			foreach (var tab in m_Tabs)
			{
				if (!m_NavItems.TryGetValue(tab.Id, out var item))
					continue;

				var badge = item[1] as Label;
				if (badge == null)
					continue;

				if (m_Result == null || tab.Categories == null)
				{
					badge.style.display = DisplayStyle.None;
					continue;
				}

				int issueCount = 0;
				int affected = 0;
				int total = 0;
				var worst = IssueSeverity.Info;

				foreach (var entry in m_Result.For(tab.Categories))
				{
					if (entry == null)
						continue;

					total++;

					if (entry.Issues.Count > 0)
					{
						affected++;

						foreach (var issue in entry.Issues)
						{
							issueCount++;

							if (issue.Severity > worst)
								worst = issue.Severity;
						}
					}
				}

				badge.RemoveFromClassList("sev-critical");
				badge.RemoveFromClassList("sev-warning");
				badge.RemoveFromClassList("sev-info");
				badge.RemoveFromClassList("sev-clean");

				// -----------------------------------------------------------------
				// Insights tabs represent report rows, so always show the number
				// of rows even when there are zero issues.
				// -----------------------------------------------------------------
				if (string.Equals(tab.Section, "Insights", StringComparison.Ordinal))
				{
					if (total == 0)
					{
						badge.style.display = DisplayStyle.None;
						continue;
					}

					badge.style.display = DisplayStyle.Flex;

					if (issueCount > 0)
					{
						badge.text = total + " | " + issueCount;
						badge.tooltip =
							total + " report entries, "
							+ affected + " affected, "
							+ issueCount + " issue(s)";

						badge.AddToClassList(
							DisplayUtility.SeverityClass(worst));
					}
					else
					{
						badge.text = total.ToString();
						badge.tooltip =
							total + " report entries, 0 issues";

						badge.AddToClassList("sev-clean");
					}

					continue;
				}

				// -----------------------------------------------------------------
				// Existing behaviour for normal asset/project tabs.
				// -----------------------------------------------------------------
				if (issueCount == 0)
				{
					badge.style.display = DisplayStyle.None;
					continue;
				}

				badge.style.display = DisplayStyle.Flex;
				badge.text = affected + " | " + issueCount;
				badge.tooltip =
					affected + " of " + total + " assets affected, "
					+ issueCount + " issues in total";

				badge.AddToClassList(
					DisplayUtility.SeverityClass(worst));
			}
		}

		// --- toolbar actions ------------------------------------------------------------

		void ShowExportMenu(VisualElement anchor)
		{
			if (m_Result == null)
			{
				Feedback.Warn("There is nothing to export yet.", "Scan now", StartScan);
				return;
			}

			var menu = new GenericMenu();
			menu.AddItem(new GUIContent("Export CSV"), false, () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Csv));
			menu.AddItem(new GUIContent("Export JSON"), false, () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Json));
			menu.AddItem(new GUIContent("Export Markdown"), false, () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Markdown));
			menu.AddItem(new GUIContent("Export SARIF"), false, () => ReportExporter.ExportWithDialog(m_Result, ReportExporter.Format.Sarif));
			menu.AddSeparator("");
			if (m_Result != null && m_Result.IsComplete) menu.AddItem(new GUIContent("Save optimization baseline"), false, SaveBaseline);
			else menu.AddDisabledItem(new GUIContent("Save optimization baseline (complete scan required)"));
			if (OptimizationBaselineStore.Exists) menu.AddItem(new GUIContent("Clear optimization baseline"), false, ClearBaseline);
			else menu.AddDisabledItem(new GUIContent("Clear optimization baseline"));

			// ShowAsContext needs a live IMGUI event, which a UI Toolkit click callback does not have,
			// so the menu is dropped under the button instead.
			menu.DropDown(anchor.worldBound);
		}

		void SaveBaseline()
		{
			if (m_Result == null || !m_Result.IsComplete)
			{
				Feedback.Warn("Only a complete scan can become an optimization baseline.");
				return;
			}
			if (!OptimizationBaselineStore.Save(m_Result))
			{
				Feedback.Error("The optimization baseline could not be saved.");
				return;
			}
			m_Overview?.SetData(m_Result, m_Tabs);
			Feedback.Success("Optimization baseline saved.");
		}

		void ClearBaseline()
		{
			if (!OptimizationBaselineStore.Exists) return;
			if (!EditorUtility.DisplayDialog("Clear optimization baseline",
				"Remove the stored before/after baseline? Scan history and findings will remain untouched.", "Clear", "Cancel")) return;
			OptimizationBaselineStore.Clear();
			m_Overview?.SetData(m_Result, m_Tabs);
			Feedback.Success("Optimization baseline cleared.");
		}

		void RevertLastFixes()
		{
			if (!FixEngine.CanRevert)
			{
				Feedback.Warn("There is no fix batch left to revert.");
				return;
			}

			if (!EditorUtility.DisplayDialog("Revert last fixes",
				"Restore every file changed by the most recent fix batch?\n\n" +
				FixEngine.PendingBackupCount + " batch(es) can still be rolled back; this undoes the newest.\n" +
				"Changes made since then to those files will be lost.", "Revert", "Cancel"))
				return;

			var result = FixEngine.RevertLastBatch();

			if (!result.Complete)
			{
				// The batch deliberately stays pending. Reporting success on a half-restored project
				// would remove the only route back for the files that did not make it.
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Revert incomplete: restored " + result.Restored + " of "
							   + result.Expected + " file(s).\n" + string.Join("\n", result.Errors.ToArray()));
				Feedback.Error("Only " + result.Restored + " of " + result.Expected + " file(s) could be restored. "
							   + "The batch is still available, so you can retry once the cause is dealt with.",
							   "Open console", () => EditorApplication.ExecuteMenuItem("Window/General/Console"));
				UpdateStats();
				RequestRescan();
				return;
			}

			Debug.Log(ProjectOptimizerConstants.LogPrefix + $" Restored {result.Restored} file(s).");
			Feedback.Success(result.Restored == 1
				? "Reverted 1 file. Rescanning..."
				: "Reverted " + result.Restored + " files. Rescanning...");
			UpdateStats();
			RequestRescan();
		}
	}
}

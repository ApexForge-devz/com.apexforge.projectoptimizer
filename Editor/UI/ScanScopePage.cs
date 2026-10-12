using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Decides what a scan is pointed at, before it runs.
	/// <para>
	/// On a small project scanning everything is free, so the scope never mattered. On a project with
	/// hundreds of thousands of assets it is the single thing that decides whether a scan takes half
	/// a minute or half an hour, so it gets its own page rather than living as a few checkboxes among
	/// the thresholds. Every option says what it costs and what you lose by turning it off - an
	/// option nobody understands is one nobody touches.
	/// </para>
	/// </summary>
	public sealed class ScanScopePage : VisualElement
	{
		readonly Action m_RequestScan;
		readonly ScrollView m_Scroll;

		/// <summary>Last preview, kept so the numbers survive a rebuild of the page.</summary>
		ScanScope m_Preview;
		Dictionary<AssetKind, int> m_PreviewByKind;
		Label m_PreviewLabel;

		/// <summary>
		/// Set when settings changed after the preview was taken. The numbers stay on screen and are
		/// labelled as out of date, rather than disappearing: a panel that empties itself when you
		/// touch a checkbox reads as a glitch, not as an answer.
		/// </summary>
		bool m_PreviewStale;

		public ScanScopePage(Action requestScan)
		{
			m_RequestScan = requestScan;
			AddToClassList("po-page");
			style.flexDirection = FlexDirection.Column;

			var bar = UiKit.Row("po-filters");
			bar.Add(UiKit.Button("Preview scope", RunPreview, "po-button-small"));
			m_PreviewLabel = UiKit.Text("Press Preview to count what the current settings would scan.", "po-status");
			m_PreviewLabel.style.marginLeft = 8f;
			bar.Add(m_PreviewLabel);
			bar.Add(UiKit.Spacer());
			bar.Add(UiKit.Button("Scan with these settings", () => m_RequestScan?.Invoke(),
				"po-button-primary", "po-button-small"));
			Add(bar);

			m_Scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			m_Scroll.AddToClassList("po-scroll");
			Add(m_Scroll);

			Rebuild();
		}

		public void Rebuild()
		{
			m_Scroll.Clear();
			var settings = OptimizerSettings.Instance;

			BuildPreviewSummary();
			BuildPresets(settings);
			BuildModeSection(settings);
			BuildFolderSection(settings);
			BuildTypeSection(settings);
			BuildPassSection(settings);
			BuildPerformanceSection(settings);
			BuildLimitSection(settings);
		}

		void Save()
		{
			OptimizerSettings.Instance.Save();
			// The preview describes the settings it was taken with, so it stops being true here.
			if (m_Preview != null) m_PreviewStale = true;
			UpdatePreviewLabel();
		}

		void UpdatePreviewLabel()
		{
			if (m_PreviewLabel == null) return;
			if (m_Preview == null)
			{
				m_PreviewLabel.text = "Press Preview scope to count what these settings would scan.";
				m_PreviewLabel.RemoveFromClassList("sev-warning");
				return;
			}

			m_PreviewLabel.text = m_PreviewStale
				? "Settings changed - preview is out of date"
				: m_Preview.Summary();
			m_PreviewLabel.EnableInClassList("sev-warning", m_PreviewStale);
		}

		/// <summary>Spells out what the last preview counted, including what it decided to leave out.</summary>
		void BuildPreviewSummary()
		{
			if (m_Preview == null) return;

			m_Scroll.Add(UiKit.Text(m_PreviewStale ? "This scope covered (out of date)" : "This scope covers",
				"po-section-title"));
			var group = UiKit.Column("po-settings-group");

			var headline = UiKit.Text(
				DisplayUtility.FormatCount(m_Preview.Paths.Count) + " assets will be scanned, out of "
				+ DisplayUtility.FormatCount(m_Preview.Candidates) + " under Assets.", "po-rule-title");
			group.Add(headline);

			if (m_PreviewStale)
			{
				var note = UiKit.Text(
					"These numbers were counted before your last change, so they no longer describe what "
					+ "a scan would do. Press Preview scope again to refresh them.", "po-rule-why");
				note.AddToClassList("sev-warning");
				group.Add(note);
			}

			void Reason(int count, string text)
			{
				if (count <= 0) return;
				group.Add(UiKit.Text("\u2022  " + DisplayUtility.FormatCount(count) + " " + text, "po-rule-why"));
			}

			Reason(m_Preview.SkippedByFolder, "skipped: outside the folders chosen above");
			Reason(m_Preview.SkippedOutsideBuild, "skipped: not reachable from a player build");
			Reason(m_Preview.SkippedByKind, "skipped: of a type that is switched off");
			Reason(m_Preview.SkippedTooLarge, "skipped: over the size limit ("
											  + DisplayUtility.FormatBytes(m_Preview.SkippedTooLargeBytes) + " in total)");

			if (m_Preview.Paths.Count == 0)
			{
				var warning = UiKit.Text(
					"Nothing is in scope, so a scan would find nothing. Widen the folders or switch a type back on.",
					"po-rule-why");
				warning.AddToClassList("sev-warning");
				group.Add(warning);
			}

			m_Scroll.Add(group);
		}

		// --- presets ------------------------------------------------------------------

		/// <summary>
		/// A named bundle of scope settings. It knows how to recognise itself as well as how to apply
		/// itself, so the page can show which preset you are currently on - that standing answer is
		/// worth more than a message confirming the click, because it is still there tomorrow.
		/// </summary>
		sealed class Preset
		{
			public string Name;
			public string Description;
			public Action<OptimizerSettings> Apply;
			public Func<OptimizerSettings, bool> Matches;
		}

		static readonly Preset[] k_Presets =
		{
			new Preset
			{
				Name = "Everything",
				Description = "Every asset under Assets, every check switched on. The most thorough and the slowest.",
				Apply = s =>
				{
					s.scopeMode = ScanScopeMode.EntireProject;
					foreach (var kind in AssetKinds.All) s.SetKindEnabled(kind, true);
					s.scanPrefabs = s.scanScenes = true;
					s.scanDuplicates = s.scanUnused = true;
					s.projectProofPass = true;
					s.maxAssetSizeMb = 0;
				},
				Matches = s => s.scopeMode == ScanScopeMode.EntireProject
							   && s.disabledAssetKinds.Count == 0
							   && s.scanPrefabs && s.scanScenes && s.scanDuplicates && s.scanUnused
							   && s.projectProofPass
							   && s.maxAssetSizeMb == 0
			},
			new Preset
			{
				Name = "Fast pass",
				Description = "Importer settings only: textures, audio, video, models, materials and animation. "
							  + "Skips scenes, prefabs and the project-wide passes, which is where nearly all of "
							  + "the time goes. The right starting point on a very large project.",
				Apply = s =>
				{
					s.scopeMode = ScanScopeMode.EntireProject;
					foreach (var kind in AssetKinds.All) s.SetKindEnabled(kind, false);
					foreach (var kind in k_FastPassKinds) s.SetKindEnabled(kind, true);
					s.scanPrefabs = s.scanScenes = false;
					s.scanDuplicates = s.scanUnused = false;
					s.projectProofPass = false;
					s.deepTextureScan = false;
					s.maxAssetSizeMb = 256;
				},
				Matches = s =>
				{
					if (s.scopeMode != ScanScopeMode.EntireProject) return false;
					if (s.scanPrefabs || s.scanScenes || s.scanDuplicates || s.scanUnused || s.deepTextureScan || s.projectProofPass) return false;
					if (s.maxAssetSizeMb != 256) return false;
					foreach (var kind in AssetKinds.All)
						if (s.IsKindEnabled(kind) != (Array.IndexOf(k_FastPassKinds, kind) >= 0)) return false;
					return true;
				}
			},
			new Preset
			{
				Name = "Only what static build analysis can reach",
				Description = "Restricts the scan to assets reachable from the build scenes, Resources, "
							  + "StreamingAssets and preloaded assets. Skips everything an imported package brought "
							  + "along that nothing leads to - usually the bulk of a large project. Assets loaded "
							  + "through runtime-generated Addressables keys, remote catalogs, externally built AssetBundles or computed resource paths "
							  + "cannot be proven here, so this is a static reachability view rather than a complete runtime-load model. The independent project proof pass is not affected by this scope.",
				Apply = s =>
				{
					s.scopeMode = ScanScopeMode.ShippedInBuild;
					foreach (var kind in AssetKinds.All) s.SetKindEnabled(kind, true);
					s.scanPrefabs = s.scanScenes = true;
					s.scanDuplicates = true;
                    // Nothing outside the build is in scope, so "unreferenced" has nothing to find.
                    s.scanUnused = false;
					s.maxAssetSizeMb = 512;
				},
				Matches = s => s.scopeMode == ScanScopeMode.ShippedInBuild
							   && s.disabledAssetKinds.Count == 0
							   && s.scanPrefabs && s.scanScenes && s.scanDuplicates && !s.scanUnused
							   && s.maxAssetSizeMb == 512
			}
		};

		static readonly AssetKind[] k_FastPassKinds =
		{
			AssetKind.Texture, AssetKind.Audio, AssetKind.Video,
			AssetKind.Model, AssetKind.MaterialShader, AssetKind.Animation
		};

		void BuildPresets(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("Start from a preset", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			Preset active = null;
			foreach (var preset in k_Presets)
				if (preset.Matches(settings)) { active = preset; break; }

			group.Add(UiKit.Text(
				active != null
					? "Current settings match the \"" + active.Name + "\" preset. Changing anything below moves you off it."
					: "Current settings are custom - they do not match any preset. Applying one replaces them.",
				"po-rule-why"));

			var row = UiKit.Row();
			row.style.marginTop = 8f;
			row.style.flexWrap = Wrap.Wrap;
			foreach (var preset in k_Presets) row.Add(BuildPresetCard(settings, preset, preset == active));

			group.Add(row);
			m_Scroll.Add(group);
		}

		VisualElement BuildPresetCard(OptimizerSettings settings, Preset preset, bool active)
		{
			var card = UiKit.Column("po-preset");
			if (active) card.AddToClassList("po-preset-active");

			var header = UiKit.Row();
			header.style.alignItems = Align.Center;
			header.Add(UiKit.Text(preset.Name, "po-rule-title"));
			header.Add(UiKit.Spacer());
			if (active) header.Add(UiKit.Pill("ACTIVE", "sev-clean"));
			card.Add(header);

			var body = UiKit.Text(preset.Description, "po-rule-why");
			body.style.flexGrow = 1;
			card.Add(body);

			// An applied preset leaves nothing to press, which is itself the clearest possible reply
			// to "did that work?".
			var button = UiKit.Button(active ? "Applied" : "Apply", () =>
			{
				preset.Apply(settings);
				Save();
				Rebuild();
				Feedback.Success("Applied the \"" + preset.Name + "\" preset.",
					"Scan now", () => m_RequestScan?.Invoke());
			}, "po-button-small");
			button.SetEnabled(!active);
			card.Add(button);
			return card;
		}

		// --- what to scan --------------------------------------------------------------

		void BuildModeSection(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("1. Which assets", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			group.Add(ModeRow(settings, ScanScopeMode.EntireProject, "The whole project",
				"Every asset under Assets, minus the excluded folders below. The scan does not analyze Package Manager content, "
				+ "because package files live outside the normal project-asset editing surface; package-managed dependencies can still appear as references."));

			group.Add(ModeRow(settings, ScanScopeMode.SelectedFolders, "Only certain folders",
				"Scans just the folders you list below. Use this to work through a large project one area "
				+ "at a time, or to check a package you have just imported without touching the rest."));

			// Worded as what the analysis can actually establish. "Only what a build ships" claimed
			// knowledge the AssetDatabase does not have: it can follow static references and nothing
			// else, so an asset outside this set has not been shown to be absent from the build.
			group.Add(ModeRow(settings, ScanScopeMode.ShippedInBuild, "Only what the build can be shown to reach",
				"Follows dependencies out from the scenes enabled in Build Settings, every Resources folder, "
				+ "StreamingAssets and the preloaded asset list, and scans only what it can reach.\n\n"
				+ ScanScope.ReachabilityCaveat));

			if (settings.scopeMode == ScanScopeMode.ShippedInBuild)
			{
				int enabledScenes = 0;
				foreach (var scene in EditorBuildSettings.scenes)
					if (scene.enabled) enabledScenes++;

				var note = UiKit.Text(
					enabledScenes == 0
						? "Build Settings has no enabled scenes, so nothing is reachable and this scope will "
						  + "match almost nothing. Add your scenes, or pick another mode."
						: "Following " + enabledScenes + " enabled build scene(s), plus Resources and preloaded assets.",
					"po-rule-why");
				note.style.marginTop = 6f;
				if (enabledScenes == 0) note.AddToClassList("sev-warning");
				group.Add(note);
			}

			m_Scroll.Add(group);
		}

		VisualElement ModeRow(OptimizerSettings settings, ScanScopeMode mode, string title, string description)
		{
			var row = UiKit.Row("po-option-row");

			var radio = new RadioButton { value = settings.scopeMode == mode };
			radio.style.marginRight = 6f;
			radio.style.marginTop = 1f;
			radio.RegisterValueChangedCallback(evt =>
			{
				if (!evt.newValue) return;
				settings.scopeMode = mode;
				Save();
				Rebuild();
			});
			row.Add(radio);

			var body = UiKit.Column("po-rule-body");
			body.Add(UiKit.Text(title, "po-rule-title"));
			body.Add(UiKit.Text(description, "po-rule-why"));
			row.Add(body);
			return row;
		}

		// --- folders ---------------------------------------------------------------------

		void BuildFolderSection(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("2. Folders", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			if (settings.scopeMode == ScanScopeMode.SelectedFolders)
			{
				group.Add(UiKit.Text("SCAN ONLY THESE FOLDERS", "po-meta-label"));
				group.Add(UiKit.Text(
					"Subfolders are included. Leave the list empty and this behaves like scanning the whole project.",
					"po-rule-why"));
				group.Add(FolderList(settings.includedFolders, "Choose a folder to scan"));
			}

			var excludeLabel = UiKit.Text("NEVER SCAN THESE FOLDERS", "po-meta-label");
			excludeLabel.style.marginTop = 10f;
			group.Add(excludeLabel);
			group.Add(UiKit.Text(
				"Applied in every mode. Third-party plugins, editor-only tooling and imported samples belong here: "
				+ "they are usually not yours to change, and they are a large share of the assets in a big project.",
				"po-rule-why"));
			group.Add(FolderList(settings.excludedFolders, "Choose a folder to exclude"));

			m_Scroll.Add(group);
		}

		VisualElement FolderList(List<string> folders, string dialogTitle)
		{
			var column = UiKit.Column();
			column.style.marginTop = 4f;

			for (int i = 0; i < folders.Count; i++)
			{
				int index = i;
				var row = UiKit.Row();
				row.style.alignItems = Align.Center;
				row.style.marginTop = 2f;

				var field = new TextField { value = folders[index] };
				field.style.flexGrow = 1;
				field.RegisterValueChangedCallback(evt =>
				{
					folders[index] = evt.newValue;
					Save();
				});
				row.Add(field);
				row.Add(UiKit.Button("Remove", () =>
				{
					var removed = folders[index];
					folders.RemoveAt(index);
					Save();
					Rebuild();
					Feedback.Show("Removed " + removed + " from the list.");
				}, "po-button-small"));
				column.Add(row);
			}

			if (folders.Count == 0)
				column.Add(UiKit.Text("(none)", "po-rule-why"));

			column.Add(UiKit.Button("Add folder", () =>
			{
				var picked = EditorUtility.OpenFolderPanel(dialogTitle, Application.dataPath, string.Empty);
				if (string.IsNullOrEmpty(picked)) return;

				var root = Application.dataPath;
				var assetPath = picked.Replace('\\', '/');
				if (assetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					assetPath = "Assets" + assetPath.Substring(root.Length);

				if (!assetPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
				{
					EditorUtility.DisplayDialog(ProjectOptimizerConstants.ProductName,
						"Pick a folder inside this project's Assets folder.", "OK");
					return;
				}

				if (folders.Contains(assetPath))
				{
					Feedback.Warn(assetPath + " is already on this list.");
					return;
				}

				folders.Add(assetPath);
				Save();
				Rebuild();
				Feedback.Success("Added " + assetPath + ".");
			}, "po-button-small"));

			return column;
		}

		// --- asset types -------------------------------------------------------------------

		void BuildTypeSection(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("3. Which types of asset", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			group.Add(UiKit.Text(
				"Switching a type off removes it from the scan entirely: no rows, no issues, no time spent. "
				+ "The counts appear once you press Preview scope.",
				"po-rule-why"));

			var card = UiKit.Column("po-card");
			card.style.marginLeft = 0f;
			card.style.marginRight = 0f;
			card.style.marginTop = 8f;

			foreach (var kind in AssetKinds.All)
			{
				var row = UiKit.Row("po-option-row");

				var captured = kind;
				var toggle = new Toggle { value = settings.IsKindEnabled(kind) };
				toggle.style.marginRight = 6f;
				toggle.style.marginTop = 1f;
				toggle.RegisterValueChangedCallback(evt =>
				{
					settings.SetKindEnabled(captured, evt.newValue);
					Save();
				});
				row.Add(toggle);

				var body = UiKit.Column("po-rule-body");
				body.Add(UiKit.Text(AssetKinds.Label(kind), "po-rule-title"));
				body.Add(UiKit.Text(AssetKinds.Description(kind), "po-rule-why"));
				row.Add(body);

				if (m_PreviewByKind != null && m_PreviewByKind.TryGetValue(kind, out int count))
					row.Add(UiKit.Pill(DisplayUtility.FormatCount(count), "sev-clean"));

				card.Add(row);
			}

			group.Add(card);

			var buttons = UiKit.Row();
			int enabledKinds = 0;
			foreach (var kind in AssetKinds.All)
				if (settings.IsKindEnabled(kind)) enabledKinds++;

			buttons.Add(UiKit.Button("All types", () =>
			{
				foreach (var kind in AssetKinds.All) settings.SetKindEnabled(kind, true);
				Save();
				Rebuild();
				Feedback.Success("All " + AssetKinds.All.Length + " asset types switched on.");
			}, "po-button-small"));
			buttons.Add(UiKit.Button("No types", () =>
			{
				foreach (var kind in AssetKinds.All) settings.SetKindEnabled(kind, false);
				Save();
				Rebuild();
				Feedback.Warn("Every asset type is off, so a scan would find nothing.");
			}, "po-button-small"));

			// A standing count, so the state is readable without adding up twelve checkboxes.
			var summary = UiKit.Text(
				enabledKinds + " of " + AssetKinds.All.Length + " types will be scanned.",
				enabledKinds == 0 ? "po-rule-title" : "po-rule-why");
			summary.style.marginLeft = 8f;
			summary.style.alignSelf = Align.Center;
			if (enabledKinds == 0) summary.AddToClassList("sev-warning");
			buttons.Add(summary);
			group.Add(buttons);

			m_Scroll.Add(group);
		}

		// --- expensive passes ---------------------------------------------------------------

		void BuildPassSection(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("4. Extra passes", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			group.Add(UiKit.Text(
				"These run in addition to the per-asset checks. They are the expensive part of a scan, and "
				+ "the part most likely to make a large project feel like it has hung.",
				"po-rule-why"));

			var card = UiKit.Column("po-card");
			card.style.marginLeft = 0f;
			card.style.marginRight = 0f;
			card.style.marginTop = 8f;

			card.Add(PassRow("Walk prefab hierarchies", settings.scanPrefabs, v => settings.scanPrefabs = v,
				"Loads each prefab and walks every component: missing scripts, broken references, colliders, "
				+ "renderers and raycastable UI.\n\n"
				+ "Loading a prefab makes Unity resolve every reference in it, so a prefab that already had a "
				+ "missing script or a broken link logs an error as it loads. Those errors describe damage that "
				+ "was already there - the scan reports the same thing as PRE002 and PRE013."));

			card.Add(PassRow("Open and measure scenes", settings.scanScenes, v => settings.scanScenes = v,
				"Opens each scene in an isolated preview stage, measures it and closes it. Your open scenes are "
				+ "never touched or marked dirty.\n\n"
				+ "Normally the slowest single part of a scan and the loudest in the console, for the same reason "
				+ "as prefabs. Turn this off first if a scan is taking too long."));

			card.Add(PassRow("Find duplicate files", settings.scanDuplicates, v => settings.scanDuplicates = v,
				"Groups assets by exact file size, then hashes only the groups that collide, so identical copies "
				+ "are found without reading the whole project. Needs the reference index (see below) so that a "
				+ "duplicate can be collapsed onto a keeper rather than only reported."));

			card.Add(PassRow("Find unreferenced assets", settings.scanUnused, v => settings.scanUnused = v,
				"Builds a reference index by asking Unity for the dependencies of every asset in scope - one "
				+ "query per asset, which is minutes of work on a very large project. The result is cached in "
				+ "Library and reused until something in the project changes.\n\n"
				+ "It can account for explicit Addressables entries when the optional package is available, but it cannot prove runtime-generated Addressables keys, remote catalogs, externally built AssetBundles or computed Resources.Load paths. Confirm before deleting anything it reports."));

			card.Add(PassRow("Deep texture scan (reads pixels)", settings.deepTextureScan, v => settings.deepTextureScan = v,
				"Blits a small copy of every colour texture and compares its channels, to find images stored as "
				+ "RGB/RGBA textures that only ever hold greyscale (TEX016). Those can sometimes drop to a single-channel format, "
				+ "reducing the texture memory pool substantially; the exact ratio depends on the source format. Adds noticeable time, "
				+ "after switching this runs cold."));

			card.Add(PassRow("Project dependency / source proof", settings.projectProofPass, v => settings.projectProofPass = v,
				"Builds an explicit project-wide source/dependency evidence index for rules that need source or serialized-reference proof. "
				+ "It is independent of the selected asset scope, including build-reachability scope, and is opt-in because it reads project/package source files. "
				+ "It cannot prove arbitrary runtime behavior such as reflection, generated resource names or dynamically constructed Addressables keys; when evidence is incomplete, proof-dependent rules fail closed rather than silently broadening the scan."));

			group.Add(card);
			m_Scroll.Add(group);
		}

		VisualElement PassRow(string title, bool value, Action<bool> setter, string description)
		{
			var row = UiKit.Row("po-option-row");

			var toggle = new Toggle { value = value };
			toggle.style.marginRight = 6f;
			toggle.style.marginTop = 1f;
			toggle.RegisterValueChangedCallback(evt =>
			{
				setter(evt.newValue);
				Save();
			});
			row.Add(toggle);

			var body = UiKit.Column("po-rule-body");
			body.Add(UiKit.Text(title, "po-rule-title"));
			body.Add(UiKit.Text(description, "po-rule-why"));
			row.Add(body);
			return row;
		}

		// --- performance / diagnostics --------------------------------------------------------

		void BuildPerformanceSection(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("5. Performance and diagnostics", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			var logging = new Toggle("Detailed scan timing logs") { value = settings.enableScanPerformanceLogging };
			logging.AddToClassList("po-field");
			logging.RegisterValueChangedCallback(evt => { settings.enableScanPerformanceLogging = evt.newValue; Save(); });
			group.Add(logging);
			group.Add(UiKit.Text(
				"Writes stage, analyzer and slow-asset timings to the Console and " + ProjectOptimizerConstants.ScanTimingsFolder + ". Disable when diagnostics are not needed.", "po-rule-why"));

			var profiling = new EnumField("Profiling mode", settings.scanProfilingMode);
			profiling.AddToClassList("po-field");
			profiling.RegisterValueChangedCallback(evt => { settings.scanProfilingMode = (ScanProfilingMode)evt.newValue; Save(); });
			group.Add(profiling);
			group.Add(UiKit.Text(
				"Production keeps only low-overhead scan accounting; Aggregate adds per-rule counters; Deep retains detailed rule samples; Validation reserves the profiling shape for correctness comparisons.", "po-rule-why"));

			var projectState = UiKit.Row("po-option-row");
			var rebuildState = new Button(() =>
			{
				ProjectOptimizerWindow.RebuildProjectState();
			}) { text = "Rebuild Project State" };
			rebuildState.AddToClassList("po-button");
			projectState.Add(rebuildState);
			var reconcileState = new Button(() =>
			{
				ProjectOptimizerWindow.RequestProjectStateReconcile();
			}) { text = "Reconcile" };
			reconcileState.AddToClassList("po-button");
			projectState.Add(reconcileState);
			var flushState = new Button(ProjectOptimizerWindow.FlushProjectStateSnapshot) { text = "Flush Snapshot" };
			flushState.AddToClassList("po-button");
			projectState.Add(flushState);
			group.Add(projectState);
			group.Add(UiKit.Text("Project state is incrementally journaled between scans. Rebuild is a full filesystem index; Reconcile forces authoritative state recovery without using Unity path enumeration on the normal warm path.", "po-rule-why"));

			var workers = new IntegerField("Background worker count (0 = Auto)") { value = settings.scanWorkerCount };
			workers.AddToClassList("po-field");
			workers.RegisterValueChangedCallback(evt => { settings.scanWorkerCount = Mathf.Clamp(evt.newValue, 0, 32); Save(); });
			group.Add(workers);
			group.Add(UiKit.Text(
				"Controls filesystem/CPU-only work such as duplicate hashing. Auto keeps bounded concurrency; Unity AssetDatabase work remains on the editor thread.", "po-rule-why"));

			var pacing = new EnumField("Scan pacing mode", settings.scanPacingMode);
			pacing.AddToClassList("po-field");
			pacing.RegisterValueChangedCallback(evt => { settings.scanPacingMode = (ScanPacingMode)evt.newValue; Save(); });
			group.Add(pacing);
			group.Add(UiKit.Text("Interactive favors editor responsiveness; Balanced is the normal mode; Throughput favors scan completion; Background yields more aggressively when the editor is active.", "po-rule-why"));

			var queue = new IntegerField("Background queue capacity (0 = Auto)") { value = settings.scanQueueCapacity };
			queue.AddToClassList("po-field");
			queue.RegisterValueChangedCallback(evt => { settings.scanQueueCapacity = Mathf.Clamp(evt.newValue, 0, 8192); Save(); });
			group.Add(queue);
			group.Add(UiKit.Text("Bounds producer/consumer backlog. Smaller values reduce retained work; larger values can improve throughput when preparation is faster than workers.", "po-rule-why"));

			var budget = new IntegerField("Scan frame budget (ms)") { value = settings.scanFrameBudgetMs };
			budget.AddToClassList("po-field");
			budget.RegisterValueChangedCallback(evt => { settings.scanFrameBudgetMs = Mathf.Clamp(evt.newValue, 1, 100); Save(); });
			group.Add(budget);
			group.Add(UiKit.Text("Higher values maximize throughput; lower values yield to the editor more often.", "po-rule-why"));

			var batch = new IntegerField("Assets per scan batch") { value = settings.scanBatchSize };
			batch.AddToClassList("po-field");
			batch.RegisterValueChangedCallback(evt => { settings.scanBatchSize = Mathf.Clamp(evt.newValue, 1, 4096); Save(); });
			group.Add(batch);

			m_Scroll.Add(group);
		}

		// --- limits ---------------------------------------------------------------------------

		void BuildLimitSection(OptimizerSettings settings)
		{
			m_Scroll.Add(UiKit.Text("6. Safety limit", "po-section-title"));
			var group = UiKit.Column("po-settings-group");

			var field = new IntegerField("Skip assets larger than (MB)") { value = settings.maxAssetSizeMb };
			field.AddToClassList("po-field");
			field.RegisterValueChangedCallback(evt =>
			{
				settings.maxAssetSizeMb = Mathf.Max(0, evt.newValue);
				Save();
			});
			group.Add(field);

			group.Add(UiKit.Text(
				"Analyzing one asset is all-or-nothing: Unity has to finish opening a file before the scan can "
				+ "get a frame back. A single very large model or scene therefore freezes the editor for as long "
				+ "as it takes to load, no matter how the scan is paced. Assets over this size are counted and "
				+ "named in the scan summary instead of being opened.\n\n"
				+ "Set it to 0 for no limit.",
				"po-rule-why"));

			m_Scroll.Add(group);
		}

		// --- preview -----------------------------------------------------------------------------

		/// <summary>
		/// Counts what the current settings would scan. Run on demand rather than live: on a large
		/// project this walks every asset path and stats every file, which is not something to do on
		/// every keystroke.
		/// </summary>
		void RunPreview()
		{
			var settings = OptimizerSettings.Instance;
			var byKind = new Dictionary<AssetKind, int>();
			ScanScope scope;

			try
			{
				// Constructing the scope walks the build's dependencies when the mode calls for it,
				// which is itself long enough to look like a hang. So the bar goes up first.
				EditorUtility.DisplayProgressBar(ProjectOptimizerConstants.ProductName, "Working out what is in scope...", 0f);
				scope = new ScanScope(settings);

				IEnumerable<string> indexedPaths = AssetDatabaseThreadSafeUtility.IsComplete
					? AssetDatabaseThreadSafeUtility.EnumerateIndexedAssetPaths()
					: AssetDatabase.GetAllAssetPaths();
				var paths = indexedPaths is string[] exactPaths ? exactPaths : indexedPaths.ToArray();
				for (int i = 0; i < paths.Length; i++)
				{
					if ((i & 2047) == 0 && EditorUtility.DisplayCancelableProgressBar(
							ProjectOptimizerConstants.ProductName, "Counting assets in scope...", i / (float)paths.Length))
					{
						Feedback.Warn("Preview cancelled.");
						return;
					}

					if (!scope.Consider(paths[i])) continue;
					var kind = AssetKinds.Of(paths[i]);
					byKind.TryGetValue(kind, out int count);
					byKind[kind] = count + 1;
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}

			m_Preview = scope;
			m_PreviewByKind = byKind;
			m_PreviewStale = false;
			Rebuild();
			UpdatePreviewLabel();

			if (scope.Paths.Count == 0)
			{
				Feedback.Warn("Nothing is in scope - a scan would find nothing.");
			}
			else
			{
				Feedback.Success(DisplayUtility.FormatCount(scope.Paths.Count) + " assets in scope"
								 + (scope.Skipped > 0 ? ", " + DisplayUtility.FormatCount(scope.Skipped) + " skipped" : string.Empty),
					"Scan now", () => m_RequestScan?.Invoke());
			}
		}
	}
}

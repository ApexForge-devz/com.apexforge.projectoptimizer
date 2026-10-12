using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Shows the latest compact runtime evidence captured by Project Optimizer without duplicating Unity Profiler's frame UI.</summary>
	public sealed class RuntimeEvidencePage : VisualElement
	{
		readonly ScrollView m_Scroll;

		public RuntimeEvidencePage()
		{
			AddToClassList("po-page");
			m_Scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			m_Scroll.AddToClassList("po-scroll");
			Add(m_Scroll);
			Refresh();
		}

		public void Refresh()
		{
			m_Scroll.Clear();
			var settings = OptimizerSettings.Instance;
			var snapshot = RuntimeEvidenceStore.LoadLatest();

			if (snapshot == null)
			{
				m_Scroll.Add(BuildHero(null, settings));
				m_Scroll.Add(UiKit.Text("What gets captured automatically", "po-section-title"));
				m_Scroll.Add(BuildAutomaticCaptureInfo());
				return;
			}

			m_Scroll.Add(BuildHero(snapshot, settings));
			m_Scroll.Add(UiKit.Text("Captured runtime evidence", "po-section-title"));
			m_Scroll.Add(BuildMetrics(snapshot.Metrics));
			m_Scroll.Add(BuildEvidenceNote(snapshot.Note));
			m_Scroll.Add(BuildMemorySnapshotList());
		}

		VisualElement BuildHero(RuntimeEvidenceSnapshot snapshot, OptimizerSettings settings)
		{
			var hero = UiKit.Column("po-runtime-hero");
			var header = UiKit.Row("po-runtime-hero-header");
			var heading = UiKit.Column("po-runtime-hero-heading");
			heading.Add(UiKit.Text("RUNTIME EVIDENCE", "po-hero-kicker"));
			heading.Add(UiKit.Text(snapshot == null ? "No runtime sample yet" : "Latest captured runtime sample", "po-hero-title"));
			heading.Add(UiKit.Text(
				snapshot == null
					? "Lightweight profiler counters are captured automatically during Editor Play Mode. Memory Profiler snapshots are separate, heavier, and opt-in."
					: snapshot.FrameSamples.ToString("N0") + " frame samples over " + DisplayUtility.FormatDuration(snapshot.DurationSeconds),
				"po-hero-subtitle"));
			heading.Add(UiKit.Text("Editor Play Mode is useful for trend/baseline evidence, but Editor overhead can affect attribution. Use a Player build with the Unity Profiler for authoritative runtime diagnosis.", "po-runtime-hero-note"));
			header.Add(heading);

			var actions = UiKit.Column("po-runtime-hero-actions");
			actions.Add(UiKit.Button("Refresh evidence", Refresh, "po-button-small"));
			actions.Add(UiKit.Button("Import Player report", ImportPlayerReport, "po-button-small"));
			var capture = UiKit.Button("Capture memory snapshot", RuntimeMemorySnapshotUtility.CaptureMemorySnapshot, "po-button-primary", "po-button-small");
			capture.tooltip = "Capture an explicit Memory Profiler snapshot. This is intentionally separate from lightweight runtime counters and can be expensive.";
			actions.Add(capture);
			header.Add(actions);
			hero.Add(header);

			var meta = UiKit.Row("po-runtime-meta-grid");
			meta.Add(UiKit.Meta("Unity", snapshot?.UnityVersion ?? "Not captured"));
			meta.Add(UiKit.Meta("Platform", snapshot?.Platform ?? "Not captured"));
			meta.Add(UiKit.Meta("Capture", snapshot?.CaptureMode ?? "Not captured"));
			meta.Add(UiKit.Meta("Captured (UTC)", snapshot != null ? FormatTimestamp(snapshot.TimestampUtc) : "Waiting for first Play Mode session"));
			meta.Add(UiKit.Meta("Freshness", snapshot != null ? FreshnessLabel(snapshot) : "Not captured"));
			meta.Add(UiKit.Meta("Counters", snapshot?.Metrics != null ? snapshot.Metrics.Count.ToString("N0") : "0"));
			if (snapshot != null) { meta.Add(UiKit.Meta("Samples", snapshot.FrameSamples.ToString("N0"))); meta.Add(UiKit.Meta("Quality", snapshot.EvidenceQuality ?? "Unknown")); }
			hero.Add(meta);

			if (snapshot != null && !string.IsNullOrEmpty(snapshot.BuildId))
				hero.Add(UiKit.Text("Build " + snapshot.BuildId + (string.IsNullOrEmpty(snapshot.Scenario) ? string.Empty : " • " + snapshot.Scenario), "po-runtime-hero-note"));
			if (snapshot != null && !string.IsNullOrEmpty(snapshot.CommitId))
				hero.Add(UiKit.Text("Commit " + snapshot.CommitId, "po-runtime-hero-note"));

			var option = UiKit.Column("po-runtime-option");
			var toggle = new Toggle("Capture a memory snapshot automatically when Play Mode exits") { value = settings.captureMemorySnapshotOnPlayModeExit };
			toggle.tooltip = "Off by default. When enabled, Project Optimizer requests one Memory Profiler snapshot after each Editor Play Mode session exits.";
			toggle.RegisterValueChangedCallback(evt =>
			{
				settings.captureMemorySnapshotOnPlayModeExit = evt.newValue;
				settings.Save();
				Feedback.Show(evt.newValue ? "Automatic memory snapshot capture enabled." : "Automatic memory snapshot capture disabled.");
			});
			option.Add(toggle);
			option.Add(UiKit.Text("Memory snapshots are substantially heavier than the compact counters above. Keep this off during ordinary iteration and enable it only when an object/heap snapshot is needed.", "po-runtime-option-help"));
			hero.Add(option);
			return hero;
		}

		void ImportPlayerReport()
		{
			RuntimeReportImporter.Import();
			Refresh();
		}

		static VisualElement BuildAutomaticCaptureInfo()
		{
			var card = UiKit.Column("po-card");
			card.Add(UiKit.Text("Automatic runtime capture is lightweight", "po-runtime-info-title"));
			card.Add(UiKit.Text("Project Optimizer records available profiler counters during Editor Play Mode and saves a compact baseline when Play Mode exits. A Memory Profiler snapshot is never created unless the explicit opt-in is enabled or Capture Now is pressed.", "po-rule-why"));
			return card;
		}

		static VisualElement BuildEvidenceNote(string note)
		{
			var card = UiKit.Column("po-runtime-evidence-note");
			card.Add(UiKit.Text("EVIDENCE QUALITY", "po-runtime-note-kicker"));
			card.Add(UiKit.Text(note ?? "No evidence note was stored with this sample.", "po-runtime-note-text"));
			return card;
		}

		static VisualElement BuildMetrics(List<RuntimeEvidenceMetric> metrics)
		{
			var card = UiKit.Column("po-card");
			if (metrics == null || metrics.Count == 0)
			{
				card.Add(UiKit.Empty("No readable counters", "Counter names vary by Unity version and target. Missing counters are omitted instead of inventing zeroes."));
				return card;
			}

			var header = UiKit.Row("po-runtime-metric-header");
			header.Add(UiKit.Text("Metric", "po-runtime-metric-name"));
			header.Add(UiKit.Text("Average", "po-runtime-metric-value"));
			header.Add(UiKit.Text("P95", "po-runtime-metric-stat"));
			header.Add(UiKit.Text("Max", "po-runtime-metric-stat"));
			header.Add(UiKit.Text("Samples", "po-runtime-metric-stat"));
			header.Add(UiKit.Text("Quality", "po-runtime-metric-quality"));
			header.Add(UiKit.Text("Evidence", "po-runtime-metric-evidence"));
			card.Add(header);

			foreach (var metric in metrics)
			{
				if (metric == null) continue;
				var row = UiKit.Row("po-runtime-metric-row");
				row.Add(UiKit.Text(metric.Name ?? "Unknown", "po-runtime-metric-name"));
				row.Add(UiKit.Text(FormatMetric(metric.Average, metric.Unit), "po-runtime-metric-value"));
				row.Add(UiKit.Text(FormatMetric(metric.P95, metric.Unit), "po-runtime-metric-stat"));
				row.Add(UiKit.Text(FormatMetric(metric.Max, metric.Unit), "po-runtime-metric-stat"));
				row.Add(UiKit.Text(metric.Samples.ToString("N0"), "po-runtime-metric-stat"));
				row.Add(UiKit.Text(string.IsNullOrEmpty(metric.Quality) ? "Unknown" : metric.Quality, "po-runtime-metric-quality"));
				row.Add(UiKit.Text(string.IsNullOrEmpty(metric.Evidence) ? "Unavailable" : metric.Evidence, "po-runtime-metric-evidence"));
				row.tooltip = "Last: " + FormatMetric(metric.Last, metric.Unit) + "\nQuality: " + (metric.Quality ?? "Unknown") + "\nEvidence: " + (metric.Evidence ?? "Unavailable");
				card.Add(row);
			}
			if (card.childCount > 1)
				card[card.childCount - 1].AddToClassList("po-runtime-metric-last");
			return card;
		}

		static VisualElement BuildMemorySnapshotList()
		{
			var card = UiKit.Column("po-card");
			card.Add(UiKit.Text("Memory snapshots", "po-section-title"));
			string folder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ProjectOptimizerConstants.MemorySnapshotsFolder));
			if (!Directory.Exists(folder))
			{
				card.Add(UiKit.Text("No Project Optimizer snapshots have been captured in this project.", "po-rule-why"));
				return card;
			}
			var files = new List<FileInfo>();
			foreach (string file in Directory.GetFiles(folder, "*.snap", SearchOption.TopDirectoryOnly))
				files.Add(new FileInfo(file));
			files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
			int shown = Math.Min(files.Count, 8);
			for (int i = 0; i < shown; i++)
			{
				var info = files[i];
				var row = UiKit.Row("po-row");
				row.Add(UiKit.Text(info.Name, "po-row-name"));
				row.Add(UiKit.Text(DisplayUtility.FormatBytes(info.Length), "po-slot-value"));
				row.Add(UiKit.Text(info.LastWriteTime.ToString("g"), "po-slot-path"));
				row.tooltip = "Memory Profiler snapshot generated by Project Optimizer. Open it with the Memory Profiler package for object-level attribution.";
				card.Add(row);
			}
			if (files.Count == 0) card.Add(UiKit.Text("No .snap files are present.", "po-rule-why"));
			return card;
		}

		static string FreshnessLabel(RuntimeEvidenceSnapshot snapshot)
		{
			string reason;
			var state = EvidenceIdentity.Evaluate(snapshot.TimestampUtc, snapshot.UnityVersion, snapshot.ProjectFingerprint, snapshot.Platform, EditorUserBuildSettings.activeBuildTarget.ToString(), TimeSpan.FromHours(24), out reason);
			return state + (string.IsNullOrEmpty(reason) ? string.Empty : " — " + reason);
		}

		static string FormatTimestamp(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return "Not captured";
			if (DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
				return parsed.ToUniversalTime().ToString("dd MMM yyyy, HH:mm:ss 'UTC'");
			return raw;
		}

		static string FormatMetric(double value, string unit)
		{
			if (string.Equals(unit, "bytes", StringComparison.OrdinalIgnoreCase))
				return DisplayUtility.FormatBytes((long)Math.Max(0.0, value));
			if (string.Equals(unit, "ms", StringComparison.OrdinalIgnoreCase)) return value.ToString("0.###") + " ms";
			return value.ToString("0.###");
		}
	}
}

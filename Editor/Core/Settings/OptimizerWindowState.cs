using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One column's user-set width, by column key.</summary>
	[Serializable]
	public sealed class ColumnWidthEntry
	{
		public string Key;
		public float Width;
	}

	/// <summary>One sorted column, in priority order.</summary>
	[Serializable]
	public sealed class SortEntry
	{
		public string Key;
		public bool Ascending;
	}

	/// <summary>Everything one tab's table remembers about how the user last had it arranged.</summary>
	[Serializable]
	public sealed class TabUiState
	{
		public string TabId;
		public string Search = string.Empty;

		/// <summary>Null means every rule. Per tab: a rule id belongs to exactly one tab's data.</summary>
		public string RuleFilter;

		/// <summary>Null means every kind: "verified" | "review" | "nofix" | "stale".</summary>
		public string FixKindFilter;

		/// <summary>Duplicates tab only: null/all, "referenced", or "referencedCopies".</summary>
		public string ReferencedFilter;

		/// <summary>Legacy boolean filter state from the old chip UI. Read only for migration; new saves use ReferencedFilter.</summary>
		public bool ReferencedOnly;

		public List<ColumnWidthEntry> ColumnWidths = new List<ColumnWidthEntry>();
		public List<SortEntry> Sort = new List<SortEntry>();

		/// <summary>
		/// Selection identity. Guid is preferred on restore - it survives a rename or move that a bare
		/// path would not - with Path as the fallback for rows that never had one, such as the
		/// Project Settings pseudo-assets. See <see cref="CategoryPage.RestoreSelectionAndSort"/>.
		/// </summary>
		public List<string> SelectedGuids = new List<string>();
		public List<string> SelectedPaths = new List<string>();
	}

	/// <summary>
	/// The Project Optimizer window's UI state - active tab, filters, search, sort, column widths and
	/// selection - kept across a domain reload and, since this is backed by a file, across an editor
	/// restart too.
	/// <para>
	/// Deliberately separate from <see cref="OptimizerSettings"/>: that file is shared project
	/// configuration, meant to be identical for every teammate and worth diffing in version control.
	/// This one is "where I left the window", which is personal, disposable, and never worth a commit
	/// - so it lives under Library, like everything else this tool generates, rather than
	/// ProjectSettings.
	/// </para>
	/// <para>
	/// Also deliberately separate from the scan result itself - see <see cref="ScanResultStore"/> -
	/// because the two change at very different rates and for different reasons. Dragging a column
	/// border has nothing to do with what a scan found; saving the whole scan on every such tweak would
	/// make every small UI change pay for a much bigger write.
	/// </para>
	/// </summary>
	[FilePath(ProjectOptimizerConstants.WindowStatePath, FilePathAttribute.Location.ProjectFolder)]
	public sealed class OptimizerWindowState : ScriptableSingleton<OptimizerWindowState>
	{
		/// <summary>Bumped whenever this class's shape changes incompatibly. See <see cref="EnsureLoaded"/>.</summary>
		const int k_CurrentVersion = 1;

		public int Version = k_CurrentVersion;

		public string ActiveTab;

		// Shared across every tab - mirrors CategoryPage's own static severity/issues-only/folder
		// filters, which are process-global by design (see the remarks on CategoryPage.s_Severity).
		public bool SeverityCritical = true;
		public bool SeverityWarning = true;
		public bool SeverityInfo = true;
		public bool IssuesOnly = true;
		public string FolderFilter;

		/// <summary>Mirrors ProjectOptimizerWindow.m_StaleAcknowledged: the settings revision last dismissed.</summary>
		public int StaleAcknowledgedRevision = -1;

		/// <summary>
		/// Which colour theme the window is drawn in - see <see cref="OptimizerThemes"/>. Personal
		/// window state like everything else here, not project configuration, which is why it lives in
		/// this file and not in the shared, version-controlled <see cref="OptimizerSettings"/>.
		/// </summary>
		public string ThemeId = OptimizerThemes.DefaultIdDark;
		public string LastDarkThemeId = OptimizerThemes.DefaultIdDark;
		public string LastLightThemeId = OptimizerThemes.DefaultIdLight;

		public List<TabUiState> Tabs = new List<TabUiState>();

		bool m_Loaded;

		/// <summary>
		/// Confirms the data <see cref="ScriptableSingleton{T}"/> just loaded from disk is a version
		/// this build understands, discarding it otherwise.
		/// <para>
		/// The file is read the moment <see cref="ScriptableSingleton{T}.instance"/> is first touched,
		/// before any code here can run, so version validation cannot happen in a constructor - it has
		/// to be an explicit first step the caller takes, same as <see cref="ScanResultStore"/>'s own
		/// validation on load.
		/// </para>
		/// </summary>
		public void EnsureLoaded()
		{
			if (m_Loaded) return;
			m_Loaded = true;

			if (Version == k_CurrentVersion) return;

			Debug.Log(ProjectOptimizerConstants.LogPrefix + " Saved window state is from a different version of the tool ("
					  + Version + ", expected " + k_CurrentVersion + "). Starting with default window state.");
			ResetToDefaults();
		}

		public void ResetToDefaults()
		{
			Version = k_CurrentVersion;
			ActiveTab = null;
			SeverityCritical = SeverityWarning = SeverityInfo = true;
			IssuesOnly = true;
			FolderFilter = null;
			StaleAcknowledgedRevision = -1;
			ThemeId = OptimizerThemes.DefaultIdDark;
			LastDarkThemeId = OptimizerThemes.DefaultIdDark;
			LastLightThemeId = OptimizerThemes.DefaultIdLight;
			Tabs.Clear();
		}

		public TabUiState FindTab(string tabId)
		{
			if (string.IsNullOrEmpty(tabId)) return null;
			for (int i = 0; i < Tabs.Count; i++)
				if (Tabs[i].TabId == tabId) return Tabs[i];
			return null;
		}

		/// <summary>Writes this instance to disk. Best-effort: a failed save leaves the previous file intact.</summary>
		public void Persist()
		{
			try
			{
				Save(true);
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not save window state: " + e.Message);
			}
		}
	}
}

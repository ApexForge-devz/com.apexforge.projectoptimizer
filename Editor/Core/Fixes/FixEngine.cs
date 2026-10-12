using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ApexForge.ProjectOptimizer
{
	public struct FixRequest
	{
		public AssetEntry Entry;
		public OptimizationIssue Issue;

		/// <summary>Concrete option metadata, when this request came from a selected Review option.</summary>
		public FixOption OptionOverride;

		public string ActionId => OptionOverride?.StableActionId(Issue?.RuleId) ?? ((Issue?.RuleId ?? "rule") + "_default");
		public FixKind EffectiveFixKind => Issue?.FixKind ?? FixKind.None;
		public bool EffectiveDestructive => EffectiveRisk == FixRisk.Destructive;
		public bool EffectiveBackupRequired => FixContract.EffectiveBackupRequired(Issue, OptionOverride);
		public FixRisk EffectiveRisk => OptionOverride?.EffectiveRisk(Issue?.Risk ?? FixRisk.Unspecified) ??
			(Issue?.Risk != FixRisk.Unspecified ? Issue.Risk : FixRisk.Medium);

		/// <summary>The delegate this request actually runs: the chosen option's, or the issue's own.</summary>
		public Action<FixContext> EffectiveFix => OptionOverride?.Apply ?? Issue?.Fix;

		/// <summary>The verifier that matches whichever delegate <see cref="EffectiveFix"/> resolves to.</summary>
		public Func<FixContext, bool> EffectiveVerify => FixContract.EffectiveVerify(Issue, OptionOverride);
		public long EffectiveSavings => FixContract.EffectiveSavings(Issue, OptionOverride);
	}

	/// <summary>
	/// What became of one requested fix. Every request ends in exactly one of these, so the counts
	/// in a <see cref="FixReport"/> always add up to the number of fixes asked for.
	/// </summary>
	public enum FixOutcome
	{
		/// <summary>Refused before it ran: no backup could be taken, or backups were off for a destructive fix.</summary>
		Blocked = 0,
		/// <summary>Ran and threw, or its asset could not be committed.</summary>
		Failed = 1,
		/// <summary>Ran, then was undone - because a sibling fix failed, or because verification said no.</summary>
		RolledBack = 2,
		/// <summary>Ran, and its own verifier confirmed the result after the import completed.</summary>
		AppliedVerified = 3,
		/// <summary>Ran, and the rule has no verifier. Believed, not confirmed.</summary>
		AppliedUnverified = 4,
		/// <summary>Ran and verified, but changed no bytes: the asset was already in the target state.</summary>
		NoOp = 5
	}

	public sealed class FixReport
	{
		/// <summary>How this batch was requested - Fix All, a single fix, or an explicit Review Batch.</summary>
		public FixExecutionMode Mode;

		/// <summary>Ran and was confirmed afterwards.</summary>
		public int AppliedVerified;

		/// <summary>
		/// Ran, and the rule ships no verifier, so the tool is repeating the fix's own claim.
		/// Counted apart from <see cref="AppliedVerified"/> because it is a weaker statement, and
		/// summarising the two together is how "fixed 12 issues" comes to include changes nobody
		/// checked.
		/// </summary>
		public int AppliedUnverified;

		/// <summary>Verified, but the files did not change: the asset already had the target state.</summary>
		public int NoOp;

		public int Failed;

		/// <summary>Fixes undone again, either by a sibling's failure or by their own verifier.</summary>
		public int RolledBack;

		/// <summary>Verifiers that came back negative. Each of these also counts as rolled back.</summary>
		public int VerificationFailed;

		/// <summary>Fixes refused before they ran, because their asset could not be backed up first.</summary>
		public int Blocked;

		/// <summary>
		/// Assets whose rollback did not fully succeed. Anything above zero means files on disk are
		/// in a state nobody chose, and is reported as loudly as the UI can manage.
		/// </summary>
		public int RollbackFailed;

		/// <summary>Everything that ran and was left in place.</summary>
		public int Applied => AppliedVerified + AppliedUnverified + NoOp;

		/// <summary>True when every fix that ran was confirmed and none was left unchecked.</summary>
		public bool FullyVerified => AppliedUnverified == 0
									  && VerificationFailed == 0
									  && Failed == 0
									  && Blocked == 0
									  && RollbackFailed == 0;

		public int AssetsTouched;
		public long EstimatedSavings;
		public string BackupFolder;
		public readonly List<string> Errors = new List<string>();

		/// <summary>Assets actually modified, so the scan can refresh just these instead of everything.</summary>
		public readonly List<string> TouchedPaths = new List<string>();

		/// <summary>
		/// Paths whose contents were restored. The scan has to re-read these too: they were changed
		/// and changed back, and any row describing them was written before either.
		/// </summary>
		public readonly List<string> RolledBackPaths = new List<string>();

		public readonly List<FixResultRecord> Results = new List<FixResultRecord>();
	}

	public sealed class FixResultRecord
	{
		public string AssetPath;
		public string RuleId;
		public string ActionId;
		public string OptionLabel;
		public FixKind FixKind;
		public FixRisk Risk;
		public bool Destructive;
		public bool BackupRequired;
		public long EstimatedSavings;
		public string Outcome;
		public string Error;
	}

	/// <summary>
	/// What one revert attempt achieved. Returned rather than a bare count because a partial restore
	/// has to be distinguishable from a complete one - the batch stays pending in that case, and the
	/// user has to be told which files did not come back.
	/// </summary>
	public sealed class RevertReport
	{
		public int Expected;
		public int Restored;
		public int RestoreFailed;
		public int IntegrityFailed;
		public int ReimportFailed;
		public readonly List<string> Errors = new List<string>();

		/// <summary>True only when every recorded file was restored and reimported.</summary>
		public bool Complete => Expected > 0
								&& Restored == Expected
								&& RestoreFailed == 0
								&& IntegrityFailed == 0
								&& ReimportFailed == 0;
	}

	/// <summary>
	/// Applies fixes as a transaction: snapshot, apply, commit, verify, and undo anything that could
	/// not be confirmed.
	/// <para>
	/// The engine owns the editor-side lifecycle - the asset-editing scope, the import settings it
	/// tunes, and when saves and reimports happen. It used to share that with whichever UI page
	/// called it, and the two callers did it differently, so whether verification observed a
	/// completed import depended on which button was pressed. There is one path now.
	/// </para>
	/// <para>
	/// Work happens in two domains. ProjectSettings fixes are applied and verified outside the
	/// asset-editing transaction because they mutate Unity's global project configuration rather
	/// than an imported asset. Ordinary asset fixes then run inside one asset-editing scope, where
	/// Unity can defer imports until the transaction closes.
	/// </para>
	/// </summary>
	public static class FixEngine
	{
		static readonly string k_BackupRoot = ProjectOptimizerConstants.FixBackupFolder;
		const string k_RevertedMarker = "reverted.marker";
		const string k_ManifestName = "manifest.txt";

		/// <summary>
		/// Manifest schema. v1 was "backupFile|assetPath"; v2 adds the captured size and hash, and
		/// v3 adds directory/created entries so fixes that create assets can be rolled back safely.
		/// </summary>
		const int k_ManifestVersion = 3;
		const string k_ManifestHeaderPrefix = "#projectoptimizer-manifest v";

		static readonly HashSet<string> k_InPlaceEditedExtensions = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase
		)
		{
			".mat",
			".anim",
			".prefab",
			".asset",
			".controller",
			".unity",
			".shadergraph",
			".shader",
			".shadervariants",
			".spriteatlas",
			".spriteatlasv2",
		};

		/// <summary>One captured file: where the copy is, where it came from, and what it looked like.</summary>
		sealed class ManifestEntry
		{
			public string BackupName;
			public string AssetPath;
			public long Size;
			public string Hash;
			public bool IsDirectory;
			public bool IsCreated;

			public string Serialize()
			{
				return BackupName + "|" + AssetPath + "|"
					   + Size.ToString(CultureInfo.InvariantCulture) + "|" + (Hash ?? string.Empty)
					   + "|" + (IsDirectory ? "1" : "0") + "|" + (IsCreated ? "1" : "0");
			}

			/// <summary>Parses a manifest line. v1 lines have no size or hash and are read as unverifiable.</summary>
			public static ManifestEntry Parse(string line)
			{
				if (string.IsNullOrWhiteSpace(line)) return null;
				if (line.StartsWith("#", StringComparison.Ordinal)) return null;

				var fields = line.Split('|');
				if (fields.Length < 2) return null;

				var entry = new ManifestEntry
				{
					BackupName = fields[0],
					AssetPath = fields[1],
					Size = -1
				};

				if (fields.Length >= 4)
				{
					if (long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
						entry.Size = size;
					entry.Hash = fields[3].Length > 0 ? fields[3] : null;
				}
				if (fields.Length >= 5) entry.IsDirectory = fields[4] == "1";
				if (fields.Length >= 6) entry.IsCreated = fields[5] == "1";
				return entry;
			}
		}

		/// <summary>
		/// One batch's snapshot folder and what it holds.
		/// <para>
		/// The manifest is appended to as each asset is captured rather than written once at the
		/// end. That is deliberate: a batch interrupted half way still has to be revertable, and
		/// every line is complete and self-describing when it is written, so a truncated manifest is
		/// a shorter valid manifest rather than a corrupt one.
		/// </para>
		/// </summary>
		sealed class BackupBatch
		{
			public readonly string Folder;
			readonly HashSet<string> m_Captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			bool m_HeaderWritten;

			public BackupBatch(string folder) { Folder = folder; }

			/// <summary>Snapshots one exact file without assuming it has a Unity .meta file.</summary>
			public bool TryCaptureFile(string assetPath, List<ManifestEntry> into, out string error)
			{
				error = null;
				try { return TryCopy(assetPath, into, out error); }
				catch (Exception e) { error = e.Message; return false; }
			}

			/// <summary>Snapshots one file plus its .meta. Returns false if anything could not be copied.</summary>
			public bool TryRecordCreatedAsset(string assetPath, List<ManifestEntry> into, out string error)
			{
				error = null;
				try
				{
					if (string.IsNullOrEmpty(assetPath))
					{
						error = "created asset path is empty";
						return false;
					}
					if (!m_Captured.Add("<created>" + assetPath)) return true;
					into.Add(new ManifestEntry { BackupName = string.Empty, AssetPath = assetPath, Size = -2, Hash = null, IsDirectory = false, IsCreated = true });
					return true;
				}
				catch (Exception e) { error = e.Message; return false; }
			}

			public bool TryCapture(string assetPath, List<ManifestEntry> into, out string error)
			{
				error = null;
				try
				{
					return TryCopy(assetPath, into, out error)
						   && TryCopy(assetPath + ".meta", into, out error);
				}
				catch (Exception e)
				{
					error = e.Message;
					return false;
				}
			}

			/// <summary>Copies one file if it exists. A file that is not there was never at risk.</summary>
			bool TryCopy(string assetPath, List<ManifestEntry> into, out string error)
			{
				error = null;
				var source = AssetPathUtility.ToAbsolutePath(assetPath);
				bool directory = Directory.Exists(source);
				if (!File.Exists(source) && !directory) return true;

				if (!m_Captured.Add(assetPath)) return true;

				if (directory)
				{
					into.Add(new ManifestEntry { BackupName = string.Empty, AssetPath = assetPath, Size = -1, Hash = null, IsDirectory = true });
					return true;
				}

				var token = Convert.ToBase64String(System.Security.Cryptography.MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(assetPath)))
					.Replace('+', '-').Replace('/', '_').TrimEnd('=');
				var backupName = token + "-" + Path.GetFileName(assetPath).Replace(':', '_');
				var destination = Path.Combine(Folder, backupName);

				try { File.Copy(source, destination, false); }
				catch (Exception e)
				{
					m_Captured.Remove(assetPath);
					error = "could not copy '" + assetPath + "': " + e.Message;
					return false;
				}

				var info = new FileInfo(destination);
				var original = new FileInfo(source);
				if (!info.Exists || info.Length != original.Length)
				{
					m_Captured.Remove(assetPath);
					error = "the backup copy of '" + assetPath + "' is incomplete";
					return false;
				}

				into.Add(new ManifestEntry
				{
					BackupName = backupName,
					AssetPath = assetPath,
					Size = info.Length,
					Hash = FileUtility.Md5OfAbsolute(destination)
				});
				return true;
			}

			/// <summary>
			/// Records these captures in the manifest. Returns false when the manifest could not be
			/// written, which makes the snapshot unusable even though the copies exist - a backup
			/// nothing can find is not a backup.
			/// </summary>
			public bool TryAppendToManifest(List<ManifestEntry> entries)
			{
				if (entries == null || entries.Count == 0) return true;

				try
				{
					var text = new StringBuilder();
					if (!m_HeaderWritten)
					{
						// Enough identity to make a folder found weeks later diagnosable: what wrote
						// it, which editor, and when.
						text.Append(k_ManifestHeaderPrefix).Append(k_ManifestVersion)
							.Append(" tool=").Append(OptimizerVersion.Schema)
							.Append(" unity=").Append(Application.unityVersion)
							.Append(" created=").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture))
							.AppendLine();
					}

					for (int i = 0; i < entries.Count; i++) text.AppendLine(entries[i].Serialize());

					File.AppendAllText(Path.Combine(Folder, k_ManifestName), text.ToString());
					m_HeaderWritten = true;
					return true;
				}
				catch (Exception e)
				{
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Could not write the backup manifest: " + e.Message);
					return false;
				}
			}

			/// <summary>Puts these files back, and says how completely it managed it.</summary>
			public RevertReport Restore(List<ManifestEntry> entries)
			{
				return RestoreEntries(Folder, entries);
			}
		}

		public static bool IsEditorTransactionActive => EditorTransaction.IsActive;

		/// <summary>
		/// The editor-side settings a fix batch changes, and the values they had before.
		/// <para>
		/// These are global editor and project preferences, not the tool's own state. Turning the
		/// import worker count up for a batch and then writing a hardcoded default back afterwards -
		/// which is what happened before - quietly replaces whatever the user had configured. So the
		/// previous values are captured and restored exactly.
		/// </para>
		/// </summary>
		struct EditorTransaction : IDisposable
		{
			static int s_Depth;
			public static bool IsActive => s_Depth > 0;

			int m_PreviousWorkerCount;
			int m_PreviousUserWorkerCount;
			AssetDatabase.RefreshImportMode m_PreviousRefreshMode;
			AssetDatabase.RefreshImportMode m_PreviousActiveRefreshMode;
			bool m_Entered;
			bool m_TunedImportSettings;
			bool m_AutoRefreshDisallowed;
			bool m_AssetEditingStarted;

			/// <summary>
			/// Enters the asset-editing scope and tunes import throughput for the batch.
			/// A nested call is a no-op that still disposes cleanly, so an inner caller cannot close
			/// the outer scope early.
			/// </summary>
			public static EditorTransaction Begin(int workers)
			{
				var transaction = new EditorTransaction();
				if (s_Depth++ > 0) return transaction;

				transaction.m_Entered = true;

				try
				{
					transaction.m_PreviousWorkerCount = AssetDatabase.DesiredWorkerCount;
					transaction.m_PreviousUserWorkerCount = EditorUserSettings.desiredImportWorkerCount;
					transaction.m_PreviousRefreshMode = EditorSettings.refreshImportMode;
					transaction.m_PreviousActiveRefreshMode = AssetDatabase.ActiveRefreshImportMode;

					AssetDatabase.DesiredWorkerCount = workers;
					EditorUserSettings.desiredImportWorkerCount = workers;
					EditorSettings.refreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
					AssetDatabase.ActiveRefreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
					transaction.m_TunedImportSettings = true;
				}
				catch (Exception e)
				{
					// Import tuning is an optimization. Losing it costs time, not correctness.
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not tune import settings for this batch: " + e.Message);
				}

				try
				{
					AssetDatabase.DisallowAutoRefresh();
					transaction.m_AutoRefreshDisallowed = true;
					AssetDatabase.StartAssetEditing();
					transaction.m_AssetEditingStarted = true;
				}
				catch
				{
					// The scope was never entered, so nothing must later try to leave it - and the
					// depth counter has to come back down or every subsequent batch would think it
					// was nested inside this one and skip entering a scope of its own.
					transaction.m_Entered = false;
					try
					{
						if (transaction.m_AssetEditingStarted)
							AssetDatabase.StopAssetEditing();
					}
					catch { }
					try
					{
						if (transaction.m_AutoRefreshDisallowed)
							AssetDatabase.AllowAutoRefresh();
					}
					catch { }
					s_Depth = Mathf.Max(0, s_Depth - 1);
					transaction.RestoreImportSettings();
					throw;
				}

				return transaction;
			}

			public void Dispose()
			{
				s_Depth = Mathf.Max(0, s_Depth - 1);
				if (!m_Entered) return;
				m_Entered = false;

				// Closing the scope is what actually runs the queued imports, so it comes first and
				// is never skipped: an editor left inside StartAssetEditing stops importing anything.
				if (m_AssetEditingStarted)
				{
					try { AssetDatabase.StopAssetEditing(); }
					catch (Exception e) { Debug.LogError(ProjectOptimizerConstants.LogPrefix + " StopAssetEditing failed: " + e.Message); }
					m_AssetEditingStarted = false;
				}

				if (m_AutoRefreshDisallowed)
				{
					try { AssetDatabase.AllowAutoRefresh(); }
					catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " AllowAutoRefresh failed: " + e.Message); }
					m_AutoRefreshDisallowed = false;
				}

				RestoreImportSettings();
			}

			/// <summary>
			/// Puts back the four editor and project settings this batch borrowed. These belong to
			/// the user, not to the tool, so they are restored to whatever they were rather than to
			/// a default that looked reasonable.
			/// </summary>
			void RestoreImportSettings()
			{
				if (!m_TunedImportSettings) return;
				m_TunedImportSettings = false;

				try
				{
					AssetDatabase.DesiredWorkerCount = m_PreviousWorkerCount;
					EditorUserSettings.desiredImportWorkerCount = m_PreviousUserWorkerCount;
					EditorSettings.refreshImportMode = m_PreviousRefreshMode;
					AssetDatabase.ActiveRefreshImportMode = m_PreviousActiveRefreshMode;
				}
				catch (Exception e)
				{
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not restore import settings: " + e.Message);
				}
			}
		}

		/// <summary>Everything the two phases need to know about one asset's fixes.</summary>
		sealed class PreflightFileState
		{
			public string Path;
			public bool Exists;
			public long Size;
			public string Hash;
		}

		sealed class AssetWork
		{
			public string Path;
			public List<FixRequest> Requests;
			public readonly List<ManifestEntry> Manifest = new List<ManifestEntry>();
			public readonly List<PreflightFileState> PreflightStates = new List<PreflightFileState>();
			public FixContext Context;

			/// <summary>Fixes that ran without throwing, always a prefix of <see cref="Requests"/>.</summary>
			public int Applied;

			public long Savings;
			public long MemoryBase;
			public long DiskBase;
			public long MemorySaved;
			public long DiskSaved;

			/// <summary>Set when the asset must be rolled back. Null while it is still a candidate.</summary>
			public string Failure;

			/// <summary>
			/// True when a fix itself threw, as opposed to the commit failing afterwards. The two
			/// account for their requests differently: a throwing fix is one Failed request, while a
			/// failed commit leaves every fix that ran to be rolled back instead.
			/// </summary>
			public bool FixThrew;

			/// <summary>Requests never attempted, because an earlier one on this asset failed.</summary>
			public int Abandoned;

			public bool Snapshotted;
			public bool Blocked;
		}

		/// <summary>
		/// Fix batches stack up: the newest batch that has not already been rolled back is the one
		/// Revert undoes, so reverting one batch exposes the one before it rather than losing it.
		/// </summary>
		public static string LastBackupFolder
		{
			get
			{
				var folders = PendingBackups();
				return folders.Count > 0 ? folders[folders.Count - 1] : string.Empty;
			}
		}

		public static int PendingBackupCount => PendingBackups().Count;

		public static bool CanRevert => PendingBackups().Count > 0;

		/// <summary>Backup folders, oldest first, that still hold changes nobody has reverted.</summary>
		static List<string> PendingBackups()
		{
			var result = new List<string>();
			try
			{
				if (!Directory.Exists(k_BackupRoot))
					return result;
				foreach (var directory in Directory.GetDirectories(k_BackupRoot))
				{
					if (File.Exists(Path.Combine(directory, k_RevertedMarker)))
						continue;
					if (!File.Exists(Path.Combine(directory, k_ManifestName)))
						continue;
					result.Add(directory);
				}
				// Folder names are a sortable timestamp followed by a tiebreaker, so ordinal order is
				// chronological order.
				result.Sort(StringComparer.Ordinal);
			}
			catch
			{
				// Backup discovery is best effort.
			}
			return result;
		}

		/// <summary>
		/// Applies a batch of fixes.
		/// <para>
		/// The caller must have obtained any confirmation it needs <em>before</em> calling this. The
		/// asset-editing scope is entered here and not before, so a dialog can never be shown while
		/// the editor is inside it, and cancelling never leaves editor settings changed.
		/// </para>
		/// <para>
		/// <paramref name="mode"/> is the one gate between "Fix All" and a Review Fix a human just
		/// approved - see <see cref="FixExecutionMode"/>. <see cref="FixExecutionMode.VerifiedBulk"/>
		/// and <see cref="FixExecutionMode.VerifiedSingle"/> (the default, and the only modes "Fix all
		/// shown" / "Fix selected" ever pass) accept only requests whose issue is
		/// <see cref="FixKind.Verified"/> issues.
		/// <see cref="FixExecutionMode.ReviewSingle"/> and <see cref="FixExecutionMode.ReviewBatch"/>
		/// additionally accept <see cref="FixKind.Review"/> requests, and exist so an explicit
		/// approval - one issue, or a whole batch a human picked - can still run through this same
		/// transactional machinery: backup, apply, verify, rollback, rather than a second, weaker
		/// path. Nothing outside this file ever needs to choose which gate applies to Fix All: it is
		/// hardwired to <see cref="FixExecutionMode.VerifiedBulk"/> at the two call sites that mean
		/// "everything currently in view".
		/// </para>
		/// </summary>
		public static FixReport Apply(IReadOnlyList<FixRequest> requests, bool createBackup = true,
			FixExecutionMode mode = FixExecutionMode.VerifiedBulk)
		{
			var report = new FixReport();
			report.Mode = mode;
			if (requests == null || requests.Count == 0)
				return report;

			bool reviewMode = mode == FixExecutionMode.ReviewSingle || mode == FixExecutionMode.ReviewBatch;
			bool verifiedMode = mode == FixExecutionMode.VerifiedSingle || mode == FixExecutionMode.VerifiedBulk;
			if (!reviewMode && !verifiedMode)
			{
				report.Errors.Add("Unknown fix execution mode: " + mode);
				return report;
			}

			// Group by the entry path. ProjectSettings entries are intentionally separated from normal
			// assets below: project-wide settings are not asset-import transactions and must never run
			// inside StartAssetEditing / the temporary import-tuning scope.
			var order = new List<string>();
			var byPath = new Dictionary<string, List<FixRequest>>(StringComparer.Ordinal);
			foreach (var request in requests)
			{
				if (request.Issue == null || request.Entry == null || string.IsNullOrEmpty(request.Entry.Path))
					continue;

				if (!ValidateRequest(request, mode, report, createBackup))
					continue;

				if (!ValidateCleanProjectSettingsTarget(request, report))
					continue;

				var path = request.Entry.Path;
				if (!byPath.TryGetValue(path, out var list))
				{
					list = new List<FixRequest>();
					byPath[path] = list;
					order.Add(path);
				}
				list.Add(request);
			}
			if (order.Count == 0)
				return report;

			BackupBatch batch = null;
			if (createBackup)
			{
				var folder = NewBackupFolder();
				if (folder == null)
				{
					foreach (var path in order) report.Blocked += byPath[path].Count;
					report.Errors.Add("No fixes were applied: a backup folder could not be created under "
								  + k_BackupRoot + ".");
					return report;
				}
				batch = new BackupBatch(folder);
				report.BackupFolder = folder;
				if (!TestBackupFolderWritable(folder))
				{
					foreach (var path in order) report.Blocked += byPath[path].Count;
					report.Errors.Add("No fixes were applied: the backup folder is not writable: " + folder);
					return report;
				}
			}

			var work = new List<AssetWork>(order.Count);
			for (int i = 0; i < order.Count; i++)
			{
				var pathRequests = byPath[order[i]];
				var diskBase = FileUtility.FileSizeOf(order[i]);
				for (int r = 0; r < pathRequests.Count; r++) if (pathRequests[r].Entry != null && pathRequests[r].Entry.FileSize > diskBase) diskBase = pathRequests[r].Entry.FileSize;
				var item = new AssetWork { Path = order[i], Requests = pathRequests, DiskBase = Math.Max(0L, diskBase) };
				for (int r = 0; r < pathRequests.Count; r++)
					if (pathRequests[r].Entry != null && pathRequests[r].Entry.RuntimeSize > item.MemoryBase) item.MemoryBase = pathRequests[r].Entry.RuntimeSize;
				work.Add(item);
			}

			for (int i = 0; i < work.Count; i++)
			{
				var preflightWork = work[i];
				if (!PreflightWritableAndCaptureState(preflightWork, report))
				{
					preflightWork.Blocked = true;
					report.Blocked += preflightWork.Requests.Count;
					AddResultRecords(preflightWork, report, "Blocked");
				}
			}
			work.RemoveAll(w => w == null || w.Blocked);
			if (work.Count == 0) return report;

			var projectWorks = new List<AssetWork>();
			var assetWorks = new List<AssetWork>();
			for (int i = 0; i < work.Count; i++)
				if (IsProjectSettingsPath(work[i].Path)) projectWorks.Add(work[i]);
				else assetWorks.Add(work[i]);

			try
			{
				// ------------------------------------------------------------------
				// Phase 1: Project settings
				// ------------------------------------------------------------------
				// Project settings are global state, not ordinary imported assets. Snapshot only the
				// settings files actually affected by this batch. Unrelated dirty settings must not block
				// an otherwise safe fix, while a dirty target must still block to avoid committing the
				// user's unsaved edits.
				bool projectPhaseFailed = false;
				if (projectWorks.Count > 0)
				{
					var preExistingProjectSettingDirtyIds = CaptureDirtyProjectSettingsObjects();
					List<ManifestEntry> projectManifest = null;
					if (!ValidateProjectSettingsTargetsBeforeBatch(projectWorks, report))
					{
						foreach (var projectWork in projectWorks)
						{
							projectWork.Blocked = true;
							report.Blocked += projectWork.Requests.Count;
							AddResultRecords(projectWork, report, "Blocked");
						}
						projectPhaseFailed = true;
					}
					else if (!CaptureProjectSettingsSnapshot(batch, projectWorks, out projectManifest, out string snapshotError))
					{
						foreach (var projectWork in projectWorks)
						{
							projectWork.Blocked = true;
							report.Blocked += projectWork.Requests.Count;
							AddResultRecords(projectWork, report, "Blocked");
						}
						report.Errors.Add("No project-setting fixes were applied: "
						+ (snapshotError ?? "the ProjectSettings snapshot could not be created") + ".");
						projectPhaseFailed = true;
					}
					else
					{
						var projectContextWorks = new List<AssetWork>();
						for (int i = 0; i < projectWorks.Count; i++)
						{
							var projectWork = projectWorks[i];
							if (projectWork.Blocked)
								continue;
							projectContextWorks.Add(projectWork);
							ApplyProjectSettingsWork(projectWork, batch, report);
						}

						// Create exactly one result record per attempted request before verification or rollback.
						for (int i = 0; i < projectContextWorks.Count; i++)
							AddResultRecords(projectContextWorks[i], report, "Pending");

						AssetWork projectFailure = null;
						for (int i = 0; i < projectContextWorks.Count; i++)
							if (projectContextWorks[i].Failure != null) { projectFailure = projectContextWorks[i]; break; }

						if (projectFailure != null)
						{
							projectPhaseFailed = true;
							RollbackProjectSettingsWorks(projectContextWorks, batch, projectManifest, report,
								"A project-settings fix failed before the project-settings batch could be committed.");
						}
						else if (!PersistProjectSettings(report, preExistingProjectSettingDirtyIds, projectWorks))
						{
							projectPhaseFailed = true;
							for (int i = 0; i < projectContextWorks.Count; i++)
								if (projectContextWorks[i].Applied > 0)
									projectContextWorks[i].Failure = projectContextWorks[i].Path + ": project settings could not be persisted; the project-settings batch was rolled back.";
							RollbackProjectSettingsWorks(projectContextWorks, batch, projectManifest, report,
								"Project settings could not be persisted.");
						}
						else if (!VerifyProjectSettingsWorks(projectContextWorks, report))
						{
							projectPhaseFailed = true;
							RollbackProjectSettingsWorks(projectContextWorks, batch, projectManifest, report,
								"A project-settings verifier failed; the project-settings phase was rolled back atomically.");
						}
						else
						{
							FinalizeProjectSettingsResults(projectContextWorks, report);
						}
					}
				}

				// ------------------------------------------------------------------
				// Phase 2: ordinary assets
				// ------------------------------------------------------------------
				if (projectPhaseFailed)
				{
					// The project-settings phase is atomic. Once it fails, ordinary assets are not
					// touched, so give every skipped asset request an explicit terminal result.
					for (int i = 0; i < assetWorks.Count; i++)
						AddResultRecords(assetWorks[i], report, "NotApplied");
				}
				else if (assetWorks.Count > 0)
				{
					using (EditorTransaction.Begin(assetWorks.Count > 32 ? 16 : 12))
					{
						for (int i = 0; i < assetWorks.Count; i++)
						{
							EditorUtility.DisplayProgressBar(ProjectOptimizerConstants.ProductName,
								$"Fixing ({(i + 1)}/{(float)assetWorks.Count}) {assetWorks[i].Path}",
								(i + 1) / (float)assetWorks.Count);
							ApplyToAsset(assetWorks[i], batch, report);
						}
					}

					for (int i = 0; i < assetWorks.Count; i++)
					{
						EditorUtility.DisplayProgressBar(ProjectOptimizerConstants.ProductName,
							"Checking " + assetWorks[i].Path, (i + 1) / (float)assetWorks.Count);
						Settle(assetWorks[i], batch, report);
					}
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}

			return report;
		}

		static bool PreflightWritableAndCaptureState(AssetWork work, FixReport report)
		{
			if (work == null || string.IsNullOrEmpty(work.Path)) return false;
			if (!UnityEditorUtility.CanEditPath(work.Path, out var reason))
			{ report.Errors.Add("Blocked fix on " + work.Path + ": " + reason); return false; }
			var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { work.Path };
			if (work.Path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) targets.Add(work.Path + ".meta");
			foreach (var request in work.Requests)
				if (request.Issue != null) foreach (var target in GetProjectSettingsTargetPaths(request)) targets.Add(target);
			foreach (var target in targets)
			{
				if (!UnityEditorUtility.CanEditPath(target, out var targetReason))
				{
					report.Errors.Add("Blocked fix on " + work.Path + ": affected target " + target + " is not editable: " + targetReason);
					return false;
				}
				string absolute = AssetPathUtility.ToAbsolutePath(target);
				bool exists = File.Exists(absolute);
				work.PreflightStates.Add(new PreflightFileState { Path = target, Exists = exists, Size = exists ? new FileInfo(absolute).Length : 0L, Hash = FileUtility.Md5OfAbsolute(absolute) });
			}
			return true;
		}

		static bool PreflightStateUnchanged(AssetWork work, FixReport report)
		{
			if (work == null) return false;
			for (int i = 0; i < work.PreflightStates.Count; i++)
			{
				var expected = work.PreflightStates[i];
				if (!UnityEditorUtility.CanEditPath(expected.Path, out var editReason))
				{
					report.Errors.Add(work.Path + ": blocked because affected target " + expected.Path + " is no longer editable: " + editReason);
					return false;
				}
				string absolute = AssetPathUtility.ToAbsolutePath(expected.Path);
				bool exists = File.Exists(absolute);
				if (exists != expected.Exists) { report.Errors.Add(work.Path + ": blocked because " + expected.Path + " changed after analysis (existence changed)."); return false; }
				if (!exists) continue;
				var info = new FileInfo(absolute);
				string hash = FileUtility.Md5OfAbsolute(absolute);
				if (info.Length != expected.Size || string.IsNullOrEmpty(hash) || !string.Equals(hash, expected.Hash, StringComparison.OrdinalIgnoreCase)) { report.Errors.Add(work.Path + ": blocked because " + expected.Path + " changed after analysis. Rescan before applying this fix."); return false; }
			}
			return true;
		}

		static bool IsProjectSettingsPath(string path)
		{
			return !string.IsNullOrEmpty(path) && path.StartsWith("ProjectSettings/", StringComparison.Ordinal);
		}

		static string[] GetProjectSettingsFiles()
		{
			var result = new List<string>();
			try
			{
				var root = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, "ProjectSettings");
				if (!Directory.Exists(root)) return result.ToArray();

				var files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly);
				for (int i = 0; i < files.Length; i++)
				{
					var name = Path.GetFileName(files[i]);
					if (string.Equals(name, "ProjectVersion.txt", StringComparison.OrdinalIgnoreCase)) continue;
					if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
					result.Add("ProjectSettings/" + name.Replace('\\', '/'));
				}
			}
			catch { }
			result.Sort(StringComparer.Ordinal);
			return result.ToArray();
		}

		static HashSet<string> GetProjectSettingsTargetPaths(List<AssetWork> works)
		{
			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (works == null) return paths;

			for (int i = 0; i < works.Count; i++)
			{
				var work = works[i];
				if (work == null || work.Requests == null) continue;
				for (int j = 0; j < work.Requests.Count; j++)
					foreach (var path in GetProjectSettingsTargetPaths(work.Requests[j]))
						paths.Add(path);
			}

			return paths;
		}

		static IEnumerable<string> GetProjectSettingsTargetPaths(FixRequest request)
		{
			if (request.Issue == null) yield break;

			switch (request.Issue.RuleId)
			{
				case "PRJ064":
				case "PRJ065":
				case "PRJ074":
					yield return "ProjectSettings/EditorSettings.asset";
					yield break;
				case "PRJ067":
				case "PAS1005":
					yield return "ProjectSettings/EditorBuildSettings.asset";
					yield break;
			}

			if (request.Entry != null && IsProjectSettingsPath(request.Entry.Path))
				yield return request.Entry.Path;
		}

		static bool ValidateProjectSettingsTargetsBeforeBatch(List<AssetWork> works, FixReport report)
		{
			var files = GetProjectSettingsTargetPaths(works);
			if (files.Count == 0)
			{
				report.Errors.Add("Project-settings fixes were blocked: no affected ProjectSettings files could be identified safely.");
				return false;
			}

			bool clean = true;
			foreach (var path in files)
			{
				try
				{
					var objects = AssetDatabase.LoadAllAssetsAtPath(path);
					if (objects == null || objects.Length == 0)
					{
						clean = false;
						report.Errors.Add("Blocked project-setting fix: " + path + " could not be loaded safely.");
						continue;
					}

					for (int j = 0; j < objects.Length; j++)
					{
						var obj = objects[j];
						if (obj == null || !EditorUtility.IsDirty(obj)) continue;
						clean = false;
						report.Errors.Add("Blocked project-setting fix: " + path
							+ " has unsaved editor changes. Save or revert those changes before applying an optimizer fix that targets this settings file.");
						break;
					}
				}
				catch (Exception e)
				{
					clean = false;
					report.Errors.Add("Blocked project-setting fix: could not inspect " + path + " safely (" + e.Message + ").");
				}
			}
			return clean;
		}

		static bool CaptureProjectSettingsSnapshot(BackupBatch batch, List<AssetWork> works, out List<ManifestEntry> manifest, out string error)
		{
			manifest = new List<ManifestEntry>();
			error = null;
			if (batch == null)
			{
				error = "project-setting fixes require a backup so they can be rolled back atomically";
				return false;
			}

			var files = GetProjectSettingsTargetPaths(works);
			if (files.Count == 0)
			{
				error = "no affected ProjectSettings files could be identified";
				return false;
			}

			foreach (var path in files)
			{
				if (!File.Exists(AssetPathUtility.ToAbsolutePath(path)))
				{
					error = "the affected ProjectSettings file does not exist: " + path;
					return false;
				}
				var captured = new List<ManifestEntry>();
				if (!batch.TryCaptureFile(path, captured, out error))
					return false;
				manifest.AddRange(captured);
			}

			if (!batch.TryAppendToManifest(manifest))
			{
				error = "the ProjectSettings backup manifest could not be written";
				return false;
			}
			return true;
		}

		static void ApplyProjectSettingsWork(AssetWork work, BackupBatch batch, FixReport report)
		{
			if (!PreflightStateUnchanged(work, report))
			{
				work.Failure = work.Path + ": target changed after analysis; the ProjectSettings fix was blocked and the batch will be rolled back.";
				work.Abandoned = work.Requests.Count;
				return;
			}

			var context = new FixContext
			{
				AssetPath = work.Path,
				Importer = null,
				Asset = AssetDatabase.LoadMainAssetAtPath(work.Path),
				BackupEnabled = batch != null,
				IsVerifierProbe = false,
				TryBackupExtra = extra =>
				{
					if (batch == null) return false;
					var captured = new List<ManifestEntry>();
					if (!batch.TryCaptureFile(extra, captured, out _)) return false;
					if (!batch.TryAppendToManifest(captured)) return false;
					work.Manifest.AddRange(captured);
					return true;
				},
				RegisterCreatedAssetCallback = extra =>
				{
					if (batch == null) return false;
					var created = new List<ManifestEntry>();
					if (!batch.TryRecordCreatedAsset(extra, created, out _)) return false;
					work.Manifest.AddRange(created);
					if (!batch.TryAppendToManifest(created)) return false;
					return true;
				},
			};
			work.Context = context;

			for (int i = 0; i < work.Requests.Count; i++)
			{
				var request = work.Requests[i];
				try
				{
					if (request.EffectiveFix == null)
						throw new InvalidOperationException("The fix action is missing.");
					request.EffectiveFix(context);
					work.Applied++;
					AccumulateWorkSavings(work, request);
				}
				catch (Exception e)
				{
					work.Failure = work.Path + " / " + request.Issue.RuleId + ": " + e.Message;
					work.FixThrew = true;
					work.Abandoned = work.Requests.Count - i - 1;
					break;
				}
			}
		}

		static bool VerifyProjectSettingsWorks(List<AssetWork> works, FixReport report)
		{
			bool success = true;
			for (int i = 0; i < works.Count; i++)
			{
				var work = works[i];
				if (work == null || work.Blocked || work.Applied == 0) continue;
				var failed = new List<string>();
				for (int j = 0; j < work.Requests.Count && j < work.Applied; j++)
				{
					var request = work.Requests[j];
					var verify = request.EffectiveVerify;
					if (verify == null) continue;
					bool verified;
					try { verified = verify(work.Context); }
					catch (Exception e)
					{
						verified = false;
						Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Verifier for " + request.Issue.RuleId
							+ " threw on '" + work.Path + "': " + e.Message);
					}
					if (!verified) failed.Add(request.Issue.RuleId);
				}

				if (failed.Count > 0)
				{
					success = false;
					report.VerificationFailed += failed.Count;
					work.Failure = work.Path + ": " + string.Join(", ", failed.ToArray()) + " did not take effect.";
				}
			}
			return success;
		}

		static void FinalizeProjectSettingsResults(List<AssetWork> works, FixReport report)
		{
			for (int i = 0; i < works.Count; i++)
			{
				var work = works[i];
				if (work == null || work.Blocked) continue;
				report.Failed += work.Abandoned;
				if (work.Applied == 0) continue;
				for (int j = 0; j < work.Requests.Count && j < work.Applied; j++)
				{
					var request = work.Requests[j];
					if (request.EffectiveVerify != null) report.AppliedVerified++;
					else report.AppliedUnverified++;
				}
				SetResultOutcomes(work, report, false, false);
				report.EstimatedSavings += work.Savings;
				report.AssetsTouched++;
				report.TouchedPaths.Add(work.Path);
			}
		}

		static bool RollbackProjectSettingsWorks(List<AssetWork> works, BackupBatch batch,
			List<ManifestEntry> projectManifest, FixReport report, string reason)
		{
			if (works == null || works.Count == 0) return true;
			report.Errors.Add("ProjectSettings: " + reason);

			bool restored = false;
			if (batch != null && projectManifest != null && projectManifest.Count > 0)
			{
				var result = batch.Restore(projectManifest);
				report.Errors.AddRange(result.Errors);
				restored = result.Complete;
			}

			for (int i = 0; i < works.Count; i++)
			{
				var work = works[i];
				if (work == null || work.Blocked) continue;
				report.Failed += work.Abandoned;
				if (work.Applied == 0)
				{
					if (work.Failure != null)
					{
						report.Errors.Add(work.Failure);
						if (work.FixThrew) report.Failed++;
						SetResultOutcomes(work, report, true, false, restored);
					}
					else SetResultOutcomes(work, report, false, false, restored);
					continue;
				}

				if (!string.IsNullOrEmpty(work.Failure)) report.Errors.Add(work.Failure);
				else work.Failure = "ProjectSettings batch was rolled back because another project-setting fix could not be verified.";
				if (work.FixThrew) report.Failed++;
				if (restored) report.RolledBack += work.Applied;
				else
				{
					report.RollbackFailed += work.Applied;
					report.Failed += work.Applied;
				}
				SetResultOutcomes(work, report, true, false, restored);
			}

			return restored;
		}

		static void ReloadProjectSettingsFromDisk(FixReport report)
		{
			try
			{
				AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
			}
			catch (Exception e)
			{
				report?.Errors.Add("ProjectSettings were restored on disk, but Unity could not refresh the AssetDatabase: " + e.Message);
			}

			try { AssetDatabase.RefreshSettings(); }
		catch (Exception e) { report?.Errors.Add("ProjectSettings were restored, but Unity could not refresh editor settings: " + e.Message); }
		}

		static bool ValidateCleanProjectSettingsTarget(FixRequest request, FixReport report)
		{
			if (request.Entry == null || string.IsNullOrEmpty(request.Entry.Path))
				return true;

			string path = request.Entry.Path;
			bool isProjectSettingsFile = IsProjectSettingsPath(path);
			try
			{
				if (isProjectSettingsFile)
				{
					var targetPaths = new List<string>(GetProjectSettingsTargetPaths(request));
					if (targetPaths.Count == 0)
					{
						report.Blocked++;
						report.Errors.Add("Blocked fix " + request.ActionId + " on " + path
						+ ": the affected ProjectSettings backing file could not be identified safely.");
						return false;
					}

					for (int i = 0; i < targetPaths.Count; i++)
					{
						var targetPath = targetPaths[i];
						var objects = AssetDatabase.LoadAllAssetsAtPath(targetPath);
						if (objects == null || objects.Length == 0)
						{
							report.Blocked++;
							report.Errors.Add("Blocked fix " + request.ActionId + " on " + path
							+ ": Unity could not load the affected ProjectSettings file " + targetPath + " safely.");
							return false;
						}

						for (int j = 0; j < objects.Length; j++)
						{
							var obj = objects[j];
							if (obj == null || !EditorUtility.IsDirty(obj))
								continue;
							report.Blocked++;
							report.Errors.Add("Blocked fix " + request.ActionId + " on " + path
								+ ": affected ProjectSettings file " + targetPath
								+ " has unsaved editor changes. Save or revert those changes before applying the optimizer fix.");
							return false;
						}
					}
					return true;
				}

				var target = AssetDatabase.LoadMainAssetAtPath(path);
				bool isRenderPipelineAsset = target is RenderPipelineAsset;
				if (!isRenderPipelineAsset)
					return true;

				if (EditorUtility.IsDirty(target))
				{
					report.Blocked++;
					report.Errors.Add("Blocked fix " + request.ActionId + " on " + path
						+ ": the render pipeline asset already has unsaved editor changes. Save or revert those changes before applying an optimizer fix.");
					return false;
				}
			}
			catch (Exception e)
			{
				report.Blocked++;
				report.Errors.Add("Blocked fix " + request.ActionId + " on " + path
					+ ": could not establish a clean target before mutation (" + e.Message + ").");
				return false;
			}

			return true;
		}

		static bool ValidateRequest(FixRequest request, FixExecutionMode mode, FixReport report, bool createBackup)
		{
			var issue = request.Issue;
			if (issue == null)
			{
				report?.Errors.Add("Fix request has no issue.");
				return false;
			}

			if (request.Entry == null || string.IsNullOrEmpty(request.Entry.Path))
			{
				report?.Errors.Add("Fix request for rule " + (issue.RuleId ?? "<unknown>") + " has no asset path.");
				return false;
			}

			if (issue.Stale)
			{
				if (report != null) report.Blocked++;
				report?.Errors.Add("Blocked stale fix " + (issue.RuleId ?? "<unknown>") + " on " + request.Entry.Path
								   + (string.IsNullOrEmpty(issue.StaleReason) ? ". Rescan first." : ": " + issue.StaleReason));
				return false;
			}

			var effectiveKind = request.EffectiveFixKind;
			bool reviewMode = mode == FixExecutionMode.ReviewSingle || mode == FixExecutionMode.ReviewBatch;
			bool verifiedMode = mode == FixExecutionMode.VerifiedSingle || mode == FixExecutionMode.VerifiedBulk;
			var expectedKind = reviewMode ? FixKind.Review : verifiedMode ? FixKind.Verified : FixKind.None;

			if (effectiveKind != expectedKind)
			{
				if (report != null) report.Blocked++;
				report?.Errors.Add("Blocked " + mode + " request for " + request.Entry.Path + ": rule "
								   + (issue.RuleId ?? "<unknown>") + " exposes FixKind " + effectiveKind
								   + ", expected " + expectedKind + ".");
				return false;
			}

			if (!FixContract.CanExecute(mode, request.Issue, request.OptionOverride, out var contractReason))
			{
				if (report != null) report.Blocked++;
				report?.Errors.Add("Blocked " + mode + " fix " + (issue.RuleId ?? "<unknown>") + " on " + request.Entry.Path + ": " + contractReason);
				return false;
			}

			// Verified execution is intentionally stricter than Review: the engine itself enforces
			// the invariant so a malformed analyzer cannot reach mutation merely because RuleAudit was
			// skipped. Every automatic action must have enough confidence, a deterministic default action,
			// and a verifier. High-risk automatic actions must also carry an explicit backup policy.
			if (verifiedMode)
			{
				if (issue.Confidence < RuleCatalog.MinimumForAutomaticFix || issue.Fix == null || issue.Verify == null ||
					(issue.Options != null && issue.Options.Count > 0))
				{
					if (report != null) report.Blocked++;
					report?.Errors.Add("Blocked Verified fix " + (issue.RuleId ?? "<unknown>") + " on " + request.Entry.Path
							+ ": automatic fixes require " + RuleCatalog.MinimumForAutomaticFix
							+ " confidence, one default action, a verifier, and no option-only selection.");
					return false;
				}

				if (request.EffectiveRisk >= FixRisk.High && !request.EffectiveBackupRequired)
				{
					if (report != null) report.Blocked++;
					report?.Errors.Add("Blocked High-risk Verified fix " + (issue.RuleId ?? "<unknown>") + " on " + request.Entry.Path
							+ ": an automatic action at High/Destructive risk must require a backup.");
					return false;
				}
			}

			string requestedActionId = request.ActionId;
			if (string.IsNullOrEmpty(requestedActionId))
			{
				if (report != null) report.Blocked++;
				report?.Errors.Add("Blocked fix for " + request.Entry.Path + ": missing action id.");
				return false;
			}

			if (request.OptionOverride != null)
			{
				// A Review request must carry an option that still belongs to this concrete issue.
				// Do not trust a UI-created FixOption merely because its ActionId happens to match.
				bool found = false;
				if (issue.Options != null)
				{
					for (int i = 0; i < issue.Options.Count; i++)
					{
						var option = issue.Options[i];
						if (option != null && ReferenceEquals(option, request.OptionOverride))
						{
							found = true;
							break;
						}
					}
				}

				if (!found)
				{
					if (report != null) report.Blocked++;
					report?.Errors.Add("Blocked fix for " + request.Entry.Path + ": selected action "
									   + requestedActionId + " is no longer attached to rule " + (issue.RuleId ?? "<unknown>") + ". Rescan first.");
					return false;
				}

				if (request.OptionOverride.Apply == null)
				{
					if (report != null) report.Blocked++;
					report?.Errors.Add("Blocked fix for " + request.Entry.Path + ": selected action "
									   + requestedActionId + " has no executable action.");
					return false;
				}
			}

			var effectiveFix = request.EffectiveFix;
			if (effectiveFix == null)
			{
				if (report != null) report.Blocked++;
				report?.Errors.Add("Blocked fix for " + request.Entry.Path + ": rule "
								   + (issue.RuleId ?? "<unknown>") + " has no executable action.");
				return false;
			}

			// Destructive actions are never allowed to execute without a snapshot.
			if (request.EffectiveBackupRequired && !createBackup)
			{
				if (report != null) report.Blocked++;
				report?.Errors.Add("Blocked fix " + requestedActionId + " on " + request.Entry.Path
								   + ": this action requires a backup.");
				return false;
			}

			return true;
		}

		/// <summary>
		/// Writes the project settings this batch changed, without saving anything else.
		/// <para>
		/// Project settings live outside the asset database, and the obvious way to persist them -
		/// <c>AssetDatabase.SaveAssets()</c> - saves every dirty object in the editor. That includes
		/// a scene or prefab the user had open and modified and had not yet chosen to save. A tool
		/// that changes one project setting must not be the thing that commits somebody's unsaved
		/// work, so the global call is the last resort rather than the first move.
		/// </para>
		/// <para>
		/// The settings objects are addressed directly instead. Unity exposes them through
		/// <c>AssetDatabase.LoadAllAssetsAtPath</c> on their ProjectSettings file, and
		/// <c>SaveAssetIfDirty</c> writes one object without touching the rest.
		/// </para>
		/// </summary>
		static HashSet<int> CaptureDirtyProjectSettingsObjects()
		{
			var dirty = new HashSet<int>();
			var files = GetProjectSettingsFiles();
			for (int i = 0; i < files.Length; i++)
			{
				try
				{
					var objects = AssetDatabase.LoadAllAssetsAtPath(files[i]);
					if (objects == null) continue;
					for (int j = 0; j < objects.Length; j++)
						if (objects[j] != null && EditorUtility.IsDirty(objects[j])) dirty.Add(objects[j].GetInstanceID());
				}
				catch { }
			}
			return dirty;
		}

		static bool PersistProjectSettings(FixReport report, HashSet<int> preExistingDirtyIds, List<AssetWork> works)
		{
			bool allSucceeded = true;
			var files = new List<string>(GetProjectSettingsTargetPaths(works));
			for (int i = 0; i < files.Count; i++)
			{
				try
				{
					var objects = AssetDatabase.LoadAllAssetsAtPath(files[i]);
					if (objects == null) continue;
					for (int j = 0; j < objects.Length; j++)
					{
						var obj = objects[j];
						if (obj == null || !EditorUtility.IsDirty(obj)) continue;
						if (preExistingDirtyIds != null && preExistingDirtyIds.Contains(obj.GetInstanceID())) continue;
						AssetDatabase.SaveAssetIfDirty(obj);
					}
				}
				catch (Exception e)
				{
					allSucceeded = false;
					report?.Errors.Add("Could not persist " + files[i] + ": " + e.Message);
				}
			}

			if (allSucceeded)
			{
				try { AssetDatabase.RefreshSettings(); }
				catch (Exception e)
				{
					allSucceeded = false;
					report?.Errors.Add("Project settings were saved, but Unity could not apply pending editor settings: " + e.Message);
				}
			}

			return allSucceeded;
		}

		/// <summary>
		/// Snapshots one asset and runs its fixes. Nothing is verified and nothing is rolled back
		/// here - the imports these queue have not happened yet, so there is nothing true to read.
		/// </summary>
		static void ApplyToAsset(AssetWork work, BackupBatch batch, FixReport report)
		{
			if (!PreflightStateUnchanged(work, report))
			{ work.Blocked = true; report.Blocked += work.Requests.Count; return; }
			bool backupRequired = false;
			for (int i = 0; i < work.Requests.Count; i++)
				if (work.Requests[i].EffectiveBackupRequired) backupRequired = true;

			if (batch == null && backupRequired)
			{
				report.Blocked += work.Requests.Count;
				report.Errors.Add(work.Path + ": refused. This fix requires a backup and cannot run with backups switched off.");
				work.Blocked = true;
				return;
			}

			if (batch != null)
			{
				if (!batch.TryCapture(work.Path + ".meta", work.Manifest, out string metaError)
					|| !CaptureAssetFileIfEdited(work, batch, out metaError))
				{
					report.Blocked += work.Requests.Count;
					report.Errors.Add(work.Path + ": refused. Could not be backed up ("
									  + (metaError ?? "unknown reason") + "), so nothing was changed.");
					work.Manifest.Clear();
					work.Blocked = true;
					return;
				}

				// The manifest is written before the first fix runs, not after the last one. A batch
				// whose copies exist but whose manifest never reached disk is unrevertable, and the
				// moment to discover that is while the project is still untouched.
				if (!batch.TryAppendToManifest(work.Manifest))
				{
					report.Blocked += work.Requests.Count;
					report.Errors.Add(work.Path + ": refused. The backup was taken but could not be recorded, "
									  + "so it could not be relied on to undo the change.");
					work.Manifest.Clear();
					work.Blocked = true;
					return;
				}
				work.Snapshotted = true;
			}

			var context = new FixContext
			{
				AssetPath = work.Path,
				Importer = AssetImporter.GetAtPath(work.Path),
				Asset = AssetDatabase.LoadMainAssetAtPath(work.Path),
				BackupEnabled = batch != null,
				IsVerifierProbe = false,
				TryBackupExtra = extra =>
				{
					if (batch == null) return false;
					var captured = new List<ManifestEntry>();
					if (!batch.TryCapture(extra, captured, out _)) return false;
					if (!batch.TryAppendToManifest(captured)) return false;
					work.Manifest.AddRange(captured);
					return true;
				},
				RegisterCreatedAssetCallback = extra =>
				{
					if (batch == null) return false;
					var created = new List<ManifestEntry>();
					if (!batch.TryRecordCreatedAsset(extra, created, out _)) return false;
					work.Manifest.AddRange(created);
					if (!batch.TryAppendToManifest(created)) return false;
					return true;
				},
			};
			work.Context = context;

			for (int i = 0; i < work.Requests.Count; i++)
			{
				var request = work.Requests[i];
				try
				{
					request.EffectiveFix(context);
					work.Applied++;
					AccumulateWorkSavings(work, request);

					// Each fix re-reads the importer and main object before it runs. A previous fix
					// on the same asset can have reimported it, which invalidates both, and a fix
					// writing through a stale importer writes into an object Unity has discarded.
					RefreshInPlace(context);
				}
				catch (Exception e)
				{
					work.Failure = work.Path + " / " + request.Issue.RuleId + ": " + e.Message;
					work.FixThrew = true;
					work.Abandoned = work.Requests.Count - i - 1;
					break;
				}
			}

			if (work.Failure != null) return;

			try
			{
				Commit(context);
			}
			catch (Exception e)
			{
				work.Failure = work.Path + ": could not save or reimport after fixing (" + e.Message + ")";
			}
		}

		static void AccumulateWorkSavings(AssetWork work, FixRequest request)
		{
			if (work == null || request.Issue == null) return;
			long added = FixContract.AddCappedSavings(ref work.MemorySaved, ref work.DiskSaved, work.MemoryBase, work.DiskBase, request.Issue, request.OptionOverride);
			if (added <= 0) return;
			work.Savings = work.MemorySaved + work.DiskSaved;
		}

		static bool CaptureAssetFileIfEdited(AssetWork work, BackupBatch batch, out string error)
		{
			error = null;
			var extension = Path.GetExtension(work.Path);
			if (!k_InPlaceEditedExtensions.Contains(extension)) return true;
			return batch.TryCapture(work.Path, work.Manifest, out error);
		}

		/// <summary>
		/// Decides what one asset's fixes finally were, now that their imports have completed:
		/// verified, unverified, a no-op, or rolled back.
		/// </summary>
		static void AddResultRecords(AssetWork work, FixReport report, string outcome)
		{
			if (work == null || report == null) return;
			for (int i = 0; i < work.Requests.Count; i++)
			{
				var request = work.Requests[i];
				report.Results.Add(new FixResultRecord
				{
					AssetPath = work.Path,
					RuleId = request.Issue?.RuleId,
					ActionId = request.ActionId,
					OptionLabel = request.OptionOverride?.Label,
					FixKind = request.EffectiveFixKind,
					Risk = request.EffectiveRisk,
					Destructive = request.EffectiveDestructive,
					BackupRequired = request.EffectiveBackupRequired,
					EstimatedSavings = request.EffectiveSavings,
					Outcome = outcome
				});
			}
		}

		static void Settle(AssetWork work, BackupBatch batch, FixReport report)
		{
			report.Failed += work.Abandoned;
			if (work.Blocked)
			{
				AddResultRecords(work, report, "Blocked");
				return;
			}
			AddResultRecords(work, report, "Pending");

			if (work.Failure != null)
			{
				report.Errors.Add(work.Failure);
				if (work.FixThrew) report.Failed++;

				// Rolled back whether or not any fix completed.
				//
				// A fix that throws has still usually written something first - it mutated the
				// material, then failed on the line after - and that mutation is sitting on the
				// loaded object with nobody having asked for it. Gating the rollback on "at least
				// one fix succeeded" left exactly that case untouched: the throwing fix's half-done
				// edit stayed on the object and got written out by the next save.
				bool restored = Rollback(work, batch, report);

				// The counts describe fixes, not files. A fix that never completed was never
				// applied, so it is a failure rather than a rollback - but the asset it damaged is
				// restored all the same.
				if (work.Applied > 0)
				{
					if (restored) report.RolledBack += work.Applied;
					else
					{
						report.RollbackFailed += work.Applied;
						report.Failed += work.Applied;
					}
				}
				SetResultOutcomes(work, report, true, false, restored);
				return;
			}

			if (work.Applied == 0) return;

			var failedVerifiers = new List<string>();
			var refreshed = Refresh(work.Context);

			for (int i = 0; i < work.Requests.Count && i < work.Applied; i++)
			{
				var issue = work.Requests[i].Issue;
				var verify = work.Requests[i].EffectiveVerify;
				if (verify == null) continue;

				bool verified;
				try
				{
					verified = verify(refreshed);
				}
				catch (Exception e)
				{
					verified = false;
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Verifier for " + issue.RuleId
									 + " threw on '" + work.Path + "': " + e.Message);
				}

				if (!verified) failedVerifiers.Add(issue.RuleId);
			}

			if (failedVerifiers.Count > 0)
			{
				// Verification runs after the editing scope closed, so a negative answer is about the
				// project rather than about import timing. That makes it safe - and necessary - to
				// undo: a change the tool cannot confirm is exactly what it must not leave behind.
				report.VerificationFailed += failedVerifiers.Count;
				report.Errors.Add(work.Path + ": " + string.Join(", ", failedVerifiers.ToArray())
								  + " did not take effect. The asset was rolled back.");

				bool restored = Rollback(work, batch, report);
				if (restored) report.RolledBack += work.Applied;
				else
				{
					report.RollbackFailed += work.Applied;
					report.Failed += work.Applied;
				}
				SetResultOutcomes(work, report, true, false, restored);
				return;
			}

			// Nothing on disk moved. The fix ran, the state it wanted is the state that was already
			// there, and calling that a fix inflates every number in the report.
			bool noOp = work.Snapshotted
						&& !work.Context.DeletedAsset
						&& !work.Context.ChangedProjectSettings
						&& UnchangedSinceBackup(work, batch);

			for (int i = 0; i < work.Requests.Count && i < work.Applied; i++)
			{
				if (noOp) report.NoOp++;
				else if (work.Requests[i].EffectiveVerify != null) report.AppliedVerified++;
				else report.AppliedUnverified++;
			}

			SetResultOutcomes(work, report, false, noOp);
			if (noOp) return;

			report.EstimatedSavings += work.Savings;
			report.AssetsTouched++;
			report.TouchedPaths.Add(work.Path);
		}

		static void SetResultOutcomes(AssetWork work, FixReport report, bool failure, bool noOp = false, bool rollbackSucceeded = true)
		{
			int start = Math.Max(0, report.Results.Count - work.Requests.Count);
			for (int i = 0; i < work.Requests.Count && start + i < report.Results.Count; i++)
			{
				var record = report.Results[start + i];
				if (failure)
				{
					if (i < work.Applied) record.Outcome = rollbackSucceeded ? "RolledBack" : "RollbackFailed";
					else record.Outcome = "Failed";
					if (i < work.Applied && !rollbackSucceeded)
						record.Error = work.Failure ?? "The fix could not be fully rolled back.";
					else if (i >= work.Applied && !string.IsNullOrEmpty(work.Failure))
						record.Error = work.Failure;
				}
				else if (i >= work.Applied)
				{
					record.Outcome = "NotApplied";
				}
				else if (noOp) record.Outcome = "NoOp";
				else if (work.Requests[i].EffectiveVerify != null) record.Outcome = "AppliedVerified";
				else record.Outcome = "AppliedUnverified";
			}
		}

		/// <summary>
		/// True when every file snapshotted for this asset is byte-identical to its backup, which
		/// means the fixes wrote nothing.
		/// </summary>
		static bool UnchangedSinceBackup(AssetWork work, BackupBatch batch)
		{
			if (batch == null || work.Manifest.Count == 0) return false;

			for (int i = 0; i < work.Manifest.Count; i++)
			{
				var entry = work.Manifest[i];
				if (entry.Hash == null) return false;

				var live = AssetPathUtility.ToAbsolutePath(entry.AssetPath);
				if (!File.Exists(live)) return false;

				var hash = FileUtility.Md5OfAbsolute(live);
				if (hash == null || !string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		/// <summary>
		/// Puts one asset back the way it was. Returns false when it could not be fully restored,
		/// which is reported as loudly as the tool can manage - it is the one outcome where the
		/// project is left in a state nobody asked for.
		/// </summary>
		static bool Rollback(AssetWork work, BackupBatch batch, FixReport report)
		{
			// Any in-memory edit is discarded first, and unconditionally. Without this a later save -
			// Unity's own, on focus change, or the project-settings save above - can write the
			// modified object straight back over the file just restored. It is also the only undo
			// available for a fix that mutated the loaded object and then threw before anything
			// reached disk.
			DiscardInMemoryEdits(work.Context);

			if (batch == null || work.Manifest.Count == 0)
			{
				// Nothing was captured, so there is nothing to put back. That is only a problem if
				// something was actually written - a fix that threw before touching disk has already
				// been fully undone by the discard above.
				if (work.Applied == 0) return true;

				report.Errors.Add(work.Path + ": nothing was snapshotted, so it could not be rolled back.");
				report.RollbackFailed++;
				return false;
			}

			var result = batch.Restore(work.Manifest);
			report.Errors.AddRange(result.Errors);

			if (!result.Complete)
			{
				report.RollbackFailed++;
				report.Errors.Add(work.Path + ": ROLLBACK INCOMPLETE - " + result.Restored + " of "
								  + result.Expected + " file(s) restored. Restore this asset from version "
								  + "control, or from " + (batch.Folder ?? "the backup folder") + ".");
				return false;
			}

			// The files are back; now make the editor agree with them. RestoreEntries already
			// reimports what it wrote, but the asset this fix held is reloaded explicitly - it is the
			// one whose in-memory copy was mutated, and it may have been changed without any of its
			// own files being in the manifest.
			ReloadFromDisk(new[] { work.Path }, report);

			report.RolledBackPaths.Add(work.Path);
			return true;
		}

		/// <summary>
		/// Drops the editor's unsaved changes to an asset.
		/// <para>
		/// <c>ClearDirty</c> alone is not a rollback. It clears the flag that would cause the object
		/// to be written out, which is necessary - otherwise a later save puts the abandoned edit
		/// straight back over the restored file - but the loaded object still holds the mutated
		/// values. Anything reading it before the next reimport sees the change that was supposedly
		/// undone. So the caller reloads the asset from the restored file afterwards; this only
		/// stops the in-memory copy from being written out in the meantime.
		/// </para>
		/// </summary>
		static void DiscardInMemoryEdits(FixContext context)
		{
			if (context?.Asset == null) return;
			try { EditorUtility.ClearDirty(context.Asset); }
			catch { /* the object may already be gone, which is the same outcome */ }
		}

		/// <summary>
		/// Forces Unity to re-read these assets from disk, so the loaded object graph matches the
		/// files that were just restored.
		/// <para>
		/// This is the half of a rollback that <c>ClearDirty</c> cannot do. A material, prefab or
		/// controller that a fix mutated keeps those values in memory until something reimports it,
		/// and every subsequent read - the next verifier, the next scan, the inspector - would be
		/// looking at the state the rollback was supposed to remove.
		/// </para>
		/// </summary>
		static void ReloadFromDisk(IEnumerable<string> assetPaths, FixReport report)
		{
			foreach (var assetPath in assetPaths)
			{
				if (string.IsNullOrEmpty(assetPath)) continue;
				if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) continue;

				try
				{
					AssetDatabase.ImportAsset(assetPath,
						ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
				}
				catch (Exception e)
				{
					// The file is correct on disk but the editor is still showing the old object.
					// Said out loud, because "rolled back" would otherwise overstate what happened.
					report?.Errors.Add(assetPath + ": restored on disk, but Unity could not reload it ("
									   + e.Message + "). Reimport it by hand before trusting what the editor shows.");
				}
			}
		}

		/// <summary>
		/// Puts the asset's changes on disk. Reimports are queued by the surrounding editing scope
		/// and run when it closes, which is why nothing is verified until after that point.
		/// </summary>
		static void Commit(FixContext context)
		{
			if (context.DeletedAsset) return;

			if (context.NeedsReimport && context.Importer != null)
				context.Importer.SaveAndReimport();

			if (context.NeedsSave && context.Asset != null)
			{
				EditorUtility.SetDirty(context.Asset);
				AssetDatabase.SaveAssetIfDirty(context.Asset);
			}
		}

		/// <summary>
		/// Re-resolves the importer and main object between fixes on the same asset.
		/// <para>
		/// <b>The transaction contract.</b> Phase one runs inside <c>StartAssetEditing</c>, where
		/// Unity queues imports instead of performing them. A fix may therefore write importer
		/// settings and mark the asset dirty, but it may <em>not</em> assume the asset has been
		/// reimported - <c>SaveAndReimport</c> and <c>ImportAsset</c> only schedule work there.
		/// </para>
		/// <para>
		/// What this call does and does not buy is worth being exact about, because the previous
		/// comment overstated it. Re-fetching gives a live <c>AssetImporter</c> and main object,
		/// which protects against a reference Unity has invalidated. It does <em>not</em> mean the
		/// asset was reimported, so a second fix reading a value that only materialises on import -
		/// a texture's imported dimensions, a mesh's vertex count - will read the pre-import value.
		/// </para>
		/// <para>
		/// So fixes on the same asset must be independent: each reads importer settings and the
		/// serialized object, never another fix's imported result. Verification is where imported
		/// results are checked, and it runs in phase two, after the scope has closed and the imports
		/// have actually happened.
		/// </para>
		/// </summary>
		static void RefreshInPlace(FixContext context)
		{
			if (context.DeletedAsset) return;
			try
			{
				context.Importer = AssetImporter.GetAtPath(context.AssetPath);
				context.Asset = AssetDatabase.LoadMainAssetAtPath(context.AssetPath);
			}
			catch
			{
				// Leave whatever was there; the fix that follows will fail visibly instead.
			}
		}

		/// <summary>
		/// A context pointing at the asset as it is now. The importer and object references captured
		/// before the fix can be stale after a reimport, and a verifier reading those would be
		/// checking the state the fix wrote rather than the state Unity kept.
		/// </summary>
		static FixContext Refresh(FixContext context)
		{
			if (context.DeletedAsset) return context;

			return new FixContext
			{
				AssetPath = context.AssetPath,
				Importer = AssetImporter.GetAtPath(context.AssetPath),
				Asset = AssetDatabase.LoadMainAssetAtPath(context.AssetPath),
				BackupEnabled = context.BackupEnabled,
				TryBackupExtra = context.TryBackupExtra,
				RegisterCreatedAssetCallback = context.RegisterCreatedAssetCallback,
				NeedsReimport = context.NeedsReimport,
				NeedsSave = context.NeedsSave,
				ChangedProjectSettings = context.ChangedProjectSettings,
				DeletedAsset = context.DeletedAsset,
				VerificationData = context.VerificationData,
			};
		}

		// --- revert ----------------------------------------------------------------

		/// <summary>
		/// Restores every file captured by the most recent fix batch that has not been reverted yet.
		/// <para>
		/// The batch is only marked reverted when every file came back and every affected asset
		/// reimported. A partial restore leaves it pending, so it can be retried once whatever
		/// blocked it - a file locked by another program, a read-only checkout - is dealt with.
		/// Marking it done regardless, which is what happened before, threw away the only record of
		/// how those files used to look.
		/// </para>
		/// </summary>
		public static RevertReport RevertLastBatch()
		{
			var report = new RevertReport();

			var folder = LastBackupFolder;
			if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
				return report;

			var manifestPath = Path.Combine(folder, k_ManifestName);
			if (!File.Exists(manifestPath))
				return report;

			List<ManifestEntry> entries;
			try
			{
				entries = new List<ManifestEntry>();
				foreach (var line in File.ReadAllLines(manifestPath))
				{
					var entry = ManifestEntry.Parse(line);
					if (entry != null) entries.Add(entry);
				}
			}
			catch (Exception e)
			{
				report.Errors.Add("Could not read the backup manifest: " + e.Message);
				return report;
			}

			var result = RestoreEntries(folder, entries);
			report.Expected = result.Expected;
			report.Restored = result.Restored;
			report.RestoreFailed = result.RestoreFailed;
			report.IntegrityFailed = result.IntegrityFailed;
			report.ReimportFailed = result.ReimportFailed;
			report.Errors.AddRange(result.Errors);

			if (!report.Complete)
			{
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Revert did not complete: " + report.Restored + " of "
							   + report.Expected + " file(s) restored. The batch stays available so it can be "
							   + "retried.\n" + string.Join("\n", report.Errors.ToArray()));
				return report;
			}

			try
			{
				File.WriteAllText(Path.Combine(folder, k_RevertedMarker), DateTime.Now.ToString("o"));
			}
			catch
			{
				// If the marker cannot be written the batch simply stays revertable, which is the
				// safe direction to fail in.
			}
			return report;
		}

		/// <summary>
		/// Restores a set of captured files and reimports what they belong to.
		/// <para>
		/// Each backup is checked against the size and hash recorded when it was taken, before it is
		/// allowed to overwrite anything. A truncated snapshot restored over a live asset destroys
		/// the very thing the backup existed to protect.
		/// </para>
		/// </summary>
		static RevertReport RestoreEntries(string folder, List<ManifestEntry> entries)
		{
			var report = new RevertReport { Expected = entries?.Count ?? 0 };
			if (entries == null || entries.Count == 0) return report;

			var toReimport = new HashSet<string>(StringComparer.Ordinal);
			var toReloadProjectSettings = new HashSet<string>(StringComparer.Ordinal);

			foreach (var entry in entries)
			{
				if (entry.IsCreated)
				{
					try
					{
						var created = AssetPathUtility.ToAbsolutePath(entry.AssetPath);
						bool removed = true;
						if (AssetDatabase.LoadMainAssetAtPath(entry.AssetPath) != null)
							removed = AssetDatabase.DeleteAsset(entry.AssetPath);
						else if (File.Exists(created))
						{
							File.Delete(created);
							if (File.Exists(created + ".meta")) File.Delete(created + ".meta");
						}
						if (!removed || File.Exists(created) || File.Exists(created + ".meta"))
							throw new IOException("the created asset still exists after deletion");
						report.Restored++;
					}
					catch (Exception e)
					{
						report.RestoreFailed++;
						report.Errors.Add("Could not remove created asset '" + entry.AssetPath + "': " + e.Message);
					}
					continue;
				}

				var backupFile = string.IsNullOrEmpty(entry.BackupName) ? null : Path.Combine(folder, entry.BackupName);
				if (!entry.IsDirectory && !File.Exists(backupFile))
				{
					report.RestoreFailed++;
					report.Errors.Add("Backup copy of '" + entry.AssetPath + "' is missing.");
					continue;
				}

				if (!entry.IsDirectory && !BackupIsIntact(backupFile, entry, out string integrityError))
				{
					report.IntegrityFailed++;
					report.Errors.Add("Backup copy of '" + entry.AssetPath + "' is not intact ("
									  + integrityError + "); it was not restored.");
					continue;
				}

				try
				{
					var destination = AssetPathUtility.ToAbsolutePath(entry.AssetPath);
					var directory = Path.GetDirectoryName(destination);
					if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
						Directory.CreateDirectory(directory);

					if (entry.IsDirectory)
					{
						if (!Directory.Exists(destination)) Directory.CreateDirectory(destination);
					}
					else File.Copy(backupFile, destination, true);
					report.Restored++;

					var assetPath = entry.AssetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
						? entry.AssetPath.Substring(0, entry.AssetPath.Length - 5)
						: entry.AssetPath;
					if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
						toReimport.Add(assetPath);
					else if (assetPath.StartsWith("ProjectSettings/", StringComparison.Ordinal))
						toReloadProjectSettings.Add(assetPath);
				}
				catch (Exception e)
				{
					report.RestoreFailed++;
					report.Errors.Add("Could not restore '" + entry.AssetPath + "': " + e.Message);
				}
			}

			foreach (var assetPath in toReimport)
			{
				try
				{
					AssetDatabase.ImportAsset(assetPath,
						ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
				}
				catch (Exception e)
				{
					report.ReimportFailed++;
					report.Errors.Add("Restored '" + assetPath + "' but could not reimport it: " + e.Message);
				}
			}

			if (toReloadProjectSettings.Count > 0)
			{
				try
				{
					AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
					AssetDatabase.RefreshSettings();
				}
				catch (Exception e)
				{
					report.ReimportFailed++;
					report.Errors.Add("Restored ProjectSettings on disk, but Unity could not refresh the settings state: " + e.Message);
				}
			}

			// Verify live files after restore, not only the backup before copy. A restore is not
			// complete if another process changed/truncated the destination during the copy.
			foreach (var entry in entries)
			{
				if (entry == null || entry.Size < 0 || string.IsNullOrEmpty(entry.Hash)) continue;
				var live = AssetPathUtility.ToAbsolutePath(entry.AssetPath);
				try
				{
					if (!File.Exists(live))
					{
						report.RestoreFailed++;
						report.Errors.Add("Restored '" + entry.AssetPath + "' but the live file is missing afterwards.");
						continue;
					}
					var info = new FileInfo(live);
					var hash = FileUtility.Md5OfAbsolute(live);
					if (info.Length != entry.Size || !string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
					{
						report.RestoreFailed++;
						report.Errors.Add("Restored '" + entry.AssetPath + "' but its live bytes do not match the snapshot.");
					}
				}
				catch (Exception e)
				{
					report.RestoreFailed++;
					report.Errors.Add("Could not verify restored '" + entry.AssetPath + "': " + e.Message);
				}
			}

			return report;
		}

		static bool BackupIsIntact(string backupFile, ManifestEntry entry, out string error)
		{
			error = null;

			// A v1 manifest recorded neither size nor hash. Those batches predate this check and are
			// restored on trust, since refusing them would strand backups people may still need.
			if (entry.Size < 0 && entry.Hash == null) return true;

			try
			{
				var info = new FileInfo(backupFile);
				if (entry.Size >= 0 && info.Length != entry.Size)
				{
					error = "expected " + entry.Size + " bytes, found " + info.Length;
					return false;
				}

				if (entry.Hash != null)
				{
					var hash = FileUtility.Md5OfAbsolute(backupFile);
					if (hash == null || !string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
					{
						error = "contents do not match the recorded hash";
						return false;
					}
				}
				return true;
			}
			catch (Exception e)
			{
				error = e.Message;
				return false;
			}
		}

		// --- backups ---------------------------------------------------------------

		/// <summary>
		/// A folder for this batch's snapshots, or null if one cannot be made.
		/// <para>
		/// Named to the millisecond with a random tail. Two batches a second apart used to be able to
		/// share a folder name, and sharing a folder means sharing a manifest: reverting one would
		/// undo both, and the second batch's snapshot of a file the first had already changed would
		/// be a snapshot of the wrong state.
		/// </para>
		/// </summary>
		static bool TestBackupFolderWritable(string folder)
		{
			if (string.IsNullOrEmpty(folder)) return false;
			string probe = Path.Combine(folder, ".write-test");
			try
			{
				File.WriteAllText(probe, "ProjectOptimizer backup preflight", Encoding.UTF8);
				File.Delete(probe);
				return true;
			}
			catch
			{
				try { if (File.Exists(probe)) File.Delete(probe); } catch { }
				return false;
			}
		}

		static string NewBackupFolder()
		{
			try
			{
				for (int attempt = 0; attempt < 8; attempt++)
				{
					var name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
							   + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
					var folder = Path.Combine(k_BackupRoot, name);
					if (Directory.Exists(folder)) continue;

					Directory.CreateDirectory(folder);
					return folder;
				}

				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not find a free backup folder name under " + k_BackupRoot + ".");
				return null;
			}
			catch (Exception e)
			{
				Debug.LogWarning(
					ProjectOptimizerConstants.LogPrefix + " Could not create a backup folder: " + e.Message
				);
				return null;
			}
		}

		/// <summary>Deletes backup folders older than a week so Library does not grow without bound.</summary>
		public static void PruneOldBackups()
		{
			try
			{
				if (!Directory.Exists(k_BackupRoot))
					return;
				var cutoff = DateTime.Now.AddDays(-7);
				foreach (var directory in Directory.GetDirectories(k_BackupRoot))
				{
					// Never discard a batch the user could still want to roll back.
					if (!File.Exists(Path.Combine(directory, k_RevertedMarker)))
						continue;
					if (Directory.GetCreationTime(directory) < cutoff)
						Directory.Delete(directory, true);
				}
			}
			catch
			{
				// Housekeeping only; never let this interrupt anything.
			}
		}
	}
}

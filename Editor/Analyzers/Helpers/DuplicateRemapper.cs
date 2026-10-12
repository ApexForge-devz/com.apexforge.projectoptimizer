using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Collapses a duplicate asset onto a single kept copy: every reference to the duplicate is
	/// rewritten to point at the keeper, and only then is the duplicate deleted.
	/// <para>
	/// Unity stores a reference as a (fileID, guid) pair. Rewriting one therefore means matching
	/// both halves, not just swapping the guid: two identical source files can produce sub-objects
	/// with different local ids, and pointing a reference at an id the keeper does not contain
	/// would break it just as thoroughly as deleting the file. So the remap only proceeds when
	/// every sub-object of the duplicate has a counterpart in the keeper, and it refuses outright
	/// on projects that are not text-serialized, where the references cannot be edited this way.
	/// </para>
	/// </summary>
	public static class DuplicateRemapper
	{
		public sealed class Plan
		{
			public string DuplicatePath;
			public string KeeperPath;
			public string DuplicateGuid;
			public string KeeperGuid;

			/// <summary>Local file id in the duplicate -> equivalent id in the keeper.</summary>
			public readonly Dictionary<long, long> IdMap = new Dictionary<long, long>();
			public readonly List<string> Referrers = new List<string>();

			public bool Viable;
			public string Reason = string.Empty;
		}

		/// <summary>
		/// Works out whether the duplicate can be collapsed onto the keeper, and what would have to
		/// change. Nothing is modified here.
		/// </summary>
		public static Plan Prepare(string duplicatePath, string keeperPath, ReferenceIndex references)
		{
			var plan = new Plan
			{
				DuplicatePath = duplicatePath,
				KeeperPath = keeperPath,
				DuplicateGuid = AssetDatabase.AssetPathToGUID(duplicatePath),
				KeeperGuid = AssetDatabase.AssetPathToGUID(keeperPath)
			};

			if (EditorSettings.serializationMode != SerializationMode.ForceText)
			{
				plan.Reason = "The project is not set to Force Text serialization, so references cannot be rewritten safely.";
				return plan;
			}

			if (string.IsNullOrEmpty(plan.DuplicateGuid) || string.IsNullOrEmpty(plan.KeeperGuid)
				|| plan.DuplicateGuid == plan.KeeperGuid
				|| string.Equals(duplicatePath, keeperPath, StringComparison.OrdinalIgnoreCase))
			{
				plan.Reason = "The duplicate and the keeper do not resolve to two distinct assets.";
				return plan;
			}

			// Without a reference graph there is no referrer list, and an empty referrer list is
			// indistinguishable from "not asked". The old code treated the second as the first and
			// proceeded to delete. A graph built over a restricted scan scope is the same failure
			// one step further along: it answers a narrower question than the one being asked.
			if (references == null)
			{
				plan.Reason = "No reference graph was available, so the assets pointing at this duplicate are unknown.";
				return plan;
			}

			if (!references.IsProjectWide)
			{
				plan.Reason = "The reference graph covers only this scan's scope, so referrers elsewhere in the "
							  + "project cannot be ruled out. Scan the entire project to enable this fix.";
				return plan;
			}

			// Project-wide is not the same as complete. If some assets could not be read for
			// dependencies, the referrer list for this duplicate may be missing whichever of them
			// pointed at it - and an absent referrer is precisely what this fix acts on.
			if (!references.IsComplete)
			{
				plan.Reason = "The reference graph is incomplete, so some referrers may be unknown. "
							  + references.IncompletenessSummary();
				return plan;
			}

			if (!BuildIdMap(duplicatePath, keeperPath, plan.DuplicateGuid, plan.KeeperGuid, plan.IdMap, out string mapError))
			{
				plan.Reason = mapError;
				return plan;
			}

			foreach (var referrer in references.ReferrersOf(duplicatePath))
			{
				if (IsRewritable(referrer))
				{
					plan.Referrers.Add(referrer);
					continue;
				}

				// Anything under Packages is read-only from here, and anything the remapper does not
				// recognise cannot be edited safely. Either way the reference cannot be moved, so the
				// duplicate it points at cannot be removed.
				plan.Reason = "Referenced by " + referrer + ", which this cannot rewrite"
							  + (referrer.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
								  ? " because it lives inside a package."
								  : " - it is not a text-serialized asset type the remapper understands.");
				return plan;
			}

			// A referrer appearing twice is harmless for read-only analysis but would cause duplicate
			// work in the write phase. Normalize it here so the transaction has one write per file.
			plan.Referrers.Sort(StringComparer.OrdinalIgnoreCase);
			for (int i = plan.Referrers.Count - 1; i > 0; i--)
			{
				if (string.Equals(plan.Referrers[i], plan.Referrers[i - 1], StringComparison.OrdinalIgnoreCase))
					plan.Referrers.RemoveAt(i);
			}

			plan.Viable = true;
			return plan;
		}

		/// <summary>
		/// Pairs the duplicate's sub-objects with the keeper's by name and type. Both files are
		/// byte-identical, so a missing counterpart means the two were imported differently and the
		/// remap must not go ahead.
		/// </summary>
		static bool BuildIdMap(string duplicatePath, string keeperPath, string duplicateGuid, string keeperGuid, Dictionary<long, long> map, out string error)
		{
			error = string.Empty;

			var duplicateMain = AssetDatabase.LoadMainAssetAtPath(duplicatePath);
			var keeperMain = AssetDatabase.LoadMainAssetAtPath(keeperPath);
			if (duplicateMain == null || keeperMain == null)
			{
				error = "One of the two assets could not be loaded.";
				return false;
			}

			if (duplicateMain.GetType() != keeperMain.GetType())
			{
				error = "The two assets do not import to the same kind of object.";
				return false;
			}

			if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(duplicateMain, out _, out long duplicateMainId)
				|| !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(keeperMain, out _, out long keeperMainId))
			{
				error = "Could not resolve the main asset's local file identifier.";
				return false;
			}

			map[duplicateMainId] = keeperMainId;

			// Sub-assets are read with LoadAllAssetRepresentationsAtPath, not LoadAllAssetsAtPath.
			//
			// The latter returns the importer's own bookkeeping objects alongside the real content -
			// an AssetVersion on every asset, and an ImportLog on any asset whose import happened to
			// log something. Those are artifacts of the import, not part of the file, so two
			// byte-identical materials routinely come back with different object counts and the
			// one-to-one check below rejected them. It was refusing provably safe collapses on the
			// basis of whether Unity had written a log entry.
			//
			// Asset representations are exactly the sub-assets a reference can name, which is the
			// set this mapping has to cover.
			var duplicateSubs = AssetDatabase.LoadAllAssetRepresentationsAtPath(duplicatePath);
			var keeperSubs = AssetDatabase.LoadAllAssetRepresentationsAtPath(keeperPath);

			int duplicateCount = CountRealSubAssets(duplicateSubs);
			int keeperCount = CountRealSubAssets(keeperSubs);

			if (duplicateCount == 0 && keeperCount == 0)
				return true;

			if (duplicateCount != keeperCount)
			{
				error = "The duplicate has " + duplicateCount + " sub-asset(s) and the keeper has "
						+ keeperCount + ", so their references cannot be mapped one to one.";
				return false;
			}

			var duplicateStem = Path.GetFileNameWithoutExtension(duplicatePath);
			var keeperStem = Path.GetFileNameWithoutExtension(keeperPath);
			var keeperByKey = new Dictionary<string, long>(StringComparer.Ordinal);

			foreach (var obj in keeperSubs)
			{
				if (!IsRealSubAsset(obj)) continue;
				if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string subGuid, out long id))
				{
					error = "Could not resolve the keeper sub-asset '" + obj.name + "' to a GUID/file identifier.";
					return false;
				}
				if (!string.Equals(subGuid, keeperGuid, StringComparison.OrdinalIgnoreCase))
				{
					error = "The keeper sub-asset '" + obj.name + "' resolved to a different GUID than the keeper file.";
					return false;
				}

				string key = Key(obj, keeperStem);
				if (keeperByKey.ContainsKey(key))
				{
					error = "The keeper contains multiple sub-assets with the same normalized identity '" + key + "'.";
					return false;
				}
				keeperByKey[key] = id;
			}

			var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var obj in duplicateSubs)
			{
				if (!IsRealSubAsset(obj)) continue;
				if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string subGuid, out long id))
				{
					error = "Could not resolve the duplicate sub-asset '" + obj.name + "' to a GUID/file identifier.";
					return false;
				}
				if (!string.Equals(subGuid, duplicateGuid, StringComparison.OrdinalIgnoreCase))
				{
					error = "The duplicate sub-asset '" + obj.name + "' resolved to a different GUID than the duplicate file.";
					return false;
				}

				string key = Key(obj, duplicateStem);
				if (!duplicateKeys.Add(key))
				{
					error = "The duplicate contains multiple sub-assets with the same normalized identity '" + key + "'.";
					return false;
				}

				if (!keeperByKey.TryGetValue(key, out long keeperId))
				{
					error = "The keeper has no sub-asset matching '" + obj.name + "' (" +
							obj.GetType().Name + "), so some references would be left dangling.";
					return false;
				}

				map[id] = keeperId;
			}

			// The map must cover the main asset plus every sub-asset, with nothing left over.
			if (map.Count != duplicateCount + 1)
			{
				error = "Not every sub-asset could be mapped one-to-one (" + map.Count + " mapping(s) for "
						+ (duplicateCount + 1) + " object(s)).";
				return false;
			}

			return true;
		}

		/// <summary>
		/// True for a sub-asset a serialized reference could actually name.
		/// <para>
		/// Excludes anything Unity marks as hidden. A hidden object is not addressable content, and
		/// counting it would reintroduce the mismatch this method exists to avoid.
		/// </para>
		/// </summary>
		static bool IsRealSubAsset(UnityEngine.Object obj)
		{
			if (obj == null) return false;
			if ((obj.hideFlags & HideFlags.HideInHierarchy) != 0) return false;
			if ((obj.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave) return false;
			return true;
		}

		static int CountRealSubAssets(UnityEngine.Object[] objects)
		{
			if (objects == null) return 0;
			int count = 0;
			for (int i = 0; i < objects.Length; i++)
				if (IsRealSubAsset(objects[i])) count++;
			return count;
		}

		/// <summary>
		/// Identity of a sub-object independent of which file it came from: its type plus its name
		/// with the owning file's stem folded out, so "copy_0" and "keeper_0" compare equal.
		/// </summary>
		static string Key(UnityEngine.Object obj, string stem)
		{
			var name = obj.name;
			if (!string.IsNullOrEmpty(stem) && name.StartsWith(stem, StringComparison.Ordinal))
				name = "{stem}" + name.Substring(stem.Length);
			return obj.GetType().FullName + "|" + name;
		}

		/// <summary>Text assets whose references can be rewritten in place.</summary>
		static bool IsRewritable(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return false;
			if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return false;

			var extension = Path.GetExtension(path);
			switch (extension.ToLowerInvariant())
			{
				case ".prefab":
				case ".unity":
				case ".mat":
				case ".asset":
				case ".controller":
				case ".anim":
				case ".playable":
				case ".spriteatlas":
				case ".spriteatlasv2":
				case ".shadergraph":
				case ".vfx":
				case ".overridecontroller":
				case ".mask":
				case ".physicmaterial":
				case ".physicsmaterial2d":
				case ".guiskin":
				case ".fontsettings":
				case ".terrainlayer":
				case ".preset":
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		/// What one migration attempt achieved. Returned as an object rather than a count because
		/// "rewrote three files" is not an answer to "did this work": the migration only succeeded if
		/// the duplicate is gone <em>and</em> every rewritten referrer reimported cleanly.
		/// </summary>
		public sealed class Result
		{
			public bool Succeeded;
			public int RewrittenFiles;
			public bool DuplicateDeleted;
			public string Summary = string.Empty;

			/// <summary>True when the project was left exactly as it was found.</summary>
			public bool RolledBackCleanly;
		}

		/// <summary>
		/// Executes the plan: rewrites every referrer, deletes the duplicate, reimports what changed.
		/// Each file it touches is snapshotted through <paramref name="tryBackup"/> first, and
		/// nothing is written or deleted unless every one of those snapshots succeeded.
		/// <para>
		/// Every step after the first write is a point at which the whole thing has to be undone.
		/// Rewriting the referrers and then failing to delete the duplicate leaves a project that
		/// still works but no longer says what it means; failing to reimport leaves Unity holding
		/// references it has not re-read. Neither is a success, and neither may be reported as one.
		/// </para>
		/// </summary>
		public static Result Apply(Plan plan, Func<string, bool> tryBackup)
		{
			var result = new Result();

			if (plan == null || !plan.Viable)
			{
				result.Summary = plan?.Reason ?? "No plan.";
				result.RolledBackCleanly = true;
				return result;
			}

			if (tryBackup == null)
			{
				result.Summary = "No backup mechanism was supplied, so the migration was refused.";
				result.RolledBackCleanly = true;
				return result;
			}

			var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var details = new StringBuilder();

			foreach (var referrer in plan.Referrers)
			{
				try
				{
					string full = AssetPathUtility.ToAbsolutePath(referrer);
					if (!File.Exists(full)) continue;

					string text = File.ReadAllText(full);
					string rewritten = Rewrite(text, plan, out int replacements);
					if (replacements == 0) continue;

					originals[referrer] = text;
					updated[referrer] = rewritten;
					details.AppendLine("  " + referrer + " (" + replacements + " reference(s))");
				}
				catch (Exception e)
				{
					result.Summary = "Cannot prepare reference rewrite for " + referrer + ": " + e.Message;
					result.RolledBackCleanly = true;
					return result;
				}
			}

			// Everything is snapshotted before anything is written. Interleaving the two would leave
			// a batch that failed halfway with some files changed and no copy of how they started.
			var toBackup = new List<string>(updated.Keys) { plan.DuplicatePath };
			foreach (var file in toBackup)
			{
				if (tryBackup(file)) continue;
				result.Summary = "Could not back up " + file + ", so nothing was changed.";
				result.RolledBackCleanly = true;
				return result;
			}

			int rewrittenFiles = 0;
			try
			{
				foreach (var pair in updated)
				{
					WriteTextAtomically(AssetPathUtility.ToAbsolutePath(pair.Key), pair.Value);
					rewrittenFiles++;
				}

				foreach (var pair in updated)
				{
					string disk = File.ReadAllText(AssetPathUtility.ToAbsolutePath(pair.Key));
					if (!string.Equals(disk, pair.Value, StringComparison.Ordinal))
						throw new IOException("Post-write verification failed for " + pair.Key + ".");

					// The rewrite is only complete if nothing still points at the guid that is about
					// to stop existing. A surviving mention means the regex matched some of the
					// reference structures in this file and not others, which would leave a broken
					// reference behind exactly where the fix claimed to have repaired one.
					if (disk.IndexOf(plan.DuplicateGuid, StringComparison.Ordinal) >= 0)
						throw new IOException(
							pair.Key + " still contains the duplicate's guid after rewriting, so the "
							+ "migration is incomplete.");
				}

				if (!AssetDatabase.DeleteAsset(plan.DuplicatePath))
					throw new IOException("Could not delete " + plan.DuplicatePath + ".");

				result.DuplicateDeleted = true;

				// Do not ask AssetDatabase.LoadMainAssetAtPath here. DeleteAsset removes the file
				// immediately, but while StartAssetEditing is still active Unity can legitimately keep the
				// old object alive in its in-memory database until the editing scope is closed. Treating that
				// stale object as proof that deletion failed makes a successful delete roll back every time.
				// The on-disk file is the authoritative state in this phase; the normal post-transaction
				// verifier checks the AssetDatabase again after queued imports have completed.
				string duplicateFullPath = AssetPathUtility.ToAbsolutePath(plan.DuplicatePath);
				string duplicateMetaPath = duplicateFullPath + ".meta";
				if (File.Exists(duplicateFullPath) || File.Exists(duplicateMetaPath))
				{
					throw new IOException("The duplicate file or its .meta file still exists after Unity reported deletion.");
				}

				// The reimport is part of the migration, not housekeeping after it. Until Unity has
				// re-read these files it is still resolving the old references, so a failure here
				// means the project on disk and the project in the editor disagree.
				var failedImports = new List<string>();
				foreach (var referrer in updated.Keys)
				{
					try
					{
						AssetDatabase.ImportAsset(referrer,
							ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
					}
					catch (Exception e)
					{
						failedImports.Add(referrer + " (" + e.Message + ")");
					}
				}

				if (failedImports.Count > 0)
					throw new IOException("Could not reimport " + string.Join(", ", failedImports.ToArray()) + ".");

				result.Succeeded = true;
				result.RewrittenFiles = rewrittenFiles;
				result.Summary = "Repointed " + rewrittenFiles + " file(s) at " + plan.KeeperPath +
								 " and deleted the duplicate."
								 + (details.Length > 0 ? "\n" + details : string.Empty);
				return result;
			}
			catch (Exception e)
			{
				bool restored = Restore(originals);
				bool duplicateStillMissing = result.DuplicateDeleted;
				result.DuplicateDeleted = false;

				// DuplicateRemapper does not own the fix engine's backup/restore of the deleted asset,
				// so "clean" here can only mean every referrer this class rewrote was restored. The
				// surrounding FixEngine remains responsible for restoring the deleted duplicate.
				result.RolledBackCleanly = restored;

				if (duplicateStillMissing)
				{
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " " + plan.DuplicatePath
								   + " was deleted before migration failure. Its restoration is owned by the "
								   + "fix batch backup/rollback.");
				}

				result.Summary = "Duplicate collapse aborted: " + e.Message
								 + (result.RolledBackCleanly
									 ? " Every written file was put back."
									 : " WARNING: the rollback did not fully succeed - see the errors above.");
				return result;
			}
		}

		/// <summary>
		/// Puts back every referrer this migration rewrote, and reports whether it managed all of
		/// them. The duplicate asset itself is restored by the surrounding fix batch, which holds the
		/// snapshot of it; this owns only the text it wrote.
		/// </summary>
		static bool Restore(Dictionary<string, string> originals)
		{
			bool clean = true;

			foreach (var pair in originals)
			{
				try
				{
					WriteTextAtomically(AssetPathUtility.ToAbsolutePath(pair.Key), pair.Value);
					AssetDatabase.ImportAsset(pair.Key,
						ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
				}
				catch (Exception rollbackError)
				{
					clean = false;
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Rollback failed for " + pair.Key + ": " +
								   rollbackError.Message);
				}
			}

			return clean;
		}

		static void WriteTextAtomically(string fullPath, string content)
		{
			string temp = fullPath + ".projectoptimizer.tmp";
			try
			{
				if (File.Exists(temp))
					File.Delete(temp);
				File.WriteAllText(temp, content);
			}
			catch
			{
				if (File.Exists(temp))
				{
					try { File.Delete(temp); } catch { }
				}
				throw;
			}

			try
			{
				if (File.Exists(fullPath))
					File.Replace(temp, fullPath, null);
				else
					File.Move(temp, fullPath);
			}
			finally
			{
				if (File.Exists(temp))
					File.Delete(temp);
			}
		}

		static string Rewrite(string text, Plan plan, out int replacements)
		{
			replacements = 0;
			if (string.IsNullOrEmpty(text) || plan == null || plan.IdMap.Count == 0)
				return text;

			// Match complete serialized Unity object-reference tokens, not arbitrary occurrences of the GUID.
			string guid = Regex.Escape(plan.DuplicateGuid);
			int replacementCount = 0;

			var fileIdFirst = new Regex(
				@"\{(?<body>[^{}]*?)fileID:\s*(?<id>-?\d+)(?<middle>[^{}]*?)guid:\s*" +
				guid + @"(?<tail>[^{}]*?)\}",
				RegexOptions.CultureInvariant);

			var guidFirst = new Regex(
				@"\{(?<body>[^{}]*?)guid:\s*" +
				guid + @"(?<middle>[^{}]*?)fileID:\s*(?<id>-?\d+)(?<tail>[^{}]*?)\}",
				RegexOptions.CultureInvariant);

			string ReplaceMatch(Match match)
			{
				if (!long.TryParse(match.Groups["id"].Value, out long duplicateId))
					return match.Value;

				if (!plan.IdMap.TryGetValue(duplicateId, out long keeperId))
					return match.Value;

				string value = match.Value;
				int idStart = match.Groups["id"].Index - match.Index;
				value = value.Remove(idStart, match.Groups["id"].Length)
							 .Insert(idStart, keeperId.ToString());

				if (value.IndexOf("fileID: " + keeperId, StringComparison.Ordinal) < 0
					&& value.IndexOf("fileID:" + keeperId, StringComparison.Ordinal) < 0)
					return match.Value;

				int guidStart = value.IndexOf(plan.DuplicateGuid, StringComparison.Ordinal);
				if (guidStart < 0)
					return match.Value;

				value = value.Remove(guidStart, plan.DuplicateGuid.Length)
							 .Insert(guidStart, plan.KeeperGuid);
				replacementCount++;
				return value;
			}

			string rewritten = fileIdFirst.Replace(text, ReplaceMatch);
			rewritten = guidFirst.Replace(rewritten, ReplaceMatch);
			replacements = replacementCount;
			return rewritten;
		}
	}
}

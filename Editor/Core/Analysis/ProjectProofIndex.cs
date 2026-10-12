using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// One project-wide source-evidence index shared by rules that need complete project coverage.
	/// The index is built once per scan, but the source walk itself runs off the editor thread so a
	/// project-wide proof scan cannot monopolize Unity's update loop.
	/// </summary>
	public sealed class ProjectProofIndex
	{
		static readonly Regex k_GuidRegex = new Regex(@"\bguid:\s*([0-9a-fA-F]{32})\b", RegexOptions.Compiled);
		static readonly Regex k_TokenRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
		static readonly string[] k_ProofPatterns =
		{
			"AndroidJavaObject", "Type.GetType", "Assembly.Load", "Resources.FindObjectsOfTypeAll",
			"List<Collision>", "Dictionary<Collision", "Collision[]", "Collision m_",
			"_CameraOpaqueTexture", "RequireOpaqueTexture", "_CameraDepthTexture", "RequireDepthTexture",
			"ScreenSpaceAmbientOcclusion", "SoftParticles", "AppDomain", "MonoBehaviour[]",
			"Physics.SyncTransforms", "BuildPipeline.BuildAssetBundles", "BuildAssetBundles(",
			"Input.acceleration", "Input.gyro",
			"Accelerometer.current", "Gyroscope.current", "GravitySensor.current", "AttitudeSensor.current",
			"LinearAccelerationSensor.current", "<Accelerometer>/acceleration", "<Gyroscope>/angularVelocity",
			"<GravitySensor>/gravity", "<AttitudeSensor>/attitude", "<LinearAccelerationSensor>/acceleration", "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
			"Rigidbody.", "Collider.", "attachedRigidbody", "GetComponent<Rigidbody>", "GetComponent<Collider>",
			"Physics.Raycast", "Physics.Overlap", "Physics.Linecast", "Physics.SphereCast", "Physics.CapsuleCast", "Physics.BoxCast",
			"Rigidbody2D.", "Collider2D.", "GetComponent<Rigidbody2D>", "GetComponent<Collider2D>", "Physics2D.",
			".transform.position =", ".transform.rotation =", ".transform.localPosition =", ".transform.localRotation =",
			"SetPositionAndRotation(", "SetLocalPositionAndRotation("
		};

		sealed class BuildData
		{
			public readonly HashSet<string> Tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly Dictionary<string, int> TokenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			public readonly Dictionary<string, HashSet<string>> FileTokens = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
			public readonly Dictionary<string, Dictionary<string, int>> FileTokenCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<string> PatternsPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<string> MaterialTextureGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<string> NonUiTextureGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<string> UiTextureGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<string> KnownGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly List<string> CandidatePaths = new List<string>();
			public int CandidateFiles;
			public int ProcessedFiles;
			public int SkippedFiles;
			public int UnreadableFiles;
			public int FailedFiles;
			public int EnumerationFailures;
			public int RequestedRoots;
			public int AcceptedRoots;
		}

		readonly HashSet<string> m_Tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, int> m_TokenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, HashSet<string>> m_FileTokens = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, Dictionary<string, int>> m_FileTokenCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> m_PatternsPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> m_MaterialTextureGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> m_NonUiTextureGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> m_UiTextureGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> m_KnownGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		Task<BuildData> m_BuildTask;
		bool m_Built;
		bool m_BuildFailed;
		int m_FilesProcessed;
		int m_CandidateFiles;
		int m_FilesSkipped;
		int m_FilesUnreadable;
		int m_FilesFailed;
		int m_EnumerationFailures;
		int m_RequestedRoots;
		int m_AcceptedRoots;
		string m_CoverageSignature;
		string m_BuildError;

		public bool IsBuilt => m_Built;
		public bool BuildFailed => m_BuildFailed;
		public bool IsBuildInProgress => m_BuildTask != null && !m_BuildTask.IsCompleted;
		public int FilesProcessed => Volatile.Read(ref m_FilesProcessed);
		public int CandidateFiles => Volatile.Read(ref m_CandidateFiles);
		public int FilesSkipped => Volatile.Read(ref m_FilesSkipped);
		public int FilesUnreadable => Volatile.Read(ref m_FilesUnreadable);
		public int FilesFailed => Volatile.Read(ref m_FilesFailed);
		public int EnumerationFailures => Volatile.Read(ref m_EnumerationFailures);
		public int RequestedRoots => Volatile.Read(ref m_RequestedRoots);
		public int AcceptedRoots => Volatile.Read(ref m_AcceptedRoots);
		public bool CoverageComplete => IsBuilt && !BuildFailed && RequestedRoots > 0 && RequestedRoots == AcceptedRoots && CandidateFiles == FilesProcessed && FilesSkipped == 0 && FilesUnreadable == 0 && FilesFailed == 0 && EnumerationFailures == 0;
		public string CoverageSignature => m_CoverageSignature;
		public string CoverageSummary => "roots=" + AcceptedRoots + "/" + RequestedRoots + " candidates=" + CandidateFiles + " processed=" + FilesProcessed + " skipped=" + FilesSkipped + " unreadable=" + FilesUnreadable + " failed=" + FilesFailed + " enumerationFailures=" + EnumerationFailures;
		public string BuildError => m_BuildError;

		public Task StartBuildAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken = default)
		{
			if (m_Built) return Task.CompletedTask;
			if (m_BuildTask != null) return m_BuildTask;

			var copiedRoots = new List<string>();
			int requestedRoots = roots != null ? roots.Count : 0;
			if (roots != null)
			{
				for (int i = 0; i < roots.Count; i++)
				{
					string root = roots[i];
					if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
					root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					bool duplicate = false;
					for (int r = 0; r < copiedRoots.Count; r++)
						if (string.Equals(copiedRoots[r], root, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
					if (!duplicate) copiedRoots.Add(root);
				}
			}

			Interlocked.Exchange(ref m_FilesProcessed, 0);
			Interlocked.Exchange(ref m_CandidateFiles, 0);
			Interlocked.Exchange(ref m_FilesSkipped, 0);
			Interlocked.Exchange(ref m_FilesUnreadable, 0);
			Interlocked.Exchange(ref m_FilesFailed, 0);
			Interlocked.Exchange(ref m_EnumerationFailures, 0);
			Interlocked.Exchange(ref m_RequestedRoots, requestedRoots);
			Interlocked.Exchange(ref m_AcceptedRoots, copiedRoots.Count);
			m_CoverageSignature = null;
			m_BuildFailed = false;
			m_BuildError = null;
			m_BuildTask = Task.Run(() => BuildDataFromRoots(copiedRoots, cancellationToken), cancellationToken);
			return m_BuildTask;
		}

		public bool CompleteBuild()
		{
			if (m_Built) return true;
			if (m_BuildTask == null)
			{
				m_BuildFailed = true;
				m_BuildError = "Source proof build was never started.";
				return false;
			}
			if (!m_BuildTask.IsCompleted) return false;

			try
			{
				var data = m_BuildTask.GetAwaiter().GetResult();
				Apply(data);
				m_Built = true;
				return true;
			}
			catch (OperationCanceledException e)
			{
				m_BuildFailed = true;
				m_BuildError = e.Message;
				return false;
			}
			catch (Exception e)
			{
				m_BuildFailed = true;
				m_BuildError = e.GetBaseException().Message;
				return false;
			}
		}

		static BuildData BuildDataFromRoots(IReadOnlyList<string> roots, CancellationToken cancellationToken)
		{
			var files = new List<string>();
			var data = new BuildData { RequestedRoots = roots != null ? roots.Count : 0, AcceptedRoots = roots != null ? roots.Count : 0 };
			for (int r = 0; r < data.RequestedRoots; r++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!Directory.Exists(roots[r])) { data.AcceptedRoots--; continue; }
				try
				{
					foreach (var file in Directory.EnumerateFiles(roots[r], "*.*", SearchOption.AllDirectories))
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (!HasProofExtension(file)) continue;
						if (IsIntentionallyExcludedProofFile(file)) { data.SkippedFiles++; continue; }
						files.Add(file);
					}
				}
				catch (UnauthorizedAccessException) { data.EnumerationFailures++; }
				catch (Exception) { data.EnumerationFailures++; }
			}

			files.Sort(StringComparer.OrdinalIgnoreCase);
			data.CandidateFiles = files.Count + data.SkippedFiles;
			data.CandidatePaths.AddRange(files);
			for (int i = 0; i < files.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string file = files[i].Replace('\\', '/');
				string ext = Path.GetExtension(file);
				bool material = ext.Equals(".mat", StringComparison.OrdinalIgnoreCase);
				bool prefabOrScene = ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase) || ext.Equals(".unity", StringComparison.OrdinalIgnoreCase);

				try
				{
					string text;
					try { text = File.ReadAllText(file); }
					catch { data.UnreadableFiles++; continue; }

					var fileTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					var fileTokenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
					foreach (Match match in k_TokenRegex.Matches(text))
					{
						string token = match.Value;
						data.Tokens.Add(token);
						fileTokens.Add(token);
						fileTokenCounts.TryGetValue(token, out int fileCount);
						fileTokenCounts[token] = fileCount + 1;
						data.TokenCounts.TryGetValue(token, out int count);
						data.TokenCounts[token] = count + 1;
					}
					data.FileTokens[file] = fileTokens;
					data.FileTokenCounts[file] = fileTokenCounts;

					string proofText = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? MaskCSharpNonCode(text) : text;
					for (int pi = 0; pi < k_ProofPatterns.Length; pi++)
						if (ContainsSourcePattern(proofText, k_ProofPatterns[pi], ext)) data.PatternsPresent.Add(k_ProofPatterns[pi]);

					data.ProcessedFiles++;
					var guids = k_GuidRegex.Matches(text);
					if (guids.Count > 0)
					{
						bool uiMarker = prefabOrScene && LooksLikeUiConsumer(text);
						foreach (Match match in guids)
						{
							string guid = match.Groups[1].Value;
							data.KnownGuids.Add(guid);
							if (material) data.MaterialTextureGuids.Add(guid);
							else if (prefabOrScene)
							{
								if (uiMarker) data.UiTextureGuids.Add(guid); else data.NonUiTextureGuids.Add(guid);
							}
						}
					}
				}
				catch
				{
					data.FailedFiles++;
				}
			}
			return data;
		}

		static bool HasProofExtension(string file)
		{
			string ext = Path.GetExtension(file);
			return ext.Equals(".mat", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".unity", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".shader", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".shadergraph", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".hlsl", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".compute", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
				   || ext.Equals(".inputactions", StringComparison.OrdinalIgnoreCase);
		}

		static bool IsIntentionallyExcludedProofFile(string file)
		{
			string normalized = file.Replace('\\', '/');
			return normalized.IndexOf("/" + ProjectOptimizerConstants.PackageId + "/", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsProofFile(string file) => HasProofExtension(file) && !IsIntentionallyExcludedProofFile(file);

		void Apply(BuildData data)
		{
			m_Tokens.Clear(); m_TokenCounts.Clear(); m_FileTokens.Clear(); m_FileTokenCounts.Clear();
			m_PatternsPresent.Clear(); m_MaterialTextureGuids.Clear(); m_NonUiTextureGuids.Clear(); m_UiTextureGuids.Clear(); m_KnownGuids.Clear();
			foreach (var value in data.Tokens) m_Tokens.Add(value);
			foreach (var pair in data.TokenCounts) m_TokenCounts[pair.Key] = pair.Value;
			foreach (var pair in data.FileTokens) m_FileTokens[pair.Key] = pair.Value;
			foreach (var pair in data.FileTokenCounts) m_FileTokenCounts[pair.Key] = pair.Value;
			foreach (var value in data.PatternsPresent) m_PatternsPresent.Add(value);
			foreach (var value in data.MaterialTextureGuids) m_MaterialTextureGuids.Add(value);
			foreach (var value in data.NonUiTextureGuids) m_NonUiTextureGuids.Add(value);
			foreach (var value in data.UiTextureGuids) m_UiTextureGuids.Add(value);
			foreach (var value in data.KnownGuids) m_KnownGuids.Add(value);
			Interlocked.Exchange(ref m_FilesProcessed, data.ProcessedFiles);
			Interlocked.Exchange(ref m_CandidateFiles, data.CandidateFiles);
			Interlocked.Exchange(ref m_FilesSkipped, data.SkippedFiles);
			Interlocked.Exchange(ref m_FilesUnreadable, data.UnreadableFiles);
			Interlocked.Exchange(ref m_FilesFailed, data.FailedFiles);
			Interlocked.Exchange(ref m_EnumerationFailures, data.EnumerationFailures);
			Interlocked.Exchange(ref m_RequestedRoots, data.RequestedRoots);
			Interlocked.Exchange(ref m_AcceptedRoots, data.AcceptedRoots);
			m_CoverageSignature = CoverageCompleteCandidate(data) ? EvidenceContract.ComputeProofScopeIdentity() : string.Empty;
		}

		static bool CoverageCompleteCandidate(BuildData data)
		{
			return data != null
				&& data.RequestedRoots > 0
				&& data.RequestedRoots == data.AcceptedRoots
				&& data.CandidateFiles == data.ProcessedFiles
				&& data.SkippedFiles == 0
				&& data.UnreadableFiles == 0
				&& data.FailedFiles == 0
				&& data.EnumerationFailures == 0;
		}

		static string ComputeCoverageSignature(IReadOnlyList<string> paths)
		{
			try
			{
				if (paths == null || paths.Count == 0) return string.Empty;
				using (var sha = System.Security.Cryptography.SHA256.Create())
				using (var stream = new MemoryStream())
				{
					var sorted = new List<string>(paths);
					sorted.Sort(StringComparer.OrdinalIgnoreCase);
					for (int i = 0; i < sorted.Count; i++)
					{
						string path = sorted[i].Replace('\\', '/');
						byte[] prefix = Encoding.UTF8.GetBytes(path + "|");
						stream.Write(prefix, 0, prefix.Length);
						try
						{
							if (!File.Exists(path))
							{
								byte[] missing = Encoding.UTF8.GetBytes("missing\n");
								stream.Write(missing, 0, missing.Length);
								continue;
							}
							using (var file = File.OpenRead(path))
							{
								var contentHash = sha.ComputeHash(file);
								byte[] hashText = Encoding.UTF8.GetBytes(BitConverter.ToString(contentHash).Replace("-", string.Empty) + "\n");
								stream.Write(hashText, 0, hashText.Length);
							}
						}
						catch
						{
							byte[] unavailable = Encoding.UTF8.GetBytes("unavailable\n");
								stream.Write(unavailable, 0, unavailable.Length);
						}
					}
					stream.Position = 0;
					using (var finalSha = System.Security.Cryptography.SHA256.Create())
						return BitConverter.ToString(finalSha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
				}
			}
			catch { return string.Empty; }
		}

		public bool ContainsToken(string token)
		{
			if (string.IsNullOrEmpty(token)) return false;
			return m_Tokens.Contains(token);
		}

		public bool ContainsTokenOutsideFile(string token, string assetPath)
		{
			if (string.IsNullOrEmpty(token)) return false;
			if (!m_TokenCounts.TryGetValue(token, out var total) || total == 0) return false;

			string absolute = assetPath;
			if (!string.IsNullOrEmpty(assetPath) && !Path.IsPathRooted(assetPath))
			{
				string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
				absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
			}

			string key = string.IsNullOrEmpty(absolute) ? null : absolute.Replace('\\', '/');
			if (key == null || !m_FileTokenCounts.TryGetValue(key, out var ownCounts)) return true;
			ownCounts.TryGetValue(token, out var ownCount);
			return total > ownCount;
		}

		public bool ContainsPattern(string pattern)
		{
			if (string.IsNullOrEmpty(pattern)) return false;
			return m_PatternsPresent.Contains(pattern);
		}

		public bool TextureHasMaterialConsumer(string guid)
		{
			if (string.IsNullOrEmpty(guid)) return true;
			return m_MaterialTextureGuids.Contains(guid);
		}

		public bool TextureIsFixedScaleUiOnly(string guid)
		{
			if (string.IsNullOrEmpty(guid)) return false;
			if (m_MaterialTextureGuids.Contains(guid) || m_NonUiTextureGuids.Contains(guid)) return false;
			return m_UiTextureGuids.Contains(guid);
		}

		public bool ContainsSourcePattern(string pattern)
		{
			return ContainsPattern(pattern);
		}

		/// <summary>Source-token presence is diagnostic evidence only; this index does not claim Roslyn symbol semantics.</summary>
		public bool HasDeterministicSemanticProof => false;

		static bool ContainsSourcePattern(string source, string pattern, string extension)
		{
			if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(pattern)) return false;
			if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
				return source.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

			string normalizedSource = source.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
			string normalizedPattern = pattern.Replace(" ", string.Empty).Replace("\t", string.Empty);
			if (normalizedSource.Replace(" ", string.Empty).IndexOf(normalizedPattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return source.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static string MaskCSharpNonCode(string text)
		{
			if (string.IsNullOrEmpty(text)) return text;
			var chars = text.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
				{
					chars[i++] = ' ';
					while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
					continue;
				}
				if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
				{
					chars[i++] = ' ';
					chars[i++] = ' ';
					while (i + 1 < chars.Length && !(chars[i] == '*' && chars[i + 1] == '/')) { if (chars[i] != '\n' && chars[i] != '\r') chars[i] = ' '; i++; }
					if (i < chars.Length) chars[i] = ' ';
					if (i + 1 < chars.Length) chars[++i] = ' ';
					continue;
				}
				if (chars[i] == '"' || chars[i] == '\'')
				{
					char quote = chars[i];
					chars[i] = ' ';
					for (i++; i < chars.Length; i++)
					{
						if (chars[i] == '\n' || chars[i] == '\r') break;
						if (chars[i] == '\\') { chars[i] = ' '; if (i + 1 < chars.Length) { i++; if (chars[i] != '\n' && chars[i] != '\r') chars[i] = ' '; } continue; }
						bool end = chars[i] == quote; chars[i] = ' '; if (end) break;
					}
				}
			}
			return new string(chars);
		}

		static bool LooksLikeUiConsumer(string text)
		{
			return text.IndexOf("m_Sprite:", StringComparison.OrdinalIgnoreCase) >= 0
				   || text.IndexOf("m_Sprites:", StringComparison.OrdinalIgnoreCase) >= 0
				   || text.IndexOf("RawImage", StringComparison.OrdinalIgnoreCase) >= 0
				   || text.IndexOf("Image:", StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}

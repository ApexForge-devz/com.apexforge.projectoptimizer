using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// The hardware class the recommendations target. Enum values are serialized, so existing
	/// entries keep their numbers and new profiles are appended.
	/// </summary>
	/// <summary>Controls how aggressively the editor scan consumes its main-thread time slice.</summary>
	public enum ScanPacingMode
	{
		Interactive = 0,
		Balanced = 1,
		Throughput = 2,
		Background = 3
	}

	public enum OptimizerPlatform
	{
		Desktop = 0,
		Android = 1,
		iOS = 2,
		WebGL = 3,
		StandaloneVR = 4,
		PCVR = 5,
		Console = 6,
	}

	/// <summary>
	/// What a target profile implies: which importer override slot to use, which texture format
	/// family fits the GPU, and the budgets that are reasonable for that class of hardware.
	/// </summary>
	public sealed class PlatformProfile
	{
		public OptimizerPlatform Platform;
		public string DisplayName;
		public string Description;

		/// <summary>Key used by the importer platform-override APIs.</summary>
		public string OverrideKey;

		/// <summary>True for GPUs where ASTC is the right compression family.</summary>
		public bool IsMobileClass;

		/// <summary>True where the scene is rendered once per eye and the frame budget is halved.</summary>
		public bool IsVR;

		public int MaxTextureSize;
		public int MaxUiTextureSize;
		public int MeshVertexWarn;
		public int MeshVertexCritical;
		public float AudioStreamingSeconds;
		public int AudioSfxSampleRate;
		public int AudioMaxQuality;
		public int RealtimeLightWarn;
		public float ShadowDistanceWarn;

		static readonly PlatformProfile[] k_All =
		{
			new PlatformProfile
			{
				Platform = OptimizerPlatform.Desktop,
				DisplayName = "Desktop (Windows / macOS / Linux)",
				Description =
					"Standalone player on desktop GPUs. BC/DXT compression, generous budgets.",
				OverrideKey = "Standalone",
				IsMobileClass = false,
				IsVR = false,
				MaxTextureSize = 2048,
				MaxUiTextureSize = 1024,
				MeshVertexWarn = 30000,
				MeshVertexCritical = 100000,
				AudioStreamingSeconds = 15f,
				AudioSfxSampleRate = 22050,
				AudioMaxQuality = 70,
				RealtimeLightWarn = 6,
				ShadowDistanceWarn = 150f,
			},
			new PlatformProfile
			{
				Platform = OptimizerPlatform.Android,
				DisplayName = "Android (phone / tablet)",
				Description = "Mobile-oriented budgets: ASTC-capable texture targets, tighter memory limits, and platform-appropriate stripping. IL2CPP is the normal release path when supported by the project.",
				OverrideKey = "Android",
				IsMobileClass = true,
				IsVR = false,
				MaxTextureSize = 1024,
				MaxUiTextureSize = 512,
				MeshVertexWarn = 15000,
				MeshVertexCritical = 50000,
				AudioStreamingSeconds = 10f,
				AudioSfxSampleRate = 22050,
				AudioMaxQuality = 60,
				RealtimeLightWarn = 2,
				ShadowDistanceWarn = 50f,
			},
			new PlatformProfile
			{
				Platform = OptimizerPlatform.iOS,
				DisplayName = "iOS (iPhone / iPad)",
				Description = "Mobile-oriented budgets: ASTC-capable texture targets, tighter memory limits, and the platform-supported AOT backend for release builds.",
				OverrideKey = "iPhone",
				IsMobileClass = true,
				IsVR = false,
				MaxTextureSize = 1024,
				MaxUiTextureSize = 512,
				MeshVertexWarn = 15000,
				MeshVertexCritical = 50000,
				AudioStreamingSeconds = 10f,
				AudioSfxSampleRate = 22050,
				AudioMaxQuality = 60,
				RealtimeLightWarn = 2,
				ShadowDistanceWarn = 50f,
			},
			new PlatformProfile
			{
				Platform = OptimizerPlatform.WebGL,
				DisplayName = "WebGL",
				Description = "Download size and browser memory are constrained: use WebGL-compatible texture formats and conservative asset budgets. Threading and heap capabilities depend on the selected WebGL configuration.",
				OverrideKey = "WebGL",
				IsMobileClass = false,
				IsVR = false,
				MaxTextureSize = 1024,
				MaxUiTextureSize = 512,
				MeshVertexWarn = 20000,
				MeshVertexCritical = 60000,
				AudioStreamingSeconds = 15f,
				AudioSfxSampleRate = 22050,
				AudioMaxQuality = 55,
				RealtimeLightWarn = 3,
				ShadowDistanceWarn = 60f,
			},
			new PlatformProfile
			{
				Platform = OptimizerPlatform.StandaloneVR,
				DisplayName = "Standalone VR (Quest / Pico)",
				Description =
					"Android hardware rendering two eyes at 72-90 Hz. The tightest budgets of any profile.",
				OverrideKey = "Android",
				IsMobileClass = true,
				IsVR = true,
				MaxTextureSize = 1024,
				MaxUiTextureSize = 512,
				MeshVertexWarn = 10000,
				MeshVertexCritical = 30000,
				AudioStreamingSeconds = 10f,
				AudioSfxSampleRate = 22050,
				AudioMaxQuality = 60,
				RealtimeLightWarn = 1,
				ShadowDistanceWarn = 25f,
			},
			new PlatformProfile
			{
				Platform = OptimizerPlatform.PCVR,
				DisplayName = "PC VR (SteamVR / Rift / Index)",
				Description =
					"Desktop GPU, but the scene is drawn twice and a dropped frame is felt immediately.",
				OverrideKey = "Standalone",
				IsMobileClass = false,
				IsVR = true,
				MaxTextureSize = 2048,
				MaxUiTextureSize = 1024,
				MeshVertexWarn = 20000,
				MeshVertexCritical = 60000,
				AudioStreamingSeconds = 15f,
				AudioSfxSampleRate = 22050,
				AudioMaxQuality = 70,
				RealtimeLightWarn = 3,
				ShadowDistanceWarn = 60f,
			},
			new PlatformProfile
			{
				Platform = OptimizerPlatform.Console,
				DisplayName = "Console (PS / Xbox)",
				Description =
					"Fixed high-end hardware. Importer overrides are resolved only when a concrete console build target is active; the abstract Console profile never falls back to Standalone.",
				OverrideKey = string.Empty,
				IsMobileClass = false,
				IsVR = false,
				MaxTextureSize = 2048,
				MaxUiTextureSize = 1024,
				MeshVertexWarn = 40000,
				MeshVertexCritical = 150000,
				AudioStreamingSeconds = 20f,
				AudioSfxSampleRate = 44100,
				AudioMaxQuality = 80,
				RealtimeLightWarn = 8,
				ShadowDistanceWarn = 200f,
			},
		};

		public static PlatformProfile[] All => k_All;

		public static PlatformProfile For(OptimizerPlatform platform)
		{
			for (int i = 0; i < k_All.Length; i++)
				if (k_All[i].Platform == platform)
					return k_All[i];
			return k_All[0];
		}
	}

	/// <summary>An exact, persisted waiver for one rule finding on one asset/path.</summary>
	[Serializable]
	public sealed class RuleSuppression
	{
		public string RuleId;
		public string AssetPath;
		public string Reason;
		public string CreatedUtc;

		public static string KeyFor(string ruleId, string assetPath) => (ruleId ?? string.Empty) + "\n" + (assetPath ?? string.Empty);
		public string Key => KeyFor(RuleId, AssetPath);
	}

	/// <summary>User-tunable thresholds and rule overrides. Persisted to ProjectSettings so the team shares them.</summary>
	[Serializable]
	public sealed class OptimizerSettings
	{
		const string k_Path = ProjectOptimizerConstants.SettingsPath;
		const string k_LegacyPath = ProjectOptimizerConstants.LegacySettingsPath;

		static OptimizerSettings s_Instance;
		public static OptimizerSettings Instance => s_Instance ?? (s_Instance = Load());

		/// <summary>
		/// Bumped on every save. A scan records the revision it ran under, so the window can tell
		/// "these findings are current" from "these findings predate the settings on screen" - which
		/// otherwise looks identical, and is how people end up acting on stale results.
		/// </summary>
		public static int Revision { get; private set; }

		/// <summary>Persisted settings revision so domain reloads do not reset the stale-result comparison.</summary>
		public int revision;

		// --- scan scope -------------------------------------------------
		public OptimizerPlatform targetPlatform = OptimizerPlatform.Desktop;

		/// <summary>How the set of assets to scan is chosen. See <see cref="ScanScopeMode"/>.</summary>
		public ScanScopeMode scopeMode = ScanScopeMode.EntireProject;

		/// <summary>Folders the scan is restricted to in <see cref="ScanScopeMode.SelectedFolders"/>.</summary>
		public List<string> includedFolders = new List<string>();

		public List<string> excludedFolders = new List<string>
		{
			"Assets/Plugins",
			"Assets/UnityMCP",
			"Assets/Editor",
			"Assets/TextMesh Pro",
			"Assets/Samples",
		};

		/// <summary>
		/// <see cref="AssetKind"/> values the user switched off, stored as ints because JsonUtility
		/// will not serialize a list of enums.
		/// </summary>
		public List<int> disabledAssetKinds = new List<int>();

		/// <summary>
		/// Assets larger than this are counted and reported but never loaded. Analyzing one asset is
		/// atomic - a half-gigabyte FBX or scene blocks the editor for as long as Unity takes to open
		/// it, and no amount of time slicing can interrupt that. 0 means no limit.
		/// </summary>
		public int maxAssetSizeMb = 512;

		public bool scanScenes = true;
		public bool scanPrefabs = true;
		public bool scanProjectSettings = true;
		public bool scanDuplicates = true;
		public bool scanUnused = true;
		public bool deepTextureScan;
		/// <summary>Explicitly builds the project-wide dependency/source proof index for evidence-gated rules.</summary>
		public bool projectProofPass = false;

		// --- scan performance / diagnostics ----------------------------
		/// <summary>Writes detailed scan timing to the Unity Console and a timestamped file in Library.</summary>
		public bool enableScanPerformanceLogging = true;
		/// <summary>Controls the amount of per-rule diagnostic work. Production disables rule timing; Aggregate keeps counters; Deep keeps samples.</summary>
		public ScanProfilingMode scanProfilingMode = ScanProfilingMode.DeepProfiling;
		/// <summary>Maximum concurrent workers used for filesystem-only scan work such as duplicate hashing.</summary>
		public int scanWorkerCount = 0;
		/// <summary>Maximum milliseconds spent in the hot scan loop before yielding back to the editor. 0 uses the adaptive default.</summary>
		public int scanFrameBudgetMs = 50;
		/// <summary>Maximum assets processed between progress/editor yields. 0 uses the adaptive default.</summary>
		public int scanBatchSize = 256;
		/// <summary>Main-thread pacing preset. Explicit frame/batch values remain the upper bounds.</summary>
		public ScanPacingMode scanPacingMode = ScanPacingMode.Balanced;
		/// <summary>Capacity of the bounded background work/result queues. 0 selects a worker-scaled value.</summary>
		public int scanQueueCapacity = 0;

		/// <summary>When enabled, capture one Memory Profiler snapshot after each Editor Play Mode session.</summary>
		public bool captureMemorySnapshotOnPlayModeExit = false;

		// --- textures ---------------------------------------------------
		public int maxTextureSize = 2048;
		public int maxUiTextureSize = 1024;
		public int textureFileSizeWarnKb = 2048;
		public bool preferCrunch = true;
		public int crunchQuality = 50;
		public int maxAnisoLevel = 4;
		public int streamingMipmapMinSize = 512;
		public int fontFallbackWarnCount = 3;

		// --- audio ------------------------------------------------------
		public float audioStreamingSeconds = 15f;
		public float audioCompressedInMemorySeconds = 5f;
		public int audioMaxQuality = 70;
		public int audioSfxSampleRate = 22050;
		public int audioFileSizeWarnKb = 2048;

		// --- models -----------------------------------------------------
		public int meshVertexWarn = 30000;
		public int meshVertexCritical = 100000;
		public int meshSubmeshWarn = 4;

		// --- animation --------------------------------------------------
		public int animationKeyframeWarn = 2000;
		public int animationCurveWarn = 200;

		/// <summary>
		/// How far a curve is allowed to move when a key is removed, as a percentage of the range
		/// that curve covers. Relative rather than absolute because a rotation in quaternion
		/// components and a position in metres are not comparable numbers. 0 switches ANI009 off.
		/// </summary>
		public float animationKeyTolerancePercent = 0.033f;

		// --- prefabs & scenes -------------------------------------------
		public int prefabObjectWarn = 300;
		public int hierarchyDepthWarn = 12;
		public int realtimeLightWarn = 6;
		public int particleMaxWarn = 1000;
		public int uiCanvasWarn = 8;
		public int uiRaycasterWarn = 4;
		public int uiLayoutWarn = 12;
		public int physicsSolverIterationsWarn = 12;
		public int rigidbodyCountWarn = 64;
		public int particleModuleCountWarn = 8;
		public int jointCountWarn = 16;
		public int terrainHeightmapResolutionWarn = 2049;
		public int terrainAlphamapResolutionWarn = 2048;
		public int terrainDetailResolutionWarn = 4096;
		public int terrainLayerCountWarn = 8;

		// --- fonts, atlases, video, controllers ---------------------------
		public int fontAtlasWarnSize = 1024;
		public float fontPaddingRatioWarn = 0.35f;
		public int videoHeightWarn = 1080;
		public int videoFileWarnKb = 51200;
		public int animatorLayerWarn = 4;
		public int animatorStateWarn = 40;

		// --- Auditor-derived project/build diagnostics -------------------
		public int spriteAtlasEmptySpaceWarnPercent = 50;
		public int textureAtlasEmptySpaceWarnPercent = 50;
		public int auditorDecompressedAudioThresholdKb = 200;
		/// <summary>Project Auditor small-audio threshold in decompressed PCM bytes, expressed in KB (default ~214 KB).</summary>
		public int auditorStreamingAudioThresholdKb = 214;
		public int auditorCompressedMobileAudioThresholdKb = 200;
		public int streamingAssetsWarnMb = 128;
		public int buildLargeAssetWarnMb = 16;
		public int buildLargeAssetCriticalMb = 64;
		public float buildAssetShareInfoPercent = 5f;
		public int buildStepWarnSeconds = 30;

		// --- misc -------------------------------------------------------
		// --- validated heuristic policies --------------------------------
		// 0 disables the corresponding budget check; these are project-policy inputs, not universal targets.
		public int shaderVariantMemoryBudgetMb = 512; // Per compressed shader chunk policy; 0 disables the check.
		public float lightmapResolutionReview = 40f;
		public int giDirectSampleReview = 256;
		public int giIndirectSampleReview = 256;
		public int giEnvironmentSampleReview = 256;
		public int giMaxBouncesReview = 10;
		public int physics2DPositionIterationsReview = 8;
		public int physics2DVelocityIterationsReview = 8;
		public int physicsVelocityIterationsReview = 1;
		public float addressablesDependencyEfficiencyReview = 0.25f;
		public int addressablesLargeBundleMb = 100;
		public int addressablesTinyBundleKb = 32;
		public int addressablesDependencyFanoutWarn = 8;

		public int largeFileWarnKb = 10240;

		// 0 denotes settings written before canonical rule IDs were compacted.
		public int ruleIdSchemaVersion;

		// --- rule overrides ---------------------------------------------
		public List<string> disabledRules = new List<string>();
		public List<string> severityOverrideIds = new List<string>();
		public List<int> severityOverrideValues = new List<int>();

		/// <summary>Exact rule/path suppressions. A suppression hides one known finding without disabling the rule globally.</summary>
		public List<RuleSuppression> suppressions = new List<RuleSuppression>();

		// Runtime indexes are rebuilt once when settings are created/loaded and then updated incrementally
		// by the mutation methods below. They must not be rebuilt from the hot-path query methods.
		[NonSerialized] HashSet<string> m_DisabledRuleSet;
		[NonSerialized] HashSet<int> m_DisabledAssetKindSet;
		[NonSerialized] Dictionary<string, IssueSeverity> m_SeverityOverrideMap;
		[NonSerialized] Dictionary<string, string> m_SuppressionReasonMap;

		void RebuildRuntimeIndexes()
		{
			disabledRules ??= new List<string>();
			disabledAssetKinds ??= new List<int>();
			severityOverrideIds ??= new List<string>();
			severityOverrideValues ??= new List<int>();

			m_DisabledRuleSet = new HashSet<string>(disabledRules, StringComparer.Ordinal);
			m_DisabledAssetKindSet = new HashSet<int>(disabledAssetKinds);
			m_SeverityOverrideMap = new Dictionary<string, IssueSeverity>(StringComparer.Ordinal);
			m_SuppressionReasonMap = new Dictionary<string, string>(StringComparer.Ordinal);
			if (suppressions == null) suppressions = new List<RuleSuppression>();
			for (int i = suppressions.Count - 1; i >= 0; i--)
			{
				var suppression = suppressions[i];
				if (suppression == null || string.IsNullOrEmpty(suppression.RuleId) || string.IsNullOrEmpty(suppression.AssetPath)) { suppressions.RemoveAt(i); continue; }
				m_SuppressionReasonMap[RuleSuppression.KeyFor(suppression.RuleId, suppression.AssetPath)] = suppression.Reason ?? string.Empty;
			}
			for (int i = 0; i < severityOverrideIds.Count && i < severityOverrideValues.Count; i++)
			{
				string id = severityOverrideIds[i];
				if (!string.IsNullOrEmpty(id) && !m_SeverityOverrideMap.ContainsKey(id))
					m_SeverityOverrideMap.Add(id, (IssueSeverity)severityOverrideValues[i]);
			}
		}

		public bool IsRuleEnabled(string ruleId)
		{
			if (string.IsNullOrEmpty(ruleId)) return false;
			if (m_DisabledRuleSet == null) RebuildRuntimeIndexes();
			ruleId = RuleCatalog.CanonicalizeId(ruleId);
			if (m_DisabledRuleSet.Contains(ruleId)) return false;
			// Honor disabled IDs saved before the alias was introduced.
			var aliases = RuleCatalog.GetAliasesFor(ruleId);
			for (int i = 0; i < aliases.Count; i++)
				if (m_DisabledRuleSet.Contains(aliases[i])) return false;
			return true;
		}

		public bool IsSuppressed(string ruleId, string assetPath)
		{
			if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(assetPath)) return false;
			if (m_SuppressionReasonMap == null) RebuildRuntimeIndexes();
			ruleId = RuleCatalog.CanonicalizeId(ruleId);
			if (m_SuppressionReasonMap.ContainsKey(RuleSuppression.KeyFor(ruleId, assetPath))) return true;
			var aliases = RuleCatalog.GetAliasesFor(ruleId);
			for (int i = 0; i < aliases.Count; i++)
				if (m_SuppressionReasonMap.ContainsKey(RuleSuppression.KeyFor(aliases[i], assetPath))) return true;
			return false;
		}

		public bool Suppress(string ruleId, string assetPath, string reason)
		{
			if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(assetPath)) return false;
			if (m_SuppressionReasonMap == null) RebuildRuntimeIndexes();
			ruleId = RuleCatalog.CanonicalizeId(ruleId);
			string key = RuleSuppression.KeyFor(ruleId, assetPath);
			if (IsSuppressed(ruleId, assetPath)) return false;
			if (suppressions == null) suppressions = new List<RuleSuppression>();
			suppressions.Add(new RuleSuppression { RuleId = ruleId, AssetPath = assetPath, Reason = reason ?? string.Empty, CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
			m_SuppressionReasonMap[key] = reason ?? string.Empty;
			return true;
		}

		public bool Unsuppress(string ruleId, string assetPath)
		{
			if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(assetPath) || suppressions == null) return false;
			if (m_SuppressionReasonMap == null) RebuildRuntimeIndexes();
			string canonicalId = RuleCatalog.CanonicalizeId(ruleId);
			bool removed = suppressions.RemoveAll(x => x != null
				&& string.Equals(RuleCatalog.CanonicalizeId(x.RuleId), canonicalId, StringComparison.Ordinal)
				&& string.Equals(x.AssetPath, assetPath, StringComparison.Ordinal)) > 0;
			if (m_SuppressionReasonMap != null)
			{
				m_SuppressionReasonMap.Remove(RuleSuppression.KeyFor(canonicalId, assetPath));
				var aliases = RuleCatalog.GetAliasesFor(canonicalId);
				for (int i = 0; i < aliases.Count; i++)
					m_SuppressionReasonMap.Remove(RuleSuppression.KeyFor(aliases[i], assetPath));
			}
			return removed;
		}

		public int ClearSuppressions()
		{
			int count = suppressions?.Count ?? 0;
			suppressions?.Clear();
			m_SuppressionReasonMap?.Clear();
			return count;
		}

		public string SuppressionReason(string ruleId, string assetPath)
		{
			if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(assetPath)) return string.Empty;
			if (m_SuppressionReasonMap == null) RebuildRuntimeIndexes();
			ruleId = RuleCatalog.CanonicalizeId(ruleId);
			if (m_SuppressionReasonMap.TryGetValue(RuleSuppression.KeyFor(ruleId, assetPath), out var reason)) return reason;
			var aliases = RuleCatalog.GetAliasesFor(ruleId);
			for (int i = 0; i < aliases.Count; i++)
				if (m_SuppressionReasonMap.TryGetValue(RuleSuppression.KeyFor(aliases[i], assetPath), out reason)) return reason;
			return string.Empty;
		}

		public void SetRuleEnabled(string ruleId, bool enabled)
		{
			if (string.IsNullOrEmpty(ruleId)) return;
			if (m_DisabledRuleSet == null) RebuildRuntimeIndexes();
			if (disabledRules == null) disabledRules = new List<string>();
			ruleId = RuleCatalog.CanonicalizeId(ruleId);
			var aliases = RuleCatalog.GetAliasesFor(ruleId);
			if (enabled)
			{
				disabledRules.RemoveAll(id => string.Equals(RuleCatalog.CanonicalizeId(id), ruleId, StringComparison.Ordinal));
				m_DisabledRuleSet.Remove(ruleId);
				for (int i = 0; i < aliases.Count; i++) m_DisabledRuleSet.Remove(aliases[i]);
			}
			else
			{
				// Persist disabled state under the canonical ID only.
				for (int i = 0; i < aliases.Count; i++) m_DisabledRuleSet.Remove(aliases[i]);
				disabledRules.RemoveAll(id => aliases.Contains(id));
				if (m_DisabledRuleSet.Add(ruleId)) disabledRules.Add(ruleId);
			}
		}

		public IssueSeverity SeverityFor(RuleDef def)
		{
			if (def == null) throw new ArgumentNullException(nameof(def));
			if (m_SeverityOverrideMap == null) RebuildRuntimeIndexes();
			if (m_SeverityOverrideMap.TryGetValue(def.Id, out var severity)) return severity;
			var aliases = RuleCatalog.GetAliasesFor(def.Id);
			for (int i = 0; i < aliases.Count; i++)
				if (m_SeverityOverrideMap.TryGetValue(aliases[i], out severity)) return severity;
			return def.DefaultSeverity;
		}

		public void SetSeverityOverride(string ruleId, IssueSeverity? severity)
		{
			if (string.IsNullOrEmpty(ruleId)) return;
			if (m_SeverityOverrideMap == null) RebuildRuntimeIndexes();
			severityOverrideIds ??= new List<string>();
			severityOverrideValues ??= new List<int>();
			ruleId = RuleCatalog.CanonicalizeId(ruleId);
			if (severity == null)
			{
				var aliases = RuleCatalog.GetAliasesFor(ruleId);
				var removeIds = new HashSet<string>(aliases, StringComparer.Ordinal) { ruleId };
				for (int i = severityOverrideIds.Count - 1; i >= 0; i--)
				{
					if (!removeIds.Contains(severityOverrideIds[i])) continue;
					severityOverrideIds.RemoveAt(i);
					if (i < severityOverrideValues.Count) severityOverrideValues.RemoveAt(i);
				}
				foreach (string id in removeIds) m_SeverityOverrideMap.Remove(id);
				return;
			}
			// New overrides are stored against canonical IDs; remove stale alias copies so they cannot
			// unexpectedly take precedence after a later reload.
			var legacyAliases = RuleCatalog.GetAliasesFor(ruleId);
			for (int i = severityOverrideIds.Count - 1; i >= 0; i--)
			{
				for (int a = 0; a < legacyAliases.Count; a++)
					if (string.Equals(severityOverrideIds[i], legacyAliases[a], StringComparison.Ordinal))
					{
						severityOverrideIds.RemoveAt(i);
						if (i < severityOverrideValues.Count) severityOverrideValues.RemoveAt(i);
						break;
					}
			}
			foreach (string id in legacyAliases) m_SeverityOverrideMap.Remove(id);
			int idx = severityOverrideIds.IndexOf(ruleId);
			if (idx >= 0)
			{
				if (idx >= severityOverrideValues.Count) severityOverrideValues.Add((int)severity.Value);
				else severityOverrideValues[idx] = (int)severity.Value;
				m_SeverityOverrideMap[ruleId] = severity.Value;
			}
			else
			{
				severityOverrideIds.Add(ruleId);
				severityOverrideValues.Add((int)severity.Value);
				m_SeverityOverrideMap[ruleId] = severity.Value;
			}
		}

		public bool IsExcluded(string assetPath)
		{
			for (int i = 0; i < excludedFolders.Count; i++)
				if (AssetPathUtility.IsUnder(assetPath, excludedFolders[i]))
					return true;
			return false;
		}

		public bool IsKindEnabled(AssetKind kind)
		{
			return !m_DisabledAssetKindSet.Contains((int)kind);
		}

		public void SetKindEnabled(AssetKind kind, bool enabled)
		{
			int value = (int)kind;
			if (enabled)
			{
				disabledAssetKinds.Remove(value);
				m_DisabledAssetKindSet.Remove(value);
			}
			else if (m_DisabledAssetKindSet.Add(value))
				disabledAssetKinds.Add(value);
		}

		public PlatformProfile Profile => PlatformProfile.For(targetPlatform);

		/// <summary>Unity's build-target string used by the platform override importer APIs.</summary>
		public string PlatformString => PlatformUtility.GetImporterPlatformString(targetPlatform);

		/// <summary>True where ASTC is the right compression family and mobile-class settings apply.</summary>
		public bool IsMobile => Profile.IsMobileClass;

		public bool IsVR => Profile.IsVR;

		/// <summary>Overwrites the budget thresholds with the ones recommended for the current profile.</summary>
		public void ApplyRecommendedBudgets()
		{
			var profile = Profile;
			maxTextureSize = profile.MaxTextureSize;
			maxUiTextureSize = profile.MaxUiTextureSize;
			meshVertexWarn = profile.MeshVertexWarn;
			meshVertexCritical = profile.MeshVertexCritical;
			audioStreamingSeconds = profile.AudioStreamingSeconds;
			audioSfxSampleRate = profile.AudioSfxSampleRate;
			audioMaxQuality = profile.AudioMaxQuality;
			realtimeLightWarn = profile.RealtimeLightWarn;
			Save();
		}

		/// <summary>True when every budget already matches the profile's recommendation.</summary>
		public bool MatchesRecommendedBudgets()
		{
			var profile = Profile;
			return maxTextureSize == profile.MaxTextureSize
				&& maxUiTextureSize == profile.MaxUiTextureSize
				&& meshVertexWarn == profile.MeshVertexWarn
				&& meshVertexCritical == profile.MeshVertexCritical
				&& Mathf.Approximately(audioStreamingSeconds, profile.AudioStreamingSeconds)
				&& audioSfxSampleRate == profile.AudioSfxSampleRate
				&& audioMaxQuality == profile.AudioMaxQuality
				&& realtimeLightWarn == profile.RealtimeLightWarn;
		}

		static OptimizerSettings Load()
		{
			try
			{
				string path = File.Exists(k_Path) ? k_Path : k_LegacyPath;
				if (File.Exists(path))
				{
					var json = File.ReadAllText(path);
					var loaded = JsonUtility.FromJson<OptimizerSettings>(json);
					if (loaded != null)
					{
						Revision = Math.Max(0, loaded.revision);
						bool migratedRuleIds = RuleIdMigration.MigrateSettings(loaded);
						loaded.RebuildRuntimeIndexes();
						if (migratedRuleIds) loaded.Save();
						return loaded;
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Could not read settings: {e.Message}");
			}
			var defaults = new OptimizerSettings { ruleIdSchemaVersion = RuleIdMigration.CurrentSchemaVersion };
			defaults.RebuildRuntimeIndexes();
			return defaults;
		}

		public void Save()
		{
			ruleIdSchemaVersion = RuleIdMigration.CurrentSchemaVersion;
			int nextRevision = Revision + 1;
			revision = nextRevision;
			try
			{
				var temporary = k_Path + ".tmp";
				File.WriteAllText(temporary, ToReadableJson());
				CacheFileUtility.ReplaceFile(temporary, k_Path);
				Revision = nextRevision;
			}
			catch (Exception e)
			{
				revision = Revision;
				try { if (File.Exists(k_Path + ".tmp")) File.Delete(k_Path + ".tmp"); } catch { }
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Could not write settings: {e.Message}");
			}
		}

		/// <summary>
		/// Serializes to JSON with the float fields written the way they were typed.
		/// <para>
		/// JsonUtility widens a float to a double before formatting it, and the exact decimal value
		/// of the float nearest 0.1 is 0.10000000149011612. That is what it writes. The number is
		/// correct and completely unreadable, in a file that lives in ProjectSettings and shows up in
		/// everyone's diffs.
		/// </para>
		/// <para>
		/// Each float field is rewritten using float's own round-trip format, which is the shortest
		/// text that parses back to the identical float - so this is a formatting change, not a
		/// precision one. Values that are not finite are left exactly as JsonUtility wrote them,
		/// since NaN and Infinity have no valid JSON spelling to improve on.
		/// </para>
		/// </summary>
		string ToReadableJson()
		{
			var json = JsonUtility.ToJson(this, true);

			foreach (var field in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				if (field.FieldType != typeof(float))
					continue;

				var value = (float)field.GetValue(this);
				if (float.IsNaN(value) || float.IsInfinity(value))
					continue;

				// Verbatim so the regex escapes stay regex escapes rather than C# ones.
				var pattern =
					@"("""
					+ Regex.Escape(field.Name)
					+ @"""\s*:\s*)(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)";
				var replacement = "${1}" + value.ToString("R", CultureInfo.InvariantCulture);
				json = Regex.Replace(json, pattern, replacement);
			}
			return json;
		}

		public void ResetToDefaults()
		{
			s_Instance = new OptimizerSettings();
			s_Instance.RebuildRuntimeIndexes();
			s_Instance.Save();
		}
	}
}

using System.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Non-script additions distilled from Unity Project Auditor 1.0.2.
	///
	/// This intentionally does not import Project Auditor's module framework or descriptor database.
	/// The useful part is the underlying evidence: project settings, package state, Resources
	/// dependencies and the last packed BuildReport. Findings remain native Project Optimizer issues,
	/// so Fix All, stale-result handling, reports and the existing UI continue to work unchanged.
	/// </summary>
	public sealed class ProjectAuditorSupplementAnalyzer : IProjectAnalyzer
	{
		const long k_Mb = 1024L * 1024L;

		float m_Progress;
		public string DisplayName => "Project Auditor supplement";
		public float Progress => m_Progress;
		public bool Enabled(OptimizerSettings settings) => true;

		static ProjectAuditorSupplementAnalyzer()
		{
			// --- Auditor project/settings findings not already represented by Project Optimizer ----
			Rules.Register(id: "PAS019", title: "Enter Fast Play Mode is not enabled", category: AssetCategory.ProjectSetting,
							why: "Without Enter Fast Play Mode, entering Play Mode performs the normal managed-domain reload. This reconstructs managed state and can increase editor iteration time on large projects.",
							fix: "Enable Enter Play Mode Options in Editor Settings. Treat disabling domain reload as a separate, deliberate workflow change and validate static state/reset code before relying on it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: true
							);

			Rules.Register(id: "PAS003", title: "iOS has both Metal and OpenGLES graphics APIs", category: AssetCategory.ProjectSetting,
							why: "Keeping multiple graphics APIs enabled can increase build/runtime surface area and shader/compatibility testing. Modern iOS targets normally use Metal.",
							fix: "Remove OpenGLES when your supported iOS devices are fully covered by Metal. Keep it only when you have a demonstrated compatibility requirement.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS004", title: "iOS Metal graphics API is not enabled", category: AssetCategory.ProjectSetting,
							why: "Metal is the primary modern graphics API for iOS. Omitting it may leave an avoidable compatibility/performance limitation, but graphics API selection remains a target-compatibility decision.",
							fix: "Enable Metal for iOS after confirming that every supported device and native/plugin dependency is covered. Review the API list rather than blindly replacing it.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS017", title: "Player splash screen is enabled", category: AssetCategory.ProjectSetting,
							why: "A splash screen is a deliberate product choice, but an enabled Unity splash can add startup presentation time and is unnecessary when the project already uses its own branded loading flow and the target license/settings allow disabling it.",
							fix: "Review the Unity splash setting and disable it only when the project and target-license rules permit it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS018", title: "Global audio speaker mode is not Mono", category: AssetCategory.ProjectSetting,
							why: "A stereo-wide global speaker mode increases output work where the product is intentionally mono, such as some voice-only/mobile experiences. This is a product-audio choice rather than a universal optimization.",
							fix: "Use Mono only when the game is genuinely designed for mono output. Otherwise keep the current mode.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS022", title: "IL2CPP compiler configuration is Master", category: AssetCategory.ProjectSetting,
							why: "Master enables additional native-code optimization beyond the normal Release configuration, trading substantially more build time for a shipping-oriented build.",
							fix: "Use Master for final shipping builds when its extra build time is acceptable; use Release for normal development and CI iteration.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS012", title: "Built-in Graphics Tier shader quality is inconsistent", category: AssetCategory.ProjectSetting,
							why: "Different Standard Shader Quality values across Graphics Tiers can produce more shader variants and therefore increase shader compilation work and build size.",
							fix: "Use a consistent Standard Shader Quality where the target platform does not genuinely require tier-specific variants.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS013", title: "Built-in rendering uses Forward rendering", category: AssetCategory.ProjectSetting,
							why: "Forward rendering can be appropriate for simple lighting, but scenes with many dynamic lights may spend substantial GPU time on repeated lighting work.",
							fix: "Profile Forward versus Deferred for the actual project. Keep Forward for 2D/simple/baked-light workloads when it wins.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS014", title: "Built-in rendering uses Deferred rendering", category: AssetCategory.ProjectSetting,
							why: "Deferred rendering can be advantageous with many dynamic lights, but simple scenes can pay extra bandwidth and g-buffer costs for little benefit.",
							fix: "Profile Deferred versus Forward for the actual project rather than changing the path as a blanket optimization.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS020", title: "Static batching is enabled with Entities Graphics", category: AssetCategory.ProjectSetting,
							why: "Unity Entities Graphics/Hybrid Renderer has its own batching path. Keeping classic static batching enabled can add memory/build data and work against a DOTS rendering strategy.",
							fix: "When the project genuinely uses Entities Graphics, profile with classic static batching disabled and keep only the batching systems that measurably help.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS021", title: "Built-in fog variants are not fully stripped", category: AssetCategory.ProjectSetting,
							why: "Fog keyword variants that the project never uses can increase shader variant counts, compilation time and player build size.",
							fix: "Use the Graphics settings fog-stripping configuration to keep only the fog modes the project actually renders.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS005", title: "Physics 2D layer collision matrix is fully enabled", category: AssetCategory.ProjectSetting,
							why: "A fully enabled 2D collision matrix makes Physics2D consider layer pairs that may never interact. Pruning impossible pairs reduces broadphase/contact work.",
							fix: "Disable only the layer pairs the gameplay design proves can never collide.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS008", title: "Quality Settings are using Unity's default quality levels", category: AssetCategory.ProjectSetting,
							why: "Leaving the default quality-level set untouched often means the project is shipping generic tiers instead of deliberate platform budgets. The result can be unnecessary quality work on lower-end devices.",
							fix: "Define intentional quality levels for the supported hardware classes and remove settings that are not relevant to the product.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS009", title: "A quality level reduces texture resolution globally", category: AssetCategory.ProjectSetting,
							why: "A global mip/texture limit can save memory and bandwidth on lower-quality tiers, but it also changes image quality throughout the project.",
							fix: "Keep reduced texture quality on low-end tiers when it is part of the product's quality strategy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS010", title: "Async upload time slice uses Unity's default", category: AssetCategory.ProjectSetting,
							why: "Texture/mesh upload work is budgeted through the async upload time slice. The default is not automatically ideal for every project's loading and frame-time profile.",
							fix: "Tune the async upload time slice against loading spikes and background streaming on the target hardware.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS011", title: "Async upload buffer size uses Unity's default", category: AssetCategory.ProjectSetting,
							why: "The async upload buffer controls how much data Unity can stage for background uploads. A deliberately sized buffer can change streaming smoothness and memory usage.",
							fix: "Tune the upload buffer using measured streaming/load behavior rather than assuming the default is optimal.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS001", title: "iOS architecture includes Universal/extra architectures", category: AssetCategory.ProjectSetting,
							why: "Shipping extra CPU architectures increases binary size and may duplicate native code in the player.",
							fix: "Ship only the iOS architecture(s) required by the project's deployment matrix. ARM64 is the normal modern baseline.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS002", title: "Android architecture includes both ARMv7 and ARM64", category: AssetCategory.ProjectSetting,
							why: "Including multiple Android CPU architectures can increase APK/AAB native-code size and build output when every device does not need every ABI.",
							fix: "Prefer ARM64-only for a modern deployment unless a supported device tier genuinely requires ARMv7.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS006", title: "Fixed Timestep is Unity's default 0.02 seconds", category: AssetCategory.ProjectSetting,
							why: "A 0.02 second fixed timestep requests up to 50 physics steps per second. That is reasonable for a 60 FPS simulation, but at a 30 FPS target it can cause catch-up work across frames.",
							fix: "Tune Fixed Timestep to the game's actual simulation frequency. Do not change it merely because the Auditor rule fires.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS007", title: "Maximum Allowed Timestep is Unity's default 0.1 seconds", category: AssetCategory.ProjectSetting,
							why: "A large maximum fixed-step catch-up window can let one bad frame trigger substantial subsequent physics work, making a frame-time spike self-reinforcing.",
							fix: "Choose a maximum that fits the target frame budget and simulation design, then validate physics behavior under stalls.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAA004", title: "Resources asset pulls dependencies into the Resources build data", category: AssetCategory.ProjectSetting,
							why: "An asset in Resources is runtime-loadable, but its dependency tree can also be pulled into the Resources build data. One small Resources asset can therefore retain a much larger graph than its own file suggests.",
							fix: "Move large or rarely loaded Resources content to explicit references/Addressables where practical, and verify runtime loading paths before changing it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAA005", title: "StreamingAssets folder is large", category: AssetCategory.ProjectSetting,
							why: "Everything in StreamingAssets is carried as opaque file content for runtime access. Large payloads increase the shipped footprint even when Unity cannot optimize the data like a normal imported asset.",
							fix: "Keep only runtime-required opaque files in StreamingAssets and move ordinary Unity-imported content into normal asset pipelines where possible.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAP001", title: "Package is not on Unity's recommended version", category: AssetCategory.Package,
							why: "Unity exposes a recommended package version for supported packages. Remaining on an older supported version can leave fixes and improvements unapplied.",
							fix: "Review the package in Package Manager and update to Unity's recommended version when the project's dependency graph and required features allow it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAP002", title: "Experimental/Preview package is installed", category: AssetCategory.Package,
							why: "Preview and experimental packages can change behavior or compatibility between Unity/package revisions and are not generally production-stable dependencies.",
							fix: "Remove preview packages that are not required for the product, or pin/upgrade them deliberately with CI validation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "BLD001", title: "Packed asset is a significant build contributor", category: AssetCategory.Build,
							why: "This grouped build insight reports absolute packed size and percentage of total build output as separate measurements. Large individual contributions are high-value prioritization targets, but neither attribution metric alone proves the exact size reduction a change will deliver.",
							fix: "Inspect the source asset and importer, then optimize the actual contributor rather than the source-file size alone.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "BLD002", title: "Packed asset is a significant build contributor", category: AssetCategory.Build,
							why: "This rule contributes the build-output-share measurement to the grouped build-contributor insight. Packed share is an attribution metric, not a guarantee that removing the asset reduces output by exactly that amount.",
							fix: "Prioritize assets with both high absolute packed size and high percentage share, then re-run the build to verify the change.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);


			Rules.Register(id: "BLD005", title: "Build changed materially since the previous compatible build", category: AssetCategory.Build,
				why: "A build-to-build change in output size or build time can reveal regressions that static asset rules cannot attribute on their own.",
				fix: "Inspect the changed packed contributors and build steps before changing project content.", severity: IssueSeverity.Warning, fixKind: FixKind.None, confidence: OptimizationConfidence.Proven, risk: FixRisk.Unspecified, backupRequired: false);
			Rules.Register(id: "BLD004", title: "Last build evidence is incomplete", category: AssetCategory.Build,
						why: "The persisted BuildReport does not contain a complete set of Unity build evidence. Build-size and packed-content findings may still be useful, but they should not be treated as a definitive completed-build baseline.",
						fix: "Run a clean/completed Player build and let Project Optimizer capture its BuildReport. Treat partial evidence as directional until then.",
						severity: IssueSeverity.Info,
						fixKind: FixKind.None,
						confidence: OptimizationConfidence.HighConfidence,
						risk: FixRisk.Unspecified,
						backupRequired: false
						);

			Rules.Register(id: "BLD003", title: "Build step takes a significant amount of time", category: AssetCategory.Build,
							why: "Unity's BuildReport records the duration of individual build steps. Long steps are useful profiling targets because they show where build time is actually being spent.",
							fix: "Inspect the step and its dependencies before changing build settings. Use repeated clean builds/CI measurements to confirm an optimization really moves the bottleneck.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "BLD006", title: "Last build evidence does not match the current build context", category: AssetCategory.Build,
							why: "A BuildReport is evidence about the configuration and Build Profile used for that build. A different Unity version, project/configuration fingerprint or Build Profile makes the report unsuitable as current-context evidence.",
							fix: "Build with the current Unity version, project configuration and active Build Profile, then let Project Optimizer capture that report.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

		}

		/// <summary>
		/// Runs only the report-backed portions of this analyzer.
		/// Used by the Build Report toolbar action so loading a saved BuildReport
		/// does not execute unrelated project-settings analysis.
		/// </summary>
		public List<AssetEntry> AnalyzeBuildReportOnly(ScanContext ctx)
		{
			var results = new List<AssetEntry>();

			if (ctx == null)
				return results;

			if (ctx.Settings == null)
				ctx.Settings = OptimizerSettings.Instance;

			// Packages are independent of project-settings analysis and belong
			// to the same Insights section as the BuildReport data.
			try
			{
				foreach (var entry in AnalyzePackages(ctx))
				{
					if (entry != null)
						results.Add(entry);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning(
					ProjectOptimizerConstants.LogPrefix + " Could not analyze packages while loading the BuildReport: "
					+ e.Message);
			}

			// BuildReport-backed rows:
			//   AssetCategory.Build
			//   AssetCategory.BuildStep
			//   AssetCategory.BuildFile
			try
			{
				foreach (var entry in AnalyzeBuild(ctx))
				{
					if (entry != null)
						results.Add(entry);
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e);

				Debug.LogError(
					ProjectOptimizerConstants.LogPrefix + " Failed to convert the persisted BuildReport into "
					+ "Project Optimizer entries: " + e.Message);
			}

			return results;
		}

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			m_Progress = 0f;
			foreach (var entry in AnalyzeProjectSettings(ctx)) yield return entry;
			m_Progress = 0.28f;
			foreach (var entry in AnalyzeSolidColorTextures(ctx)) yield return entry;
			m_Progress = 0.36f;
			var resourcesEntry = AnalyzeResources(ctx);
			if (resourcesEntry != null) yield return resourcesEntry;
			m_Progress = 0.58f;
			var streamingEntry = AnalyzeStreamingAssets(ctx);
			if (streamingEntry != null) yield return streamingEntry;
			foreach (var entry in AnalyzePackages(ctx)) yield return entry;
			m_Progress = 0.72f;
			foreach (var entry in AnalyzeBuild(ctx)) yield return entry;
			m_Progress = 1f;
		}

		/// <summary>
		/// Runs only the package analysis used by the Packages Insights page.
		/// Does not execute project settings, asset, or build analysis.
		/// </summary>
		public List<AssetEntry> AnalyzePackagesOnly(ScanContext ctx)
		{
			var results = new List<AssetEntry>();

			foreach (var entry in AnalyzePackages(ctx))
			{
				if (entry != null)
					results.Add(entry);
			}

			return results;
		}

		IEnumerable<AssetEntry> AnalyzeProjectSettings(ScanContext ctx)
		{
			if (!PlatformUtility.HasConcreteTarget(ctx.Settings.targetPlatform))
			{
				var unavailable = NewEntry("ProjectSettings/TargetProfile", "Target Profile", "Project/Target");
				unavailable.Category = AssetCategory.ProjectSetting;
				unavailable.Set("target", ctx.Settings.Profile.DisplayName);
				unavailable.Set("status", "Concrete Unity target unavailable");
				unavailable.Set("reason", "The Console profile is abstract. Select an active PlayStation/Xbox/Switch console target before evaluating platform-specific settings.");
				unavailable.Finalise();
				yield return unavailable;
				yield break;
			}

			yield return AnalyzeEditorSettings();
			yield return AnalyzeGraphicsApis(ctx.Settings);
			yield return AnalyzePlayerSettings(ctx.Settings);
			yield return AnalyzeBuiltinGraphics(ctx.Settings);
			yield return AnalyzeEntities(ctx.Settings);
			yield return AnalyzeFogStripping(ctx.Settings);
			yield return AnalyzePhysics2D();
			yield return AnalyzeQualityDefaults(ctx.Settings);
			yield return AnalyzeArchitecture(ctx.Settings);
			yield return AnalyzeTimeSettings();
		}

		AssetEntry AnalyzeEditorSettings()
		{
			var entry = NewEntry("ProjectSettings/EditorSettings.asset", "Editor Settings", "Project/Editor");
			entry.Set("enterPlayModeOptions", EditorSettings.enterPlayModeOptionsEnabled ? "On" : "Off");
			entry.Set("reloadDomain", (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != EnterPlayModeOptions.DisableDomainReload ? "On" : "Off");

			using (RuleTiming.Begin("PAS019", entry.Path))
				if (!EditorSettings.enterPlayModeOptionsEnabled ||
					(EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0)
					entry.AddIssue("PAS019",
						EditorSettings.enterPlayModeOptionsEnabled
							? "Reload Domain: On"
							: "Enter Play Mode Options: Off",
						"Enter Fast Play Mode",
						0,
						c => ImporterFixUtility.Project(c, () =>
						{
							EditorSettings.enterPlayModeOptionsEnabled = true;
							EditorSettings.enterPlayModeOptions |= EnterPlayModeOptions.DisableDomainReload;
						}),
						verify: c => EditorSettings.enterPlayModeOptionsEnabled &&
							(EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0);
			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeGraphicsApis(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/GraphicsSettings.asset", "Graphics APIs", "Project/Graphics");
			var target = PlatformUtility.ToBuildTarget(settings.targetPlatform);
			var apis = Array.Empty<GraphicsDeviceType>();
			try { apis = PlayerSettings.GetGraphicsAPIs(target) ?? apis; } catch { }
			entry.Set("target", settings.Profile.DisplayName);
			entry.Set("apis", apis.Length == 0 ? "Unavailable" : string.Join(", ", apis.Select(a => a.ToString()).ToArray()));

			if (target == BuildTarget.iOS)
			{
				bool hasMetal = apis.Contains(GraphicsDeviceType.Metal);
				// Inspect legacy iOS API lists produced by older projects.
#pragma warning disable CS0618
				bool hasGles = apis.Contains(GraphicsDeviceType.OpenGLES2) || apis.Contains(GraphicsDeviceType.OpenGLES3);
#pragma warning restore CS0618

				using (RuleTiming.Begin("PAS003", entry.Path))
					if (hasMetal && hasGles)
						entry.AddIssue("PAS003", "Metal + OpenGLES", "Metal only unless a compatibility target requires both",
						options: new List<FixOption> { new FixOption {
							Label = "Use Metal Only",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal })),
							Verify = _ => { var current = PlayerSettings.GetGraphicsAPIs(BuildTarget.iOS) ?? Array.Empty<GraphicsDeviceType>(); return current.Length == 1 && current[0] == GraphicsDeviceType.Metal; }
						} }
					);
				using (RuleTiming.Begin("PAS004", entry.Path))
					if (!hasMetal)
						entry.AddIssue("PAS004", "Metal not enabled", "Enable Metal", 0,
						c =>
						{
							var current = PlayerSettings.GetGraphicsAPIs(target) ?? Array.Empty<GraphicsDeviceType>();
							if (current.Contains(GraphicsDeviceType.Metal)) return;
							var updated = new GraphicsDeviceType[current.Length + 1];
							Array.Copy(current, updated, current.Length);
							updated[updated.Length - 1] = GraphicsDeviceType.Metal;
							PlayerSettings.SetGraphicsAPIs(target, updated);
							c.ChangedProjectSettings = true;
						},
						verify: _ => (PlayerSettings.GetGraphicsAPIs(target) ?? Array.Empty<GraphicsDeviceType>()).Contains(GraphicsDeviceType.Metal));
			}

			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzePlayerSettings(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/ProjectSettings.asset", "Player / Runtime Settings", "Project/Player");
			entry.Set("scriptingBackend", PlayerSettings.GetScriptingBackend(PlatformUtility.ToNamedBuildTarget(settings.targetPlatform)).ToString());
			entry.Set("accelerometer", settings.Profile.IsMobileClass ? PlayerSettings.accelerometerFrequency.ToString() : "N/A");
			entry.Set("speakerMode", SafeSpeakerMode());

			using (RuleTiming.Begin("PAS017", entry.Path))
				if (PlayerSettings.SplashScreen.show)
					entry.AddIssue("PAS017", "Unity splash: On", "Review whether the project needs the Unity splash",
						options: new List<FixOption> { new FixOption {
							Label = "Disable Splash Screen",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticBool(typeof(PlayerSettings.SplashScreen), "show", false)),
							Verify = _ => !PlayerSettings.SplashScreen.show
						} }
					);

			if (settings.Profile.IsMobileClass && settings.targetPlatform == OptimizerPlatform.iOS
				&& PlayerSettings.accelerometerFrequency > 0)
				entry.Set("accelerometer", PlayerSettings.accelerometerFrequency.ToString());

			using (RuleTiming.Begin("PAS018", entry.Path))
				if (settings.Profile.IsMobileClass && AudioSettings.GetConfiguration().speakerMode != AudioSpeakerMode.Mono)
					entry.AddIssue("PAS018", SafeSpeakerMode(), "Mono only when product audio is designed for it",
						options: new List<FixOption> { new FixOption {
							Label = "Use Mono Speaker Mode",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings), "speakerMode", "Mono"))
						} }
					);

			var namedTarget = PlatformUtility.ToNamedBuildTarget(settings.targetPlatform);
			var backend = PlayerSettings.GetScriptingBackend(namedTarget);
			using (RuleTiming.Begin("PAS022", entry.Path))
				if (backend == ScriptingImplementation.IL2CPP)
				{
					var config = GetIl2CppConfiguration(namedTarget);
					entry.Set("il2cppConfiguration", config ?? "Unavailable");
					if (string.Equals(config, "Master", StringComparison.OrdinalIgnoreCase))
						entry.AddIssue("PAS022", "Master", "Master for shipping / Release for normal builds");
				}

			entry.Finalise();
			return entry;
		}

		static bool IsBuiltInForTarget(OptimizerSettings settings)
		{
			if (GraphicsSettings.defaultRenderPipeline != null) return false;
			try
			{
				var assets = new List<RenderPipelineAsset>();
				QualitySettings.GetAllRenderPipelineAssetsForPlatform(
					PlatformUtility.ToNamedBuildTarget(settings.targetPlatform).TargetName, ref assets);
				return assets == null || !assets.Any(a => a != null);
			}
			catch
			{
				return true;
			}
		}

		AssetEntry AnalyzeBuiltinGraphics(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/GraphicsSettings.asset", "Built-in Graphics Tiers", "Project/Graphics");
			bool builtIn = IsBuiltInForTarget(settings);
			entry.Set("pipeline", builtIn ? "Built-in" :
				(GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "SRP (asset unavailable)"));
			if (!builtIn)
			{
				entry.Finalise();
				return entry;
			}

			try
			{
				var group = BuildPipeline.GetBuildTargetGroup(PlatformUtility.ToBuildTarget(settings.targetPlatform));
				var tiers = new[] { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };
				var quality = tiers.Select(t => EditorGraphicsSettings.GetTierSettings(group, t).standardShaderQuality).ToArray();
				var paths = tiers.Select(t => EditorGraphicsSettings.GetTierSettings(group, t).renderingPath).ToArray();
				entry.Set("shaderQuality", string.Join(" / ", quality.Select(q => q.ToString()).ToArray()));
				entry.Set("renderingPaths", string.Join(" / ", paths.Select(p => p.ToString()).ToArray()));
				using (RuleTiming.Begin("PAS012", entry.Path))
					if (quality.Distinct().Count() > 1)
						entry.AddIssue("PAS012", "Mixed tier shader quality", "Prefer one quality unless tiering is intentional");
				using (RuleTiming.Begin("PAS013", entry.Path))
					if (paths.Contains(RenderingPath.Forward))
						entry.AddIssue("PAS013", "Forward", "Profile against Deferred for dynamic-light workloads");
				using (RuleTiming.Begin("PAS014", entry.Path))
					if (paths.Contains(RenderingPath.DeferredShading))
						entry.AddIssue("PAS014", "Deferred", "Profile against Forward for simple workloads");
			}
			catch (Exception e)
			{
				entry.Set("analysisError", e.Message);
			}

			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeEntities(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/PhysicsManager.asset", "Entities / Batching Compatibility", "Project/Rendering");
			bool entitiesGraphics = HasPackage("com.unity.entities.graphics");
			entry.Set("entitiesGraphicsDetected", entitiesGraphics ? "Yes" : "No");
			using (RuleTiming.Begin("PAS020", entry.Path))
				if (entitiesGraphics)
				{
					bool enabled = false;
					try { enabled = PlayerSettings.GetStaticBatchingForPlatform(PlatformUtility.ToBuildTarget(settings.targetPlatform)); }
					catch { }
					entry.Set("staticBatching", enabled ? "On" : "Off");
					if (enabled)
						entry.AddIssue("PAS020", "Static batching: On", "Profile with classic static batching Off",
						options: new List<FixOption> { new FixOption {
							Label = "Disable Static Batching",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetStaticBatchingForPlatform(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop), false)),
							Verify = _ => !PlayerSettings.GetStaticBatchingForPlatform(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop))
						} }
					);
				}
			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeFogStripping(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/GraphicsSettings.asset", "Fog Variant Stripping", "Project/Graphics");
			if (!IsBuiltInForTarget(settings))
			{
				entry.Set("pipeline", "SRP target; built-in fog stripping not applicable");
				entry.Finalise();
				return entry;
			}
			try
			{
				using (RuleTiming.Begin("PAS021", entry.Path))
				{
					var graphics = GraphicsSettings.GetGraphicsSettings();
					var so = new SerializedObject(graphics);
					var strip = so.FindProperty("m_FogStripping");
					entry.Set("fogStripping", strip != null ? strip.enumDisplayNames[strip.enumValueIndex] : "Unavailable");
					if (strip != null)
					{
						string[] names = { "m_FogKeepLinear", "m_FogKeepExp", "m_FogKeepExp2" };
						string[] labels = { "Linear", "Exponential", "Exponential Squared" };
						bool anyKept = false;
						for (int i = 0; i < names.Length; i++)
						{
							var p = so.FindProperty(names[i]);
							bool kept = p != null && p.boolValue;
							entry.Set("keepFog." + labels[i], kept ? "Yes" : "No");
							anyKept |= kept;
						}
						if (strip.enumValueIndex != 0 && anyKept)
							entry.AddIssue("PAS021", "Unused fog modes are retained", "Strip fog modes the project never uses");
					}
				}
			}
			catch (Exception e)
			{
				entry.Set("analysisError", e.Message);
			}
			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzePhysics2D()
		{
			var entry = NewEntry("ProjectSettings/Physics2DSettings.asset", "Physics 2D", "Project/Physics");
			int named = 0;
			int ignored = 0;
			for (int i = 0; i < 32; i++)
				if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) named++;
			for (int a = 0; a < 32; a++)
			{
				if (string.IsNullOrEmpty(LayerMask.LayerToName(a))) continue;
				for (int b = a; b < 32; b++)
				{
					if (string.IsNullOrEmpty(LayerMask.LayerToName(b))) continue;
					if (Physics2D.GetIgnoreLayerCollision(a, b)) ignored++;
				}
			}
			entry.Set("namedLayers", (long)named);
			entry.Set("ignoredPairs", (long)ignored);
			using (RuleTiming.Begin("PAS005", entry.Path))
				if (named >= 4 && ignored == 0)
					entry.AddIssue("PAS005", "No 2D layer pairs ignored", "Prune pairs that cannot collide");

			entry.Set("simulationMode", Physics2D.simulationMode.ToString());

			entry.Finalise();
			return entry;
		}

		const int k_SolidTextureMaxPixels = 4_000_000;

		IEnumerable<AssetEntry> AnalyzeSolidColorTextures(ScanContext ctx)
		{
			if (!ctx.Settings.deepTextureScan) yield break;
			foreach (var assetPath in ctx.AllAssetPaths)
			{
				TextureImporter importer;
				Texture2D texture;
				try
				{
					importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
					texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
				}
				catch { continue; }
				if (importer == null || texture == null || !texture.isReadable) continue;
				if (importer.textureType == TextureImporterType.NormalMap
					|| importer.textureType == TextureImporterType.Lightmap
					|| importer.textureType == TextureImporterType.Shadowmask) continue;
				long pixelCount = (long)texture.width * texture.height;
				if (pixelCount <= 1 || pixelCount > k_SolidTextureMaxPixels) continue;

				Color32[] pixels;
				try { pixels = texture.GetPixels32(); } catch { continue; }
				if (pixels == null || pixels.Length == 0) continue;
				Color32 first = pixels[0];
				bool solid = true;
				for (int i = 1; i < pixels.Length; i++)
				{
					if (!pixels[i].Equals(first)) { solid = false; break; }
				}
				if (!solid) continue;

				var entry = NewEntry(assetPath, Path.GetFileName(assetPath), "Texture/Deep Analysis");
				entry.Guid = AssetDatabase.AssetPathToGUID(assetPath);
				entry.Category = AssetCategory.Texture;
				entry.TypeLabel = "Texture";
				entry.RuntimeSize = UnityEditorUtility.RuntimeSizeOf(texture);
				entry.Set("dimensions", texture.width + "x" + texture.height);
				entry.Set("solidColor", "RGBA(" + first.r + ", " + first.g + ", " + first.b + ", " + first.a + ")");
				entry.AddIssue("PAA001", texture.width + "x" + texture.height + " solid-color texture", "Review replacing with 1x1 texture or material/property value");
				entry.Finalise();
				yield return entry;
			}
		}

		struct QualityLevelFacts
		{
			public int Index;
			public string Name;
			public int TextureMipmapLimit;
			public bool StreamingMipmapsActive;
			public int AsyncUploadTimeSlice;
			public int AsyncUploadBufferSize;
		}

		static List<QualityLevelFacts> ReadTargetQualityLevels(OptimizerSettings settings)
		{
			var result = new List<QualityLevelFacts>();
			try
			{
				var active = QualitySettings.GetActiveQualityLevelsForPlatform(PlatformUtility.ToNamedBuildTarget(settings.targetPlatform).TargetName);
				if (active == null || active.Length == 0) return result;

				var qualityObject = QualitySettings.GetQualitySettings();
				var so = new SerializedObject(qualityObject);
				var array = so.FindProperty("m_QualitySettings");
				if (array == null) return result;

				foreach (int index in active)
				{
					if (index < 0 || index >= array.arraySize) continue;
					var level = array.GetArrayElementAtIndex(index);
					result.Add(new QualityLevelFacts
					{
						Index = index,
						Name = SerializedPropertyUtility.ReadRelativeString(level, "name", "Quality " + index),
						TextureMipmapLimit = SerializedPropertyUtility.ReadRelativeInt(level, "globalTextureMipmapLimit", 0),
						StreamingMipmapsActive = SerializedPropertyUtility.ReadRelativeBool(level, "streamingMipmapsActive", false),
						AsyncUploadTimeSlice = SerializedPropertyUtility.ReadRelativeInt(level, "asyncUploadTimeSlice", -1),
						AsyncUploadBufferSize = SerializedPropertyUtility.ReadRelativeInt(level, "asyncUploadBufferSize", -1)
					});
				}
			}
			catch { }
			return result;
		}

		static bool IsUnityDefaultQualityName(int index, string name)
		{
			string[] defaults = { "Very Low", "Low", "Medium", "High", "Very High", "Maximum" };
			return index >= 0 && index < defaults.Length && string.Equals(name, defaults[index], StringComparison.Ordinal);
		}

		AssetEntry AnalyzeQualityDefaults(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/QualitySettings.asset", "Quality / Upload Budgets", "Project/Quality");
			var levels = ReadTargetQualityLevels(settings);
			entry.Set("target", settings.Profile.DisplayName);
			entry.Set("levels", (long)levels.Count);
			if (levels.Count == 0)
			{
				entry.Set("analysis", "No target quality levels could be read");
				entry.Finalise();
				return entry;
			}

			var reduced = new List<string>();
			var defaultTimeSlice = new List<string>();
			var defaultBuffer = new List<string>();
			bool allDefaultNames = true;
			foreach (var level in levels)
			{
				string label = level.Name + " (#" + level.Index + ")";
				if (level.TextureMipmapLimit > 0) reduced.Add(label + ": " + level.TextureMipmapLimit);
				if (level.AsyncUploadTimeSlice == 2) defaultTimeSlice.Add(level.Name);
				if (level.AsyncUploadBufferSize == 16) defaultBuffer.Add(level.Name + ": " + level.AsyncUploadBufferSize + " MB");
				allDefaultNames &= IsUnityDefaultQualityName(level.Index, level.Name);
			}

			entry.Set("textureLimitedLevels", reduced.Count > 0 ? string.Join(", ", reduced) : "None");
			entry.Set("asyncUploadDefaultTimeSliceLevels", defaultTimeSlice.Count > 0 ? string.Join(", ", defaultTimeSlice) : "None");
			entry.Set("asyncUploadDefaultBufferLevels", defaultBuffer.Count > 0 ? string.Join(", ", defaultBuffer) : "None");

			using (RuleTiming.Begin("PAS008", entry.Path))
				if (allDefaultNames && levels.Count == 6)
					entry.AddIssue("PAS008", "Unity default quality level names", "Define deliberate platform quality tiers");
			using (RuleTiming.Begin("PAS009", entry.Path))
				if (reduced.Count > 0)
					entry.AddIssue("PAS009", reduced.Count + " target quality level(s) reduce global texture resolution", "Keep only where intentional");
			using (RuleTiming.Begin("PAS010", entry.Path))
				if (defaultTimeSlice.Count > 0)
					entry.AddIssue("PAS010", defaultTimeSlice.Count + " target quality level(s) use the 2 ms default time slice", "Tune against streaming/frame-time measurements",
						options: new List<FixOption> { new FixOption {
							Label = "Increase Async Upload Time Slice",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.asyncUploadTimeSlice = Mathf.Max(4, QualitySettings.asyncUploadTimeSlice)),
							Verify = _ => QualitySettings.asyncUploadTimeSlice >= 4
						} }
					);
			using (RuleTiming.Begin("PAS011", entry.Path))
				if (defaultBuffer.Count > 0)
					entry.AddIssue("PAS011", defaultBuffer.Count + " target quality level(s) use a default async upload buffer", "Tune against loading/streaming measurements",
						options: new List<FixOption> { new FixOption {
							Label = "Increase Async Upload Buffer",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.asyncUploadBufferSize = Mathf.Max(32, QualitySettings.asyncUploadBufferSize)),
							Verify = _ => QualitySettings.asyncUploadBufferSize >= 32
						} }
					);

			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeArchitecture(OptimizerSettings settings)
		{
			var entry = NewEntry("ProjectSettings/ProjectSettings.asset", "Native Architecture", "Project/Player");
			using (RuleTiming.Begin("PAS001", entry.Path))
				if (settings.targetPlatform == OptimizerPlatform.iOS)
				{
					int architecture = GetArchitecture(BuildTargetGroup.iOS);
					entry.Set("architecture", architecture.ToString());
					if (architecture == 2)
						entry.AddIssue("PAS001", "Universal", "ARM64-only unless another architecture is required",
						options: new List<FixOption> { new FixOption {
							Label = "Use ARM64 Only",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetArchitecture(UnityEditor.Build.NamedBuildTarget.iOS, 1))
						} }
					);
				}
				else using (RuleTiming.Begin("PAS002", entry.Path))
					if (settings.targetPlatform == OptimizerPlatform.Android || settings.targetPlatform == OptimizerPlatform.StandaloneVR)
					{
						var arch = PlayerSettings.Android.targetArchitectures;
						entry.Set("architecture", arch.ToString());
						if ((arch & AndroidArchitecture.ARMv7) != 0 && (arch & AndroidArchitecture.ARM64) != 0)
							entry.AddIssue("PAS002", "ARMv7 + ARM64", "ARM64-only for modern device matrices",
						options: new List<FixOption> { new FixOption {
							Label = "Use ARM64 Only",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64)
						} }
					);
					}
					else
					{
						entry.Set("architecture", "Not applicable");
					}
			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeTimeSettings()
		{
			var entry = NewEntry("ProjectSettings/TimeManager.asset", "Time Settings", "Project/Time");
			entry.Set("fixedTimestep", (double)Time.fixedDeltaTime);
			entry.Set("maximumTimestep", (double)Time.maximumDeltaTime);
			using (RuleTiming.Begin("PAS006", entry.Path))
				if (Mathf.Approximately(Time.fixedDeltaTime, 0.02f))
					entry.AddIssue("PAS006", Time.fixedDeltaTime.ToString("0.###"), "Tune to the simulation/target-FPS strategy",
						options: new List<FixOption> { new FixOption {
							Label = "Use 0.02s Fixed Timestep",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => Time.fixedDeltaTime = 0.02f)
						} }
					);
			using (RuleTiming.Begin("PAS007", entry.Path))
				if (Mathf.Approximately(Time.maximumDeltaTime, 0.1f))
					entry.AddIssue("PAS007", Time.maximumDeltaTime.ToString("0.###"), "Tune to the target frame budget",
						options: new List<FixOption> { new FixOption {
							Label = "Use 0.10s Maximum Timestep",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => Time.maximumDeltaTime = 0.1f)
						} }
					);
			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeResources(ScanContext ctx)
		{
			var entry = NewEntry("Assets/Resources", "Resources Dependency Footprint", "Project/Assets");
			int resources = 0;
			int dependencyHits = 0;
			long dependencyBytes = 0;
			var uniqueDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var path in ctx.AllAssetPaths)
			{
				if (!AssetPathUtility.IsInResources(path)) continue;
				resources++;
				string[] deps;
				try { deps = AssetDatabase.GetDependencies(path, true); }
				catch { deps = Array.Empty<string>(); }
				foreach (var dep in deps)
				{
					if (string.Equals(dep, path, StringComparison.OrdinalIgnoreCase)) continue;
					if (AssetPathUtility.IsInResources(dep)) continue;
					dependencyHits++;
					if (uniqueDependencies.Add(dep))
					{
						long size = (ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(dep) : FileUtility.FileSizeOf(dep));
						if (size > 0) dependencyBytes += size;
					}
				}
			}
			entry.Set("resourcesAssets", (long)resources);
			entry.Set("externalDependencyEdges", (long)dependencyHits);
			entry.Set("uniqueExternalDependencies", (long)uniqueDependencies.Count);
			entry.Set("uniqueExternalDependencySourceBytes", dependencyBytes);
			using (RuleTiming.Begin("PAA004", entry.Path))
				if (dependencyHits > 0)
				{
					// A single aggregate issue is intentionally used here. The existing UNU002 already
					// reports which Resources assets are present; this pass adds the missing dependency
					// footprint without generating thousands of duplicate rows on large projects.
					var issue = entry.AddIssue("PAA004", dependencyHits + " external dependency edge(s)",
						"Keep only intentional Resources dependency graphs");
					issue?.Note("Estimated unique source size across those dependency assets: " + DisplayUtility.FormatBytes(dependencyBytes), "Footprint");
				}
			entry.Finalise();
			return entry;
		}

		AssetEntry AnalyzeStreamingAssets(ScanContext ctx)
		{
			string folder = "Assets/StreamingAssets";
			long size = 0;
			int files = 0;
			if (Directory.Exists(folder))
			{
				try
				{
					foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
					{
						if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
						try { size += new FileInfo(file).Length; files++; } catch { }
					}
				}
				catch { }
			}
			var entry = NewEntry(folder, "StreamingAssets", "Project/Assets");
			entry.Set("files", (long)files);
			entry.Set("size", size);
			using (RuleTiming.Begin("PAA005", entry.Path))
				if (size >= (long)ctx.Settings.streamingAssetsWarnMb * k_Mb)
					entry.AddIssue("PAA005", DisplayUtility.FormatBytes(size), "Keep opaque StreamingAssets content intentionally small");
			entry.Finalise();
			return entry;
		}

		IEnumerable<AssetEntry> AnalyzePackages(ScanContext ctx)
		{
			var lockDepths = LoadPackageLockDepths();
			UnityEditor.PackageManager.PackageInfo[] registered = null;
			try { registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages(); } catch { }

			if (registered != null && registered.Length > 0)
			{
				foreach (var package in registered)
				{
					if (package == null || string.IsNullOrEmpty(package.name)) continue;
					var entry = NewEntry("Packages/" + package.name, package.name, "Project/Packages");
					entry.Category = AssetCategory.Package;
					entry.Extension = ".package";
					entry.TypeLabel = "Package";
					entry.Set("version", package.version ?? "Unknown");
					entry.Set("source", package.source.ToString());
					string recommended = null;
					try { recommended = package.versions != null ? package.versions.recommended : null; } catch { }
					if (lockDepths.TryGetValue(package.name, out var depth))
					{
						entry.Set("depth", (long)depth);
						entry.Set("dependencyType", depth == 0 ? "Direct" : "Transitive");
					}
					else
					{
						entry.Set("depth", 0L);
						entry.Set("dependencyType", "Unknown");
					}
					entry.Set("recommended", string.IsNullOrEmpty(recommended) ? "-" : recommended);
					entry.Set("status", IsPreviewVersion(package.version) ? "Preview" : "Registered");

					using (RuleTiming.Begin("PAP002", entry.Path))
						if (IsPreviewVersion(package.version))
						{
							entry.AddIssue("PAP002", package.version, "Stable release when one is available");
						}
						else using (RuleTiming.Begin("PAP001", entry.Path))
							if (package.source == PackageSource.Registry
									&& !string.IsNullOrEmpty(recommended)
									&& !string.Equals(package.version, recommended, StringComparison.OrdinalIgnoreCase))
							{
								entry.AddIssue("PAP001", package.version, "Recommended " + recommended);
							}

					entry.Finalise();
					yield return entry;
				}
				yield break;
			}

			// Fallback for unusual editor/package-manager states. The lock file still supports the
			// preview diagnostic, but it cannot reliably provide Unity's recommended-version metadata.
			string lockPath = "Packages/packages-lock.json";
			if (!File.Exists(lockPath)) yield break;
			string json;
			try { json = File.ReadAllText(lockPath); } catch { yield break; }
			foreach (var package in ParseLockPackages(json))
			{
				var entry = NewEntry("Packages/" + package.Name, package.Name, "Project/Packages");
				entry.Category = AssetCategory.Package;
				entry.Extension = ".package";
				entry.TypeLabel = "Package";
				entry.Set("version", package.Version);
				entry.Set("source", package.Source);
				entry.Set("depth", (long)package.Depth);
				entry.Set("dependencyType", package.Depth == 0 ? "Direct" : "Transitive");
				entry.Set("recommended", "-");
				entry.Set("status", IsPreviewVersion(package.Version) ? "Preview" : "Registered");
				if (IsPreviewVersion(package.Version))
					entry.AddIssue("PAP002", package.Version, "Stable release when one is available");
				entry.Finalise();
				yield return entry;
			}
		}

		public IEnumerable<AssetEntry> AnalyzeBuild(ScanContext ctx)
		{
			// Project Optimizer owns a stable persisted representation of the last completed Player
			// BuildReport. The post-build processor captures it immediately, so scanning is independent
			// of Unity's internal Library/LastBuild.buildreport lifecycle. On upgraded projects without
			// a capture yet, LoadOrCaptureLatest imports Unity's current report once as a migration path.
			var build = BuildReportStore.LoadOrCaptureLatest();
			if (build == null || build.summary == null) yield break;

			long dataSize = 0;
			if (build.packedAssets != null)
			{
				foreach (var packed in build.packedAssets)
				{
					if (packed == null || packed.contents == null) continue;
					foreach (var content in packed.contents)
						dataSize = AddSaturated(dataSize, content.packedSize);
				}
			}

			string summaryPath = string.IsNullOrEmpty(build.summary.outputPath)
				? BuildReportStore.GetLatestPath()
				: build.summary.outputPath;
			var summary = NewEntry("BuildReport/Summary", "Last Build Summary", "Build/Last Report");
			summary.Category = AssetCategory.Build;
			summary.Extension = ".buildreport";
			summary.TypeLabel = "Build Report";
			summary.FileSize = build.summary.totalSize;
			summary.Set("platform", build.summary.platform ?? "Unknown");
			summary.Set("result", build.summary.result ?? "Unknown");
			summary.Set("output", summaryPath);
			summary.Set("persistedReport", BuildReportStore.GetLatestPath());
			summary.Set("latestReportScope", "Player");
			summary.Set("capturedAtUtc", build.capturedAtUtc ?? "Unknown");
			summary.Set("started", build.summary.buildStartedAt ?? "Unknown");
			summary.Set("duration", build.summary.totalTimeSeconds.ToString("0.##") + " s");
			summary.Set("packedData", dataSize);
			summary.Set("packedFiles", CountPackedEntries(build));
			summary.Set("buildOutputFiles", build.files != null ? build.files.Length : 0L);
			summary.Set("evidenceQuality", string.IsNullOrEmpty(build.summary.evidenceQuality) ? "Unknown" : build.summary.evidenceQuality);
			if (!string.IsNullOrEmpty(build.summary.evidenceQualityReason)) summary.Set("evidenceQualityReason", build.summary.evidenceQualityReason);
			using (RuleTiming.Begin("BLD004", summary.Path))
				if (!string.Equals(build.summary.evidenceQuality, "Complete", StringComparison.OrdinalIgnoreCase))
					summary.AddIssue("BLD004", "Evidence: " + (build.summary.evidenceQuality ?? "Unknown"), "Capture a complete successful Player BuildReport before treating build data as a release baseline")
						?.Note("Raw build result: " + (build.summary.result ?? "Unknown"), "Result");
			string currentFingerprint = EvidenceIdentity.ComputeProjectFingerprint();
			string currentProfilePath = string.Empty;
			string currentProfileGuid = string.Empty;
			try
			{
				var active = ctx?.BuildProfiles?.Active;
				currentProfilePath = active?.AssetPath ?? string.Empty;
				currentProfileGuid = string.IsNullOrEmpty(currentProfilePath) ? string.Empty : AssetDatabase.AssetPathToGUID(currentProfilePath);
			}
			catch { }
			string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
			bool buildTargetMatches = !string.IsNullOrEmpty(build.summary.platform) && string.Equals(build.summary.platform, currentBuildTarget, StringComparison.OrdinalIgnoreCase);
			bool buildUnityMatches = string.IsNullOrEmpty(build.summary.unityVersion) || string.Equals(build.summary.unityVersion, Application.unityVersion, StringComparison.OrdinalIgnoreCase);
			bool projectIdentityAvailable = !string.IsNullOrEmpty(build.summary.projectFingerprint) && !string.IsNullOrEmpty(currentFingerprint);
			bool buildProjectMatches = !projectIdentityAvailable || string.Equals(build.summary.projectFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
			bool profileIdentityAvailable = !string.IsNullOrEmpty(build.summary.buildProfileGuid) || !string.IsNullOrEmpty(build.summary.buildProfilePath);
			bool currentProfileAvailable = !string.IsNullOrEmpty(currentProfileGuid) || !string.IsNullOrEmpty(currentProfilePath);
			bool buildProfileMatches = !profileIdentityAvailable || !currentProfileAvailable || ((!string.IsNullOrEmpty(build.summary.buildProfileGuid) && !string.IsNullOrEmpty(currentProfileGuid) && string.Equals(build.summary.buildProfileGuid, currentProfileGuid, StringComparison.OrdinalIgnoreCase)) || (string.IsNullOrEmpty(build.summary.buildProfileGuid) && string.Equals(build.summary.buildProfilePath, currentProfilePath, StringComparison.OrdinalIgnoreCase)));
			bool buildContextConfirmed = string.Equals(build.summary.evidenceQuality, "Complete", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrEmpty(build.summary.unityVersion)
				&& buildTargetMatches
				&& buildUnityMatches
				&& projectIdentityAvailable && buildProjectMatches
				&& profileIdentityAvailable && currentProfileAvailable && buildProfileMatches;
			using (RuleTiming.Begin("BLD006", summary.Path))
				if (!buildTargetMatches || !buildUnityMatches || !projectIdentityAvailable || (projectIdentityAvailable && !buildProjectMatches) || !profileIdentityAvailable || !currentProfileAvailable || (profileIdentityAvailable && currentProfileAvailable && !buildProfileMatches))
				{
					string reason = !buildTargetMatches ? "target platform mismatch or unavailable" : !buildUnityMatches ? "Unity version mismatch" : !projectIdentityAvailable ? "project/configuration identity unavailable" : !buildProjectMatches ? "project/configuration fingerprint mismatch" : !profileIdentityAvailable ? "Build Profile provenance unavailable" : !currentProfileAvailable ? "current Build Profile unavailable" : "Build Profile mismatch";
					summary.AddIssue("BLD006", reason, "Capture a new build report for the current build context", 0, null,
						"Build Unity=" + (build.summary.unityVersion ?? "Unknown") + ", project fingerprint=" + (string.IsNullOrEmpty(build.summary.projectFingerprint) ? "Unknown" : "present") + ", Build Profile=" + (string.IsNullOrEmpty(build.summary.buildProfileGuid) ? (build.summary.buildProfilePath ?? "Unknown") : build.summary.buildProfileGuid) + "; current Unity=" + Application.unityVersion + ", current Build Profile=" + (string.IsNullOrEmpty(currentProfileGuid) ? (currentProfilePath.Length == 0 ? "Unknown" : currentProfilePath) : currentProfileGuid) + ". Build-size/time findings remain suppressed until context identity is confirmed.");
				}
			var previousBuild = BuildReportStore.FindPreviousCompatible(build);
			if (buildContextConfirmed && previousBuild != null)
			{
				double sizeDelta = previousBuild.size > 0 ? build.summary.totalSize / (double)previousBuild.size - 1.0 : 0.0;
				double timeDelta = previousBuild.seconds > 0 ? (build.summary.totalTimeSeconds - previousBuild.seconds) / previousBuild.seconds : 0.0;
				if (Math.Abs(sizeDelta) >= 0.05 || Math.Abs(timeDelta) >= 0.20)
				{
					var issue = summary.AddIssue("BLD005", "Size " + sizeDelta.ToString("+0.0%;-0.0%;0%") + ", time " + timeDelta.ToString("+0%;-0%;0%"), "Inspect changed packed contributors and build steps");
					issue?.Note("Previous compatible build: " + (previousBuild.capturedAtUtc ?? "unknown"), "Baseline");
				}
			}
			summary.Finalise();
			yield return summary;

			foreach (var stepEntry in AnalyzeBuildSteps(build, ctx)) yield return stepEntry;
			foreach (var fileEntry in AnalyzeBuildFiles(build)) yield return fileEntry;

			var rows = new List<BuildPackedRow>();
			if (build.packedAssets != null)
				foreach (var packed in build.packedAssets)
				{
					if (packed == null || packed.contents == null) continue;
					foreach (var content in packed.contents)
					{
						long size = content.packedSize;
						if (size <= 0) continue;
						rows.Add(new BuildPackedRow
						{
							Path = content.sourceAssetPath,
							RuntimeType = string.IsNullOrEmpty(content.typeName) ? "Unknown" : content.typeName,
							PackedSize = size,
							Percent = build.summary.totalSize > 0 ? (double)size * 100.0 / build.summary.totalSize : 0.0,
							BuildFile = packed.shortPath,
							ImporterType = GetImporterType(content.sourceAssetPath)
						});
					}
				}
			rows.Sort((a, b) => b.PackedSize.CompareTo(a.PackedSize));

			int maxRows = Math.Min(100, rows.Count);
			for (int i = 0; i < maxRows; i++)
			{
				var row = rows[i];
				var path = string.IsNullOrEmpty(row.Path) ? "BuildReport/<unknown>" : row.Path.Replace('|', '_');
				var entry = NewEntry(path, Path.GetFileName(path), "Build/Last Report");
				entry.Category = AssetCategory.Build;
				entry.TypeLabel = "Packed Build Asset";
				entry.Extension = Path.GetExtension(path);
				entry.FileSize = row.PackedSize;
				entry.RuntimeSize = -1;
				entry.Set("source", row.Path);
				entry.Set("runtimeType", row.RuntimeType);
				entry.Set("importer", row.ImporterType);
				entry.Set("packedSize", row.PackedSize);
				entry.Set("share", row.Percent);
				entry.Set("buildFile", row.BuildFile);
				bool hasBuildContentEvidence = buildContextConfirmed && HasUsableBuildContentEvidence(build);
				long criticalBytes = (long)(ctx.Settings.buildLargeAssetCriticalMb * k_Mb);
				bool sizeThresholdExceeded;
				using (RuleTiming.Begin("BLD001", entry.Path))
					sizeThresholdExceeded = hasBuildContentEvidence
						&& (row.PackedSize >= (long)(ctx.Settings.buildLargeAssetWarnMb * k_Mb)
							|| row.PackedSize >= criticalBytes);
				bool shareThresholdExceeded;
				using (RuleTiming.Begin("BLD002", entry.Path))
					shareThresholdExceeded = hasBuildContentEvidence
						&& row.Percent >= ctx.Settings.buildAssetShareInfoPercent;
				if (sizeThresholdExceeded || shareThresholdExceeded)
				{
					string sizeValue = DisplayUtility.FormatBytes(row.PackedSize);
					string shareValue = row.Percent.ToString("0.##") + "%";
					var metrics = new List<GroupedFindingMetric>
					{
						new GroupedFindingMetric("BLD001", "Packed size",
							sizeValue + " (warning threshold " + ctx.Settings.buildLargeAssetWarnMb.ToString("0.##")
							+ " MB; critical threshold " + ctx.Settings.buildLargeAssetCriticalMb.ToString("0.##") + " MB)",
							sizeThresholdExceeded, sizeValue,
							row.PackedSize >= criticalBytes
								? "Keep below the critical threshold unless intentional"
								: "Review large packed contributors"),
						new GroupedFindingMetric("BLD002", "Share of build output",
							shareValue + " (informational threshold " + ctx.Settings.buildAssetShareInfoPercent.ToString("0.##") + "%)",
							shareThresholdExceeded, shareValue, "Prioritize this contributor in build-size optimization")
					};
					GroupedFindingPolicy.AddGroupedIssue(entry, metrics,
						"One build-contributor insight combines absolute packed size and share of total output. These are separate attribution signals; neither alone proves the exact size reduction that removing or changing the asset would deliver.");
				}
				entry.Finalise();
				yield return entry;
			}
		}

		static bool HasUsableBuildContentEvidence(BuildReportStore.StoredReport build)
		{
			if (build == null || build.summary == null) return false;
			if (build.packedAssets != null && build.packedAssets.Length > 0) return true;
			return build.files != null && build.files.Length > 0 && build.summary.totalSize > 0;
		}

		IEnumerable<AssetEntry> AnalyzeBuildSteps(BuildReportStore.StoredReport build, ScanContext ctx)
		{
			if (build.steps == null || build.steps.Length == 0) yield break;
			var rows = new List<BuildStepRow>();
			foreach (var step in build.steps)
			{
				if (step.durationSeconds <= 0.0) continue;
				rows.Add(new BuildStepRow
				{
					Name = step.name ?? "<unnamed>",
					Depth = step.depth,
					DurationSeconds = step.durationSeconds,
					Messages = step.messageCount
				});
			}
			rows.Sort((a, b) => b.DurationSeconds.CompareTo(a.DurationSeconds));
			int maxRows = Math.Min(100, rows.Count);
			for (int i = 0; i < maxRows; i++)
			{
				var row = rows[i];
				string key = "BuildReport/Steps/" + i.ToString("000") + "/" + SanitizePathSegment(row.Name);
				var entry = NewEntry(key, row.Name, "Build/Steps");
				entry.Category = AssetCategory.BuildStep;
				entry.Extension = ".buildstep";
				entry.TypeLabel = "Build Step";
				entry.FileSize = 0;
				entry.RuntimeSize = -1;
				entry.Set("duration", row.DurationSeconds);
				entry.Set("depth", (long)row.Depth);
				entry.Set("messages", (long)row.Messages);
				using (RuleTiming.Begin("BLD003", entry.Path))
					if (row.DurationSeconds >= ctx.Settings.buildStepWarnSeconds)
						entry.AddIssue("BLD003", row.DurationSeconds.ToString("0.##") + " s", "Profile this build step");
				entry.Finalise();
				yield return entry;
			}
		}

		IEnumerable<AssetEntry> AnalyzeBuildFiles(BuildReportStore.StoredReport build)
		{
			if (build.files == null || build.files.Length == 0) yield break;

			long total = build.summary != null ? build.summary.totalSize : 0;
			var rows = new List<BuildFileRow>();
			foreach (var file in build.files)
			{
				if (file.size <= 0) continue;
				rows.Add(new BuildFileRow
				{
					Path = file.path ?? "<unknown>",
					Size = file.size,
					Percent = total > 0 ? (double)file.size * 100.0 / total : 0.0,
					Role = string.IsNullOrEmpty(file.role) ? "Unknown" : file.role
				});
			}
			rows.Sort((a, b) => b.Size.CompareTo(a.Size));
			int maxRows = Math.Min(100, rows.Count);
			for (int i = 0; i < maxRows; i++)
			{
				var row = rows[i];
				var entry = NewEntry("BuildReport/Files/" + i.ToString("000") + "/" + SanitizePathSegment(row.Path), Path.GetFileName(row.Path), "Build/Output");
				entry.Category = AssetCategory.BuildFile;
				entry.Extension = Path.GetExtension(row.Path);
				entry.TypeLabel = "Build Output File";
				entry.FileSize = row.Size;
				entry.RuntimeSize = -1;
				entry.Set("outputSize", row.Size);
				entry.Set("share", row.Percent);
				entry.Set("role", row.Role);
				entry.Set("path", row.Path);
				entry.Finalise();
				yield return entry;
			}
		}

		static string SanitizePathSegment(string value)
		{
			if (string.IsNullOrEmpty(value)) return "unknown";
			var invalid = Path.GetInvalidFileNameChars();
			var chars = value.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
				if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\') chars[i] = '_';
			return new string(chars);
		}

		struct BuildStepRow
		{
			public string Name;
			public int Depth;
			public double DurationSeconds;
			public int Messages;
		}

		struct BuildFileRow
		{
			public string Path;
			public long Size;
			public double Percent;
			public string Role;
		}

		struct BuildPackedRow
		{
			public string Path;
			public string RuntimeType;
			public string ImporterType;
			public long PackedSize;
			public double Percent;
			public string BuildFile;
		}

		sealed class PackageRow
		{
			public string Name;
			public string Version;
			public string Source;
			public int Depth;
		}

		static Dictionary<string, int> LoadPackageLockDepths()
		{
			var result = new Dictionary<string, int>(StringComparer.Ordinal);
			const string path = "Packages/packages-lock.json";
			if (!File.Exists(path)) return result;
			try
			{
				foreach (var row in ParseLockPackages(File.ReadAllText(path)))
					if (!string.IsNullOrEmpty(row.Name)) result[row.Name] = row.Depth;
			}
			catch { }
			return result;
		}

		static IEnumerable<PackageRow> ParseLockPackages(string json)
		{
			int dependenciesIndex = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
			if (dependenciesIndex < 0) yield break;
			int start = json.IndexOf('{', dependenciesIndex);
			if (start < 0) yield break;
			int i = start + 1;
			while (i < json.Length)
			{
				while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
				if (i >= json.Length || json[i] == '}') yield break;
				if (json[i] != '"') { i++; continue; }
				int nameEnd = FindStringEnd(json, i + 1);
				if (nameEnd < 0) yield break;
				string name = json.Substring(i + 1, nameEnd - i - 1);
				int colon = json.IndexOf(':', nameEnd + 1);
				if (colon < 0) yield break;
				int objStart = json.IndexOf('{', colon + 1);
				if (objStart < 0) yield break;
				int objEnd = FindMatchingBrace(json, objStart);
				if (objEnd < 0) yield break;
				string obj = json.Substring(objStart, objEnd - objStart + 1);
				var vm = Regex.Match(obj, "\\\"version\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Singleline);
				var sm = Regex.Match(obj, "\\\"source\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Singleline);
				var dm = Regex.Match(obj, "\\\"depth\\\"\\s*:\\s*(\\d+)", RegexOptions.Singleline);
				if (vm.Success)
				{
					yield return new PackageRow
					{
						Name = name,
						Version = vm.Groups[1].Value,
						Source = sm.Success ? sm.Groups[1].Value : "Unknown",
						Depth = dm.Success ? int.Parse(dm.Groups[1].Value) : 0
					};
				}
				i = objEnd + 1;
			}
		}

		static int FindStringEnd(string text, int start)
		{
			bool escaped = false;
			for (int i = start; i < text.Length; i++)
			{
				char c = text[i];
				if (escaped) { escaped = false; continue; }
				if (c == '\\') { escaped = true; continue; }
				if (c == '"') return i;
			}
			return -1;
		}

		static int FindMatchingBrace(string text, int start)
		{
			int depth = 0;
			bool inString = false;
			bool escaped = false;
			for (int i = start; i < text.Length; i++)
			{
				char c = text[i];
				if (inString)
				{
					if (escaped) escaped = false;
					else if (c == '\\') escaped = true;
					else if (c == '"') inString = false;
					continue;
				}
				if (c == '"') { inString = true; continue; }
				if (c == '{') depth++;
				else if (c == '}' && --depth == 0) return i;
			}
			return -1;
		}

		static bool IsPreviewVersion(string version)
		{
			if (string.IsNullOrEmpty(version)) return false;
			return version.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0
				|| version.IndexOf("experimental", StringComparison.OrdinalIgnoreCase) >= 0
				|| version.IndexOf("-pre", StringComparison.OrdinalIgnoreCase) >= 0
				|| version.IndexOf(".pre", StringComparison.OrdinalIgnoreCase) >= 0
				|| version.IndexOf("-exp", StringComparison.OrdinalIgnoreCase) >= 0
				|| version.IndexOf(".exp", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool HasPackage(string packageName)
		{
			string lockPath = "Packages/packages-lock.json";
			if (File.Exists(lockPath))
			{
				try
				{
					string json = File.ReadAllText(lockPath);
					if (json.IndexOf("\"" + packageName + "\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
				catch { }
			}
			string manifest = "Packages/manifest.json";
			if (File.Exists(manifest))
			{
				try
				{
					string json = File.ReadAllText(manifest);
					if (json.IndexOf("\"" + packageName + "\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
				catch { }
			}
			return false;
		}

		static string GetImporterType(string path)
		{
			if (string.IsNullOrEmpty(path)) return "Unknown";
			try
			{
				var importer = AssetImporter.GetAtPath(path);
				return importer != null ? importer.GetType().Name : "Unknown";
			}
			catch { return "Unknown"; }
		}

		static int CountPackedEntries(BuildReportStore.StoredReport report)
		{
			int count = 0;
			if (report == null || report.packedAssets == null) return 0;
			foreach (var packed in report.packedAssets)
			{
				if (packed == null || packed.contents == null) continue;
				count += packed.contents.Length;
			}
			return count;
		}

		static long AddSaturated(long a, long b)
		{
			if (b <= 0) return a;
			return a > long.MaxValue - b ? long.MaxValue : a + b;
		}

		static AssetEntry NewEntry(string path, string name, string settingsPage)
		{
			var entry = new AssetEntry
			{
				Path = path,
				Name = name,
				Directory = Path.GetDirectoryName(path) ?? "ProjectSettings",
				Extension = Path.GetExtension(path),
				Category = AssetCategory.ProjectSetting,
				TypeLabel = "Settings",
				FileSize = FileUtility.FileSizeOf(path),
				RuntimeSize = -1
			};
			entry.Set("settingsPage", settingsPage);
			return entry;
		}

		static string SafeSpeakerMode()
		{
			try { return AudioSettings.GetConfiguration().speakerMode.ToString(); }
			catch { return "Unavailable"; }
		}

		static int GetArchitecture(BuildTargetGroup group)
		{
			try
			{
				var method = typeof(PlayerSettings).GetMethod("GetArchitecture", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
					null, new[] { typeof(UnityEditor.Build.NamedBuildTarget) }, null);
				if (method != null) return (int)method.Invoke(null, new object[] { PlatformUtility.ToNamedBuildTarget(group) });
			}
			catch { }
			return 0;
		}

		static string GetIl2CppConfiguration(UnityEditor.Build.NamedBuildTarget target)
		{
			try { return PlayerSettings.GetIl2CppCompilerConfiguration(target).ToString(); }
			catch { return null; }
		}

	}
}

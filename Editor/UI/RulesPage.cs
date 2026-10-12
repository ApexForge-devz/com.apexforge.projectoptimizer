using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Thresholds, scan scope, and per-rule enable/severity overrides.</summary>
	public sealed class RulesPage : VisualElement
	{
		readonly Action m_RequestRescan;
		readonly ScrollView m_Scroll;
		readonly TextField m_Search;

		/// <summary>Standing count of how far the rule set has been moved from its defaults.</summary>
		Label m_OverrideSummary;
		bool m_ThresholdsExpanded = true;
		bool m_RulesExpanded = true;
		readonly HashSet<string> m_ExpandedThresholdGroups = new HashSet<string>(StringComparer.Ordinal);
		bool m_ThresholdGroupStateInitialized;
		readonly HashSet<string> m_ExpandedRuleGroups = new HashSet<string>(StringComparer.Ordinal);
		bool m_RuleGroupStateInitialized;

		public RulesPage(Action requestRescan)
		{
			m_RequestRescan = requestRescan;
			AddToClassList("po-page");
			style.flexDirection = FlexDirection.Column;

			var bar = UiKit.Row("po-filters");
			m_Search = new TextField();
			m_Search.AddToClassList("po-search");
			m_Search.textEdition.placeholder = "Filter rules...";
			m_Search.RegisterValueChangedCallback(_ => Rebuild());
			bar.Add(m_Search);

			m_OverrideSummary = UiKit.Text(string.Empty, "po-status");
			m_OverrideSummary.style.marginLeft = 8f;
			bar.Add(m_OverrideSummary);

			bar.Add(UiKit.Spacer());
			bar.Add(UiKit.Button("Rescan with these settings", () => m_RequestRescan?.Invoke(),
				"po-button-primary", "po-button-small"));
			bar.Add(UiKit.Button("Enable all", () => SetAll(true), "po-button-small"));
			bar.Add(UiKit.Button("Disable all", () => SetAll(false), "po-button-small"));
			bar.Add(UiKit.Button("Reset to defaults", () =>
			{
				if (!EditorUtility.DisplayDialog("Reset settings",
					"Restore every threshold and rule override to its default?\n\n"
					+ "Scan scope is not affected - that lives on the Scan Scope page.", "Reset", "Cancel")) return;
				OptimizerSettings.Instance.ResetToDefaults();
				Rebuild();
				Feedback.Success("Thresholds and rule overrides restored to defaults.");
			}, "po-button-danger", "po-button-small"));
			Add(bar);

			m_Scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			m_Scroll.AddToClassList("po-scroll");
			Add(m_Scroll);

			Rebuild();
		}

		void SetAll(bool enabled)
		{
			var settings = OptimizerSettings.Instance;
			int count = 0;
			foreach (var rule in RuleCatalog.All)
			{
				settings.SetRuleEnabled(rule.Id, enabled);
				count++;
			}
			settings.Save();
			Rebuild();

			if (enabled) Feedback.Success("All " + count + " rules enabled.");
			else Feedback.Warn("All " + count + " rules disabled - a scan will report nothing.");
		}

		/// <summary>
		/// Says how many rules are off and how many severities are overridden. Cheap to recompute and
		/// updated in place, so toggling a rule 300 rows down still moves a number you can see.
		/// </summary>
		void UpdateOverrideSummary()
		{
			if (m_OverrideSummary == null) return;
			var settings = OptimizerSettings.Instance;

			int total = 0, disabled = 0;
			foreach (var rule in RuleCatalog.All)
			{
				total++;
				if (!settings.IsRuleEnabled(rule.Id)) disabled++;
			}
			int overrides = settings.severityOverrideIds.Count;
			int suppressed = settings.suppressions != null ? settings.suppressions.Count : 0;

			if (disabled == 0 && overrides == 0 && suppressed == 0)
			{
				m_OverrideSummary.text = total + " rules, all at their defaults";
				m_OverrideSummary.RemoveFromClassList("sev-warning");
				return;
			}

			var parts = new List<string>();
			if (disabled > 0) parts.Add(disabled + " off");
			if (overrides > 0) parts.Add(overrides + " re-graded");
			if (suppressed > 0) parts.Add(suppressed + " suppressed");
			m_OverrideSummary.text = total + " rules - " + string.Join(", ", parts.ToArray());
			m_OverrideSummary.AddToClassList("sev-warning");
		}

		public void Rebuild()
		{
			m_Scroll.Clear();
				UpdateOverrideSummary();
			var settings = OptimizerSettings.Instance;

			m_Scroll.Add(UiKit.Text("Appearance", "po-section-title"));
			var appearance = UiKit.Column("po-settings-group");
			appearance.Add(BuildAppearanceRow());
			appearance.Add(UiKit.Text(
				"Personal to this machine - unlike everything else on this page, it is not part of the "
				+ "project's shared settings and never shows up in a teammate's diff.",
				"po-rule-why"));
			m_Scroll.Add(appearance);

			m_Scroll.Add(UiKit.Text("Target platform", "po-section-title"));
			var scope = UiKit.Column("po-settings-group");
			scope.Add(BuildPlatformRow(settings));
			scope.Add(UiKit.Text(
				"What a scan is pointed at - folders, asset types and the expensive passes - now lives on "
				+ "the Scan Scope page, so there is one place to look. This page is only about what counts as "
				+ "a problem once an asset is being looked at.",
				"po-rule-why"));
			m_Scroll.Add(scope);

			m_Scroll.Add(UiKit.Text("Suppressed findings", "po-section-title"));
			m_Scroll.Add(BuildSuppressionSection(settings));

			var thresholds = new Foldout { text = "THRESHOLDS", value = m_ThresholdsExpanded };
			thresholds.AddToClassList("po-config-foldout");
			thresholds.RegisterValueChangedCallback(evt => m_ThresholdsExpanded = evt.newValue);
			var thresholdToolbar = UiKit.Row("po-foldout-toolbar");
			thresholdToolbar.Add(UiKit.Text("Thresholds are grouped by asset/content area. Use the controls to expand or collapse every group and item.", "po-foldout-toolbar-label"));
			thresholdToolbar.Add(UiKit.Spacer());
			thresholdToolbar.Add(UiKit.Button("Expand all", () => { thresholds.Query<Foldout>().ForEach(f => f.value = true); thresholds.value = true; }, "po-button-small"));
			thresholdToolbar.Add(UiKit.Button("Collapse all", () => { thresholds.Query<Foldout>().ForEach(f => f.value = false); thresholds.value = true; }, "po-button-small"));
			thresholds.Add(thresholdToolbar);

			var textures = UiKit.Column();
			textures.Add(IntRow("Max texture size", settings.maxTextureSize, v => settings.maxTextureSize = v,
				"Largest imported dimension allowed on a non-UI texture before TEX001 fires. Halving a texture's linear dimensions reduces its base texel count by roughly fourfold, "
				+ "but actual imported memory also depends on format, mipmaps, platform overrides and compression."));
			textures.Add(IntRow("Max UI / sprite size", settings.maxUiTextureSize, v => settings.maxUiTextureSize = v,
				"The same budget applied to Sprite, GUI and Cursor textures, which are drawn at a fixed screen size "
				+ "and almost never need the resolution 3D surfaces do."));
			textures.Add(IntRow("Warn above source size (KB)", settings.textureFileSizeWarnKb, v => settings.textureFileSizeWarnKb = v,
				"Size of the source file on disk that triggers TEX014. Catches oversized authored art that bloats "
				+ "version control and import times even when the imported result is fine."));
			textures.Add(BoolRow("Prefer crunch compression", settings.preferCrunch, v => settings.preferCrunch = v,
				"When on, TEX003 flags block-compressed textures that are not crunched. Crunch shrinks download and "
				+ "install size but not runtime memory, and it makes imports slower - so teams often enable it only "
				+ "late in production."));
			textures.Add(IntRow("Crunch / compression quality", settings.crunchQuality, v => settings.crunchQuality = Mathf.Clamp(v, 0, 100),
				"0-100, used both as the crunch quality set by the TEX003 fix and as the compression quality on any "
				+ "platform override this tool creates. 50 is Unity's default; higher means bigger files."));
			textures.Add(IntRow("Max anisotropic level", settings.maxAnisoLevel, v => settings.maxAnisoLevel = v,
				"Aniso levels above this trigger TEX012. Each anisotropic sample costs GPU bandwidth, and above 4 "
				+ "the difference is rarely visible outside large ground planes seen at grazing angles."));
			textures.Add(IntRow("Streaming mipmaps above size", settings.streamingMipmapMinSize, v => settings.streamingMipmapMinSize = v,
				"Mipmapped, non-UI textures at least this large trigger TEX011 when mipmap streaming is off. "
				+ "Streaming keeps only the mip levels the cameras actually need resident."));
			thresholds.Add(BuildThresholdGroup("Texture thresholds", textures));
			var audio = UiKit.Column();
			audio.Add(FloatRow("Stream clips longer than (s)", settings.audioStreamingSeconds, v => settings.audioStreamingSeconds = v,
				"Clips at or above this length should use the Streaming load type (AUD001), which keeps a small ring "
				+ "buffer resident instead of the whole decoded clip. Music and ambience belong here."));
			audio.Add(FloatRow("Treat clips shorter than (s) as effects", settings.audioCompressedInMemorySeconds, v => settings.audioCompressedInMemorySeconds = v,
				"Clips at or below this length are treated as sound effects, which drives AUD002, AUD003 and AUD009: "
				+ "Decompress On Load, no streaming, and a reduced sample rate."));
			audio.Add(IntRow("Max Vorbis quality", settings.audioMaxQuality, v => settings.audioMaxQuality = Mathf.Clamp(v, 1, 100),
				"Vorbis quality above this triggers AUD005, and is the value written by the AUD004 and AUD011 fixes. "
				+ "Past roughly 70 the file keeps growing while the difference stops being audible in game."));
			audio.Add(IntRow("Effect sample rate (Hz)", settings.audioSfxSampleRate, v => settings.audioSfxSampleRate = v,
				"Sample rate AUD009 recommends for short effects. Lowering sample rate can reduce decoded/PCM memory approximately in proportion to the rate, "
				+ "but the acceptable value depends on the effect's audible frequency content; verify the result for representative sounds."));
			audio.Add(IntRow("Warn above source size (KB)", settings.audioFileSizeWarnKb, v => settings.audioFileSizeWarnKb = v,
				"Audio source size that triggers AUD010, and the point at which a clip counts as large enough that "
				+ "preloading it into every scene load matters (AUD007)."));
			thresholds.Add(BuildThresholdGroup("Audio thresholds", audio));

			// Sections mirror the tabs in the nav rail. Anyone here is looking for the threshold
			// behind a finding they just read, so the grouping that helps is the one that matches
			// where they saw it - not whatever happens to share a C# type.
			var meshes = UiKit.Column();
			meshes.Add(IntRow("Warn above vertices", settings.meshVertexWarn, v => settings.meshVertexWarn = v,
				"Total vertex count across all meshes in one model file before MDL009 fires. Vertices cost shading, "
				+ "memory and bandwidth on every single draw."));
			meshes.Add(IntRow("Critical above vertices", settings.meshVertexCritical, v => settings.meshVertexCritical = v,
				"Vertex count that MDL009 calls out as critical in its detail text. It changes the wording, not the "
				+ "rule's severity - use the severity dropdown on MDL009 for that."));
			meshes.Add(IntRow("Warn above sub-meshes", settings.meshSubmeshWarn, v => settings.meshSubmeshWarn = v,
				"Sub-mesh count on one model that triggers MDL011. Each sub-mesh is a separate material section and generally requires its own draw call; batching can reduce some CPU overhead but does not remove the draw itself."));
			thresholds.Add(BuildThresholdGroup("Model and mesh budgets", meshes));
			var clips = UiKit.Column();
			clips.Add(IntRow("Warn above keyframes", settings.animationKeyframeWarn, v => settings.animationKeyframeWarn = v,
				"Total keyframes in a standalone clip, or across all clips in a model, before ANI005 fires. "
				+ "Keyframes are essentially the entire memory cost of an animation clip."));
			clips.Add(IntRow("Warn above curves", settings.animationCurveWarn, v => settings.animationCurveWarn = v,
				"Curve count in a standalone clip before ANI008 fires. Every curve is a binding that gets resolved "
				+ "and evaluated on each frame the clip plays."));
			clips.Add(FloatRow("Keyframe removal tolerance (%)", settings.animationKeyTolerancePercent,
				v => settings.animationKeyTolerancePercent = Mathf.Clamp(v, 0f, 10f),
				"How far a curve is allowed to move when ANI009 removes a key, as a percentage of the range "
				+ "that curve covers. Relative rather than absolute, because a rotation in quaternion components "
				+ "and a position in metres are not comparable numbers.\n\n"
				+ "0.1 removes keys that change nothing perceptible. Raising it removes more keys and starts "
				+ "changing the animation; 0 switches the rule off."));
			thresholds.Add(BuildThresholdGroup("Animation clip budgets", clips));
			var controllers = UiKit.Column();
			controllers.Add(IntRow("Warn above animator layers", settings.animatorLayerWarn, v => settings.animatorLayerWarn = v,
				"Layer count that triggers CTL001. Additional layers can add state-machine, evaluation and blending work; the exact cost depends on weights, masks and controller structure."));
			controllers.Add(IntRow("Warn above animator states", settings.animatorStateWarn, v => settings.animatorStateWarn = v,
				"State count across a controller before CTL002 fires. The whole graph is loaded and kept resident for "
				+ "every Animator that uses it."));
			thresholds.Add(BuildThresholdGroup("Animator controller budgets", controllers));
			var video = UiKit.Column();
			video.Add(IntRow("Warn above video height", settings.videoHeightWarn, v => settings.videoHeightWarn = v,
				"Video height that triggers VID001. Decode cost and bandwidth scale with resolution, and a clip playing "
				+ "into a small on-screen rectangle gains nothing from being larger than it."));
			video.Add(IntRow("Warn above video size (KB)", settings.videoFileWarnKb, v => settings.videoFileWarnKb = v,
				"Video file size that triggers VID002. Large video files can dominate build/download size when substantial footage is shipped, so review them against the target delivery budget."));
			thresholds.Add(BuildThresholdGroup("Video thresholds", video));
			var atlases = UiKit.Column();
			atlases.Add(IntRow("Warn above font atlas size", settings.fontAtlasWarnSize, v => settings.fontAtlasWarnSize = v,
				"Largest font atlas dimension before FNT001 fires. A font atlas is an ordinary texture held in memory "
				+ "for as long as anything can draw text with it, and memory scales with the square of this number."));
			atlases.Add(FloatRow("Warn above padding ratio", settings.fontPaddingRatioWarn, v => settings.fontPaddingRatioWarn = v,
				"Glyph padding as a fraction of the sampling point size, before FNT004 fires. Measured as a ratio because "
				+ "TextMeshPro scales padding with point size - fonts built for thick outlines legitimately sit around 0.2 to 0.3."));
			thresholds.Add(BuildThresholdGroup("Font and sprite atlas budgets", atlases));
			var auditor = UiKit.Column();
			auditor.Add(IntRow("Sprite Atlas empty-space warning (%)", settings.spriteAtlasEmptySpaceWarnPercent, v => settings.spriteAtlasEmptySpaceWarnPercent = Mathf.Clamp(v, 0, 99),
				"Generated Sprite Atlas area above this percentage is reported as PAA0008 during Deep Texture Scan. The measurement is intentionally opt-in because generating packed-atlas previews can be expensive."));
			auditor.Add(IntRow("StreamingAssets warning (MB)", settings.streamingAssetsWarnMb, v => settings.streamingAssetsWarnMb = Mathf.Max(1, v),
				"PAA3002-style diagnostic for opaque files under Assets/StreamingAssets. Lower it for projects where download/install size is especially constrained."));
			auditor.Add(IntRow("Build hotspot warning (MB)", settings.buildLargeAssetWarnMb, v => settings.buildLargeAssetWarnMb = Mathf.Max(1, v),
				"BLD001 appears for packed BuildReport contributors at or above this size."));
			auditor.Add(IntRow("Build hotspot critical (MB)", settings.buildLargeAssetCriticalMb, v => settings.buildLargeAssetCriticalMb = Mathf.Max(settings.buildLargeAssetWarnMb, v),
				"A higher second threshold used to make the build hotspot table distinguish exceptionally large contributors."));
			auditor.Add(FloatRow("Build share info threshold (%)", settings.buildAssetShareInfoPercent, v => settings.buildAssetShareInfoPercent = Mathf.Clamp(v, 0.1f, 100f),
				"BLD002 appears when one packed asset contributes at least this percentage of packed BuildReport data."));
			thresholds.Add(BuildThresholdGroup("Project Auditor / build insights", auditor));
			var policies = UiKit.Column();
			policies.Add(IntRow("Shader chunk size review budget (MB)", settings.shaderVariantMemoryBudgetMb, v => settings.shaderVariantMemoryBudgetMb = Mathf.Max(0, v),
				"PRJ069 compares the selected target's shader chunk size with this policy. 0 disables the check; it is not a claim about total shader resident memory."));
			policies.Add(FloatRow("Lightmap resolution review threshold", settings.lightmapResolutionReview, v => settings.lightmapResolutionReview = Mathf.Max(0f, v),
				"PRJ070 flags baked-lighting scenes above this texels-per-world-unit policy. 0 disables the check; lower values are not universally better."));
			policies.Add(IntRow("GI direct sample review threshold", settings.giDirectSampleReview, v => settings.giDirectSampleReview = Mathf.Max(0, v),
				"PRJ071 heuristic threshold for LightingSettings.directSampleCount. 0 disables this component of the check."));
			policies.Add(IntRow("GI indirect sample review threshold", settings.giIndirectSampleReview, v => settings.giIndirectSampleReview = Mathf.Max(0, v),
				"PRJ071 heuristic threshold for LightingSettings.indirectSampleCount. 0 disables this component of the check."));
			policies.Add(IntRow("GI environment sample review threshold", settings.giEnvironmentSampleReview, v => settings.giEnvironmentSampleReview = Mathf.Max(0, v),
				"PRJ071 heuristic threshold for LightingSettings.environmentSampleCount. 0 disables this component of the check."));
			policies.Add(IntRow("GI max bounces review threshold", settings.giMaxBouncesReview, v => settings.giMaxBouncesReview = Mathf.Max(0, v),
				"PRJ071 heuristic threshold for LightingSettings.maxBounces. 0 disables this component of the check."));
			policies.Add(IntRow("Physics2D position iterations review threshold", settings.physics2DPositionIterationsReview, v => settings.physics2DPositionIterationsReview = Mathf.Max(0, v),
				"PRJ072 heuristic threshold for Physics2D.positionIterations. 0 disables this component; do not treat it as a universal optimal value."));
			policies.Add(IntRow("Physics2D velocity iterations review threshold", settings.physics2DVelocityIterationsReview, v => settings.physics2DVelocityIterationsReview = Mathf.Max(0, v),
				"PRJ072 heuristic threshold for Physics2D.velocityIterations. 0 disables this component; solver fidelity is project-dependent."));
			policies.Add(IntRow("3D solver velocity review threshold", settings.physicsVelocityIterationsReview, v => settings.physicsVelocityIterationsReview = Mathf.Max(0, v),
				"PRJ073 heuristic threshold for Physics.defaultSolverVelocityIterations. 0 disables the check; Rigidbody-specific overrides still apply."));
			policies.Add(FloatRow("Addressables dependency efficiency review threshold", settings.addressablesDependencyEfficiencyReview, v => settings.addressablesDependencyEfficiencyReview = Mathf.Clamp(v, 0.01f, 0.99f),
				"ADR002 reports Addressables bundle dependency links below this measured efficiency ratio. It is a review threshold, not a universal packing target."));
			thresholds.Add(BuildThresholdGroup("Project setting policies", policies));
			var scenes = UiKit.Column();
			scenes.Add(IntRow("Warn above GameObjects", settings.prefabObjectWarn, v => settings.prefabObjectWarn = v,
				"GameObject count in one prefab or scene before PRE005 fires. Every object costs a Transform plus "
				+ "time in scene load, hierarchy updates and culling, whether or not it does anything."));
			scenes.Add(IntRow("Warn above hierarchy depth", settings.hierarchyDepthWarn, v => settings.hierarchyDepthWarn = v,
				"Transform nesting depth before PRE006 fires. Deep chains make every position change propagate "
				+ "through each level above it."));
			scenes.Add(IntRow("Warn above realtime shadow lights", settings.realtimeLightWarn, v => settings.realtimeLightWarn = v,
				"Enabled, non-baked, shadow-casting lights in one scene before PRE012 fires. Each one re-renders the "
				+ "scene into a shadow map every frame."));
			scenes.Add(IntRow("Warn above max particles", settings.particleMaxWarn, v => settings.particleMaxWarn = v,
				"A Particle System's Max Particles value that triggers PRE009. The buffer is reserved up front, and "
				+ "the number caps how much simulation and overdraw the system can produce."));
			scenes.Add(IntRow("Warn above UI Canvases", settings.uiCanvasWarn, v => settings.uiCanvasWarn = Mathf.Max(1, v),
				"Canvas count in one prefab or scene before UIT001 fires. Use this as a project-policy signal; multiple canvases can be intentional."));
			scenes.Add(IntRow("Warn above Graphic Raycasters", settings.uiRaycasterWarn, v => settings.uiRaycasterWarn = Mathf.Max(1, v),
				"GraphicRaycaster count before UIT002 fires. Each active raycaster can contribute pointer/query work."));
			scenes.Add(IntRow("Warn above Layout components", settings.uiLayoutWarn, v => settings.uiLayoutWarn = Mathf.Max(1, v),
				"LayoutGroup/ContentSizeFitter count before UIT003 fires. Layout systems can trigger hierarchy rebuilds."));
			scenes.Add(IntRow("Warn above 3D solver iterations", settings.physicsSolverIterationsWarn, v => settings.physicsSolverIterationsWarn = Mathf.Max(1, v),
				"Rigidbody-specific solver iterations above this value trigger PHY002. Higher values increase solver work and should be justified by stability requirements."));
			scenes.Add(IntRow("Warn above Rigidbodies", settings.rigidbodyCountWarn, v => settings.rigidbodyCountWarn = Mathf.Max(1, v),
				"Rigidbody count before PHY001 fires. This is a profiling prompt, not proof that the count is excessive."));
			scenes.Add(IntRow("Warn above particle modules", settings.particleModuleCountWarn, v => settings.particleModuleCountWarn = Mathf.Max(1, v),
				"Enabled Collision, Lights and Trails particle modules across the hierarchy before VFX001 fires."));
			scenes.Add(IntRow("Warn above 3D/2D joints", settings.jointCountWarn, v => settings.jointCountWarn = Mathf.Max(1, v),
				"Joint count before PHY004 fires. Use as a project-policy signal; joint cost depends on the active constraint graph."));
			scenes.Add(IntRow("Warn above terrain heightmap resolution", settings.terrainHeightmapResolutionWarn, v => settings.terrainHeightmapResolutionWarn = Mathf.Max(0, v),
				"Heightmap side length before TER001 fires. 0 disables the check; high resolution can be justified by large or close-up terrain."));
			scenes.Add(IntRow("Warn above terrain alphamap resolution", settings.terrainAlphamapResolutionWarn, v => settings.terrainAlphamapResolutionWarn = Mathf.Max(0, v),
				"Alphamap side length before TER002 fires. 0 disables the check; required blending detail varies by terrain."));
			scenes.Add(IntRow("Warn above terrain detail resolution", settings.terrainDetailResolutionWarn, v => settings.terrainDetailResolutionWarn = Mathf.Max(0, v),
				"Detail resolution before TER003 fires. 0 disables the check; review it against actual detail density and platform targets."));
			scenes.Add(IntRow("Warn above terrain layers", settings.terrainLayerCountWarn, v => settings.terrainLayerCountWarn = Mathf.Max(0, v),
				"Terrain layer count before TER004 fires. 0 disables the check; layer count affects terrain data and rendering complexity."));
			thresholds.Add(BuildThresholdGroup("Prefab and scene budgets", scenes));
			var cleanup = UiKit.Column();
			cleanup.Add(IntRow("Warn above unreferenced file size (KB)", settings.largeFileWarnKb, v => settings.largeFileWarnKb = v,
				"An unreferenced asset at least this large is reported as UNU004, a warning, rather than UNU001, "
				+ "which is informational. Big dead files are the cheapest build-size win available."));
			thresholds.Add(BuildThresholdGroup("Unused asset thresholds", cleanup));

			m_ThresholdGroupStateInitialized = true;
			m_Scroll.Add(thresholds);
			m_Scroll.Add(BuildRuleList(settings));
		}

		VisualElement BuildRuleList(OptimizerSettings settings)
		{
			var filter = m_Search.value.Trim();
			bool searching = !string.IsNullOrEmpty(filter);
			var scores = searching ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
			var byCategory = new SortedDictionary<string, List<RuleDef>>(StringComparer.Ordinal);

			foreach (var rule in RuleCatalog.All)
			{
				if (searching)
				{
					var best = FuzzyMatch.BestScore(filter, new[]
					{
						rule.Id, rule.Title, DisplayUtility.CategoryLabel(rule.Category),
						rule.DefaultSeverity.ToString(), rule.Confidence.ToString(), rule.FixKind.ToString()
					});
					if (best == null) continue;
					scores[rule.Id] = best.Value;
				}

				var key = DisplayUtility.CategoryLabel(rule.Category);
				if (!byCategory.TryGetValue(key, out var list))
				{
					list = new List<RuleDef>();
					byCategory[key] = list;
				}
				list.Add(rule);
			}

			if (searching)
				foreach (var list in byCategory.Values)
					list.Sort((a, b) => scores[b.Id].CompareTo(scores[a.Id]));

			var root = new Foldout
			{
				text = "RULES  ·  " + (searching ? "matching" : RuleCatalog.All.Count.ToString("N0") + " registered"),
				value = m_RulesExpanded
			};
			root.AddToClassList("po-config-foldout");
			root.RegisterValueChangedCallback(evt => m_RulesExpanded = evt.newValue);

			var toolbar = UiKit.Row("po-foldout-toolbar");
			toolbar.Add(UiKit.Text(searching
				? "Matching rules are shown inside their category groups."
				: "Rules control evaluation, severity and available remediation. Hover a rule for its full explanation.",
				"po-foldout-toolbar-label"));
			toolbar.Add(UiKit.Spacer());
			toolbar.Add(UiKit.Button("Audit rules", AuditRules, "po-button-small"));
			toolbar.Add(UiKit.Button("Expand all", () => { root.Query<Foldout>().ForEach(f => f.value = true); root.value = true; }, "po-button-small"));
			toolbar.Add(UiKit.Button("Collapse all", () => { root.Query<Foldout>().ForEach(f => f.value = false); root.value = true; }, "po-button-small"));
			root.Add(toolbar);

			if (byCategory.Count == 0)
			{
				root.Add(UiKit.Empty("No matching rules", "Try a rule id, title, category, confidence, severity, or fix kind."));
				return root;
			}

			var severityNames = new List<string> { "Info", "Warning", "Critical" };

			foreach (var pair in byCategory)
			{
				string categoryKey = pair.Key;
				bool categoryOpen = searching || !m_RuleGroupStateInitialized || m_ExpandedRuleGroups.Contains(categoryKey);
				if (!m_RuleGroupStateInitialized) m_ExpandedRuleGroups.Add(categoryKey);

				var group = new Foldout
				{
					text = categoryKey + "  ·  " + pair.Value.Count + " rules",
					value = categoryOpen
				};
				group.AddToClassList("po-rule-category-foldout");
				if (searching) m_ExpandedRuleGroups.Add(categoryKey);
				group.RegisterValueChangedCallback(evt =>
				{
					if (evt.newValue) m_ExpandedRuleGroups.Add(categoryKey);
					else m_ExpandedRuleGroups.Remove(categoryKey);
				});

				foreach (var rule in pair.Value)
				{
					var ruleDef = rule;
					var ruleId = rule.Id;
					var row = UiKit.Row("po-rule-row");
					row.tooltip = RuleCatalog.Describe(rule.Id, settings.SeverityFor(rule));

					void Restyle()
					{
						bool enabled = settings.IsRuleEnabled(ruleId);
						row.EnableInClassList("po-rule-off", !enabled);
						row.EnableInClassList("po-rule-overridden", enabled && settings.SeverityFor(ruleDef) != ruleDef.DefaultSeverity);
					}

					var toggle = new Toggle { value = settings.IsRuleEnabled(rule.Id) };
					toggle.style.marginRight = 6f;
					toggle.tooltip = "Off means this rule is never evaluated, on any asset.";
					toggle.RegisterValueChangedCallback(evt =>
					{
						settings.SetRuleEnabled(ruleId, evt.newValue);
						settings.Save();
						Restyle();
						UpdateOverrideSummary();
					});
					row.Add(toggle);

					row.Add(UiKit.Text(rule.Id, "po-rule-id"));

					var body = UiKit.Column("po-rule-body");
					body.Add(UiKit.Text(rule.Title, "po-rule-title"));
					body.Add(UiKit.Text(rule.Summary, "po-rule-why"));
					row.Add(body);

					var confidence = UiKit.Text(DisplayUtility.ConfidenceLabel(rule.Confidence), "po-issue-confidence");
					confidence.tooltip = DisplayUtility.ConfidenceExplanation(rule.Confidence);
					row.Add(Spaced(confidence));

					if (rule.FixKind == FixKind.Verified)
						row.Add(Spaced(UiKit.Pill("VERIFIED FIX", "sev-clean")));
					else if (rule.FixKind == FixKind.Review)
						row.Add(Spaced(UiKit.Pill("REVIEW FIX", "sev-warning")));

					var current = settings.SeverityFor(rule);
					var severity = new PopupField<string>(severityNames, (int)current);
					severity.AddToClassList("po-dropdown");
					severity.style.width = 100f;
					severity.style.marginLeft = 8f;
					severity.tooltip = "Default for this rule is " + ruleDef.DefaultSeverity + ".";
					severity.RegisterValueChangedCallback(evt =>
					{
						var value = (IssueSeverity)severityNames.IndexOf(evt.newValue);
						settings.SetSeverityOverride(ruleId, value == ruleDef.DefaultSeverity ? (IssueSeverity?)null : value);
						settings.Save();
						Restyle();
						UpdateOverrideSummary();
					});
					row.Add(severity);

					Restyle();
					group.Add(row);
				}
				root.Add(group);
			}
			m_RuleGroupStateInitialized = true;
			return root;
		}

		void AuditRules()
		{
			var findings = RuleAudit.Run();
			if (findings.Count == 0)
			{
				Feedback.Success("Rule audit passed: no structural catalogue problems found.");
				return;
			}

			int errors = 0;
			for (int i = 0; i < findings.Count; i++)
				if (findings[i].IsError) errors++;

			var sb = new System.Text.StringBuilder();
			sb.AppendLine(ProjectOptimizerConstants.LogPrefix + " Rule audit");
			for (int i = 0; i < findings.Count; i++) sb.AppendLine(findings[i].ToString());
			sb.Append("Summary: ").Append(errors).Append(" error(s), ")
				.Append(findings.Count - errors).AppendLine(" warning(s).");
			if (errors > 0) Debug.LogError(sb.ToString());
			else Debug.LogWarning(sb.ToString());
			Feedback.Show("Rule audit found " + errors + " error(s) and " + (findings.Count - errors) + " warning(s). See the Console for details.");
		}

		VisualElement BuildSuppressionSection(OptimizerSettings settings)
		{
			var root = UiKit.Column("po-settings-group");
			var header = UiKit.Row("po-foldout-toolbar");
			header.Add(UiKit.Text("Exact finding waivers are shared in ProjectSettings. They hide only the listed rule/path combination; the underlying rule remains enabled.", "po-foldout-toolbar-label"));
			header.Add(UiKit.Spacer());
			if (settings.suppressions != null && settings.suppressions.Count > 0)
			{
				header.Add(UiKit.Button("Clear all", () =>
				{
					int count = settings.ClearSuppressions();
					settings.Save();
					Rebuild();
					Feedback.Show("Cleared " + count + " suppression(s).", FeedbackKind.Success);
				}, "po-button-small"));
			}
			root.Add(header);
			if (settings.suppressions == null || settings.suppressions.Count == 0)
			{
				root.Add(UiKit.Text("No suppressed findings.", "po-rule-why"));
				return root;
			}
			for (int i = 0; i < settings.suppressions.Count; i++)
			{
				var suppression = settings.suppressions[i];
				if (suppression == null) continue;
				string rule = suppression.RuleId; string path = suppression.AssetPath;
				var row = UiKit.Row("po-rule-row");
				row.Add(UiKit.Text(rule, "po-rule-id"));
				var body = UiKit.Column("po-rule-body");
				body.Add(UiKit.Text(path, "po-rule-title"));
				body.Add(UiKit.Text(string.IsNullOrEmpty(suppression.Reason) ? "No reason recorded." : suppression.Reason, "po-rule-why"));
				row.Add(body); row.Add(UiKit.Spacer());
				row.Add(UiKit.Button("Remove", () => { settings.Unsuppress(rule, path); settings.Save(); Rebuild(); }, "po-button-small"));
				root.Add(row);
			}
			return root;
		}

		VisualElement BuildThresholdGroup(string key, VisualElement content)
		{
			bool open = !m_ThresholdGroupStateInitialized || m_ExpandedThresholdGroups.Contains(key);
			if (!m_ThresholdGroupStateInitialized) m_ExpandedThresholdGroups.Add(key);
			var foldout = new Foldout { text = key + "  ·  threshold group", value = open };
			foldout.AddToClassList("po-threshold-group");
			foldout.RegisterValueChangedCallback(evt =>
			{
				if (evt.newValue) m_ExpandedThresholdGroups.Add(key);
				else m_ExpandedThresholdGroups.Remove(key);
			});
			foldout.Add(content);
			return foldout;
		}

		static VisualElement Spaced(VisualElement element)
		{
			element.style.marginLeft = 6f;
			return element;
		}

		// --- field helpers ---------------------------------------------------------

		VisualElement BoolRow(string label, bool value, Action<bool> setter, string help)
		{
			var field = new Toggle(label) { value = value, tooltip = help };
			field.AddToClassList("po-field");
			field.RegisterValueChangedCallback(evt =>
			{
				setter(evt.newValue);
				OptimizerSettings.Instance.Save();
			});
			return field;
		}

		VisualElement IntRow(string label, int value, Action<int> setter, string help)
		{
			var field = new IntegerField(label) { value = value, tooltip = help };
			field.AddToClassList("po-field");
			field.RegisterValueChangedCallback(evt =>
			{
				setter(evt.newValue);
				OptimizerSettings.Instance.Save();
			});
			return field;
		}

		VisualElement FloatRow(string label, float value, Action<float> setter, string help)
		{
			var field = new FloatField(label) { value = value, tooltip = help };
			field.AddToClassList("po-field");
			field.RegisterValueChangedCallback(evt =>
			{
				setter(evt.newValue);
				OptimizerSettings.Instance.Save();
			});
			return field;
		}

		/// <summary>Target profile plus a one-click way to adopt that profile's recommended budgets.</summary>
		VisualElement BuildPlatformRow(OptimizerSettings settings)
		{
			var column = UiKit.Column();

			var row = UiKit.Row();
			row.style.alignItems = Align.Center;
			row.style.marginBottom = 4f;

			var label = UiKit.Text("Target platform", "po-meta-label");
			label.style.width = 230f;
			label.style.height = 24f;
			row.Add(label);

			row.Add(UiKit.PlatformSelector(() =>
			{
				Rebuild();
				m_RequestRescan?.Invoke();
			}));

			var applyButton = UiKit.Button("Apply recommended budgets", () =>
			{
				settings.ApplyRecommendedBudgets();
				Rebuild();
				Feedback.Success("Thresholds set to the " + settings.Profile.DisplayName + " budgets.");
			}, "po-button-small");

			// Nothing to apply when they already match, and a disabled button says so without a click.
			bool matches = settings.MatchesRecommendedBudgets();
			applyButton.SetEnabled(!matches);
			applyButton.tooltip = matches
				? "Your thresholds already match this profile's recommendations."
				: "Overwrites your texture, mesh, audio and lighting budgets with this profile's recommended values.";
			row.Add(applyButton);

			if (matches) row.Add(UiKit.Pill("MATCHES PROFILE", "sev-clean"));
			column.Add(row);

			var description = UiKit.Text(settings.Profile.Description, "po-rule-why");
			description.style.marginLeft = 230f;
			description.style.marginBottom = 6f;
			column.Add(description);
			return column;
		}

		/// <summary>Colour theme picker plus a one-line description of whichever one is selected.</summary>
		VisualElement BuildAppearanceRow()
		{
			var column = UiKit.Column();

			var row = UiKit.Row();
			row.style.alignItems = Align.Center;
			row.style.marginBottom = 4f;

			var label = UiKit.Text("Theme", "po-meta-label");
			label.style.width = 230f;
			label.style.height = 24f;
			row.Add(label);

			var state = OptimizerWindowState.instance;
			var current = OptimizerThemes.Get(state.ThemeId);
			bool showDarkThemes = current.IsDark;

			// Light / Dark mode filter.
			var modeButton = UiKit.Button(showDarkThemes ? "☾" : "☀", () =>
			{
				var windowState = OptimizerWindowState.instance;
				var currentTheme = OptimizerThemes.Get(windowState.ThemeId);
				bool switchToDark = !currentTheme.IsDark;

				string rememberedThemeId = switchToDark
					? windowState.LastDarkThemeId
					: windowState.LastLightThemeId;

				var rememberedTheme = OptimizerThemes.Get(rememberedThemeId);

				// Make sure the remembered theme actually belongs to the mode we're switching to.
				if (rememberedTheme.IsDark != switchToDark)
				{
					rememberedTheme = null;

					for (int i = 0; i < OptimizerThemes.All.Length; i++)
					{
						var theme = OptimizerThemes.All[i];

						if (theme.IsDark == switchToDark)
						{
							rememberedTheme = theme;
							break;
						}
					}
				}

				if (rememberedTheme != null)
					OptimizerThemes.Select(rememberedTheme.Id);

				windowState.Persist();
				Rebuild();
			}, "po-button-small");

			modeButton.style.width = 30f;
			modeButton.tooltip = showDarkThemes
				? "Showing Dark themes. Click to switch to Light themes."
				: "Showing Light themes. Click to switch to Dark themes.";

			row.Add(modeButton);

			// Build the filtered theme list.
			var filteredThemes = new List<OptimizerTheme>();

			foreach (var theme in OptimizerThemes.All)
			{
				if (theme.IsDark == showDarkThemes)
					filteredThemes.Add(theme);
			}

			var themeNames = new List<string>();
			foreach (var theme in filteredThemes)
				themeNames.Add(theme.DisplayName);

			int currentIndex = 0;
			for (int i = 0; i < filteredThemes.Count; i++)
			{
				if (filteredThemes[i].Id == current.Id)
				{
					currentIndex = i;
					break;
				}
			}

			var prevButton = UiKit.Button("◀", () =>
			{
				if (filteredThemes.Count == 0) return;

				var windowState = OptimizerWindowState.instance;
				int index = filteredThemes.FindIndex(theme =>
					theme.Id == windowState.ThemeId);

				if (index < 0) index = 0;

				index = (index - 1 + filteredThemes.Count) % filteredThemes.Count;

				OptimizerThemes.Select(filteredThemes[index].Id);
				RememberThemeForCurrentMode(windowState, filteredThemes[index]);

				Rebuild();
			}, "po-button-small");

			prevButton.tooltip = "Previous " + (showDarkThemes ? "dark" : "light") + " theme";
			row.Add(prevButton);

			var dropdown = new PopupField<string>(themeNames, currentIndex);
			dropdown.AddToClassList("po-dropdown");
			dropdown.style.width = 200f;
			dropdown.style.marginLeft = 6f;
			dropdown.style.marginRight = 6f;

			dropdown.tooltip = "Choose a " + (showDarkThemes ? "dark" : "light") + " theme.";

			dropdown.RegisterValueChangedCallback(evt =>
			{
				int index = themeNames.IndexOf(evt.newValue);

				if (index < 0 || index >= filteredThemes.Count)
					return;

				var selectedTheme = filteredThemes[index];

				OptimizerThemes.Select(selectedTheme.Id);
				RememberThemeForCurrentMode(OptimizerWindowState.instance, selectedTheme);
				Rebuild();
			});

			row.Add(dropdown);

			var nextButton = UiKit.Button("▶", () =>
			{
				if (filteredThemes.Count == 0) return;

				var windowState = OptimizerWindowState.instance;
				int index = filteredThemes.FindIndex(theme =>
					theme.Id == windowState.ThemeId);

				if (index < 0) index = 0;

				index = (index + 1) % filteredThemes.Count;

				OptimizerThemes.Select(filteredThemes[index].Id);
				RememberThemeForCurrentMode(windowState, filteredThemes[index]);
				Rebuild();
			}, "po-button-small");

			nextButton.tooltip = "Next " + (showDarkThemes ? "dark" : "light") + " theme";
			row.Add(nextButton);

			// Random theme button.
			var randomButton = UiKit.Button("🎲", () =>
			{
				if (filteredThemes.Count <= 1)
					return;

				var windowState = OptimizerWindowState.instance;

				int currentThemeIndex = filteredThemes.FindIndex(theme =>
					theme.Id == windowState.ThemeId);

				// Pick a random index, but guarantee that it differs from the current theme.
				int randomIndex;

				do
				{
					randomIndex = UnityEngine.Random.Range(0, filteredThemes.Count);
				}
				while (randomIndex == currentThemeIndex);

				var randomTheme = filteredThemes[randomIndex];

				OptimizerThemes.Select(randomTheme.Id);
				RememberThemeForCurrentMode(windowState, randomTheme);

				Rebuild();
			}, "po-button-small");

			randomButton.style.width = 30f;
			randomButton.tooltip = "Choose a random " + (showDarkThemes ? "dark" : "light") + " theme.";
			randomButton.SetEnabled(filteredThemes.Count > 1);
			row.Add(randomButton);

			column.Add(row);

			var description = UiKit.Text(current.Description, "po-rule-why");
			description.style.marginLeft = 230f;
			description.style.marginBottom = 6f;
			column.Add(description);

			return column;
		}

		/// <summary>Remembers the last selected theme independently for Light and Dark mode.</summary>
		static void RememberThemeForCurrentMode(OptimizerWindowState state, OptimizerTheme theme)
		{
			if (theme == null)
				return;

			if (theme.IsDark)
				state.LastDarkThemeId = theme.Id;
			else
				state.LastLightThemeId = theme.Id;

			state.Persist();
		}
	}
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Globalization;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Curve and keyframe analysis for standalone .anim clips and for animation imported from models.</summary>
	public sealed class AnimationAnalyzer : IBackgroundAssetAnalyzer, ISelectiveBackgroundAssetAnalyzer
	{
		static readonly string[] k_ModelExtensions = { ".fbx", ".dae", ".blend", ".ma", ".mb", ".gltf", ".glb" };

		public AssetCategory Category => AssetCategory.Animation;

		public AssetKindMask SupportedKinds => AssetKindMask.Animation | AssetKindMask.Model;

		static AnimationAnalyzer()
		{
			Rules.Register(id: "ANI001", title: "Model animation compression is not optimal", category: AssetCategory.Animation,
							why: "Generic and Humanoid model imports should use Optimal animation compression, while Legacy imports should use Keyframe Reduction and Compression. Using a supported compressed mode reduces redundant keyframes and animation data size; Optimal also allows Unity to choose a compact runtime representation where supported.",
							fix: "Automatically set Generic/Humanoid animation compression to Optimal and Legacy compression to Keyframe Reduction and Compression. The importer setting is verified after reimport.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: true
							);

			Rules.Register(id: "ANI002", title: "Constant scale curves kept on import", category: AssetCategory.Animation,
							why: "Model import can generate scale curves that remain identical to the object's initial scale. Unity can remove those unnecessary curves; this is specifically intended to reduce redundant animation data created by FBX transform conversion.",
							fix: "Enable Remove Constant Scale Curves when constant imported scale curves are observed, and verify the importer setting after reimport.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "ANI003", title: "Animation resampling is disabled for rotation curves", category: AssetCategory.Animation,
							why: "Unity normally resamples imported rotations to quaternions. Keeping the original curves is mainly for cases where the resampled result has interpolation or fidelity problems, so disabling resampling should be a deliberate choice rather than a default optimization.",
							fix: "Enable Resample Curves when Euler rotation curves are observed and verify the importer setting after reimport; revert assets whose resulting rotation quality is unacceptable.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "ANI004", title: "Clip contains non-constant scale animation", category: AssetCategory.Animation,
							why: "Unity's animation guidance notes that scale curves are more expensive than translation and rotation curves. Scale animation is therefore worth flagging when it is not actually required by the asset.",
							fix: "Remove unnecessary scale animation or move fixed sizing into the model/prefab transform where the visual result permits it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "ANI005", title: "Very high keyframe count", category: AssetCategory.Animation,
							why: "A very large key count increases animation asset size and can increase runtime memory and evaluation work. Absolute key count is only a heuristic because Unity can store compressed or dense representations differently.",
							fix: "Trim unused clip ranges, reduce source sampling where appropriate, or use Unity's animation keyframe reduction/compression with a precision budget.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "ANI006", title: "Safe constant curves stored at full length", category: AssetCategory.Animation,
							why: "A genuinely flat float curve does not need dozens of identical keys. When all segments are provably constant, the curve can be represented by its first and last values without changing the sampled result.",
							fix: "Collapse provably constant float curves to two flat keyframes.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "ANI007", title: "Complex Legacy animation clip", category: AssetCategory.Animation,
							why: "Unity's Legacy Animation system is not universally slower: it can be efficient for simple clips, while the modern Animator system is better suited to complex animation, blending and larger curve sets.",
							fix: "For complex Legacy clips, consider migrating to Animator/Mecanim when you need blending or more advanced animation features. Keep Legacy for genuinely simple use cases where it performs well.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "ANI008", title: "Clip drives a very large number of curves", category: AssetCategory.Animation,
							why: "A large number of active animation bindings increases animation data size and evaluation work. This matters most for deep rigs and clips that animate many properties that never affect the visible result.",
							fix: "Remove genuinely unused bindings and use Avatar Masks for animation layers that do not need the full body.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "ANI009", title: "Keys that do not change the animation", category: AssetCategory.Animation,
							why: "Some authored or exported keys are redundant: removing the key can leave the curve within the configured error tolerance. This rule tests the original curve against a candidate curve over the affected span, including the removed key's exact time.",
							fix: "The automatic reducer removes only keys that remain within the configured tolerance, preserves curve wrap modes, backs up the clip, and verifies the rebuilt key count after saving.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "ANI010", title: "Model GameObjects are not optimized for animation", category: AssetCategory.Animation,
							why: "Optimize Game Objects can flatten transform-only parts of an imported character hierarchy, reducing Animator CPU work. It can also break scripts or systems that expect those transforms to exist as GameObjects.",
							fix: "Enable Optimize Game Objects for eligible Generic/Humanoid imports; expose required transform paths or revert the setting on exceptional assets.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "ANI011", title: "Humanoid animation oversampling is high", category: AssetCategory.Animation,
							why: "Humanoid retargeting already resamples animation during import. Oversampling at 4x or 8x can substantially increase the number of sampled keys and the resulting animation data size; Unity documents 4x/8x as options for very fast motions and explicitly cautions that 8x can produce a large output file.",
							fix: "Set Humanoid Oversampling to X1 by default; restore higher sampling on any asset where motion quality requires it.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
							);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			if (assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) return true;
			for (int i = 0; i < k_ModelExtensions.Length; i++)
				if (assetPath.EndsWith(k_ModelExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			return assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
				? AnalyzeStandaloneClip(assetPath, ctx)
				: AnalyzeModelClips(assetPath, ctx);
		}

		[Serializable]
		sealed class AnimationReductionFacts
		{
			public int algorithmVersion;
			public float tolerancePercent;
			public int removedKeys;
			public int reducedCurves;
		}

		const int k_ReductionAlgorithmVersion = 4;

		[Serializable]
		sealed class StandaloneAnimationFacts
		{
			public long runtimeSize;
			public double length;
			public int curves;
			public long keyframes;
			public int frameRate;
			public bool legacy;
			public int discreteCurves;
			public int objectReferenceCurves;
			public int objectReferenceKeys;
			public int animationEvents;
			public int constantCurves;
			public int nonConstantScaleCurves;
		}

        AssetEntry AnalyzeStandaloneClip(string assetPath, ScanContext ctx)
        {
            StandaloneAnimationFacts facts;
            string dependencyHash = ctx.GetDependencyHash(assetPath);
            string factsKey = ScanCache.ScopedKey(assetPath, typeof(StandaloneAnimationFacts).FullName);
            if (ctx.Cache != null && ctx.Cache.TryGet(factsKey, dependencyHash, out StandaloneAnimationFacts cached))
            {
                facts = cached;
            }
            else
            {
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip == null) return null;
                facts = MeasureStandaloneClip(clip, out _, out _);
                if (facts == null) return null;
                ctx.Cache?.Set(factsKey, facts, dependencyHash);
            }

            int redundantKeys = 0;
            using (RuleTiming.Begin("ANI009", assetPath))
            {
                if (ctx.Settings.animationKeyTolerancePercent > 0f && ctx.Settings.IsRuleEnabled("ANI009"))
                {
                    string reductionKey = ReductionFactsCacheKey(assetPath, ctx.Settings.animationKeyTolerancePercent);
                    AnimationReductionFacts reductionFacts = null;
                    if (ctx.Cache == null || !ctx.Cache.TryGet(reductionKey, dependencyHash, out reductionFacts))
                    {
                        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                        if (clip != null)
                        {
                            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                            var sourceCurves = new AnimationCurve[bindings.Length];
                            for (int i = 0; i < bindings.Length; i++)
                                sourceCurves[i] = AnimationUtility.GetEditorCurve(clip, bindings[i]);
                            var reduction = BuildReductionCache(clip, ctx.Settings.animationKeyTolerancePercent, bindings, sourceCurves);
                            CacheReduction(clip, reduction);
                            reductionFacts = new AnimationReductionFacts
                            {
                                algorithmVersion = k_ReductionAlgorithmVersion,
                                tolerancePercent = ctx.Settings.animationKeyTolerancePercent,
                                removedKeys = reduction?.RemovedKeys ?? 0,
                                reducedCurves = reduction?.Curves?.Count ?? 0
                            };
                            ctx.Cache.Set(reductionKey, reductionFacts, dependencyHash);
                        }
                    }
                    redundantKeys = reductionFacts?.removedKeys ?? 0;
                }
            }

            return BuildStandaloneEntry(assetPath, facts, redundantKeys, ctx.Settings, ctx.GetGuid(assetPath),
                ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath));
        }

        public bool SupportsBackground(string assetPath)
            => assetPath != null && assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase);

        public void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext)
        {
            if (!SupportsBackground(assetPath)) return;
            string dependencyHash = backgroundContext.GetDependencyHash(assetPath);
            string factsKey = ScanCache.ScopedKey(assetPath, typeof(StandaloneAnimationFacts).FullName);
            StandaloneAnimationFacts facts = null;
            EditorCurveBinding[] bindings = null;
            AnimationCurve[] sourceCurves = null;

            bool factsHit = mainContext.Cache != null && mainContext.Cache.TryGet(factsKey, dependencyHash, out facts);
            bool needsReduction = mainContext.Settings.animationKeyTolerancePercent > 0f && mainContext.Settings.IsRuleEnabled("ANI009");
            AnimationReductionFacts reductionFacts = null;
            string reductionKey = needsReduction ? ReductionFactsCacheKey(assetPath, mainContext.Settings.animationKeyTolerancePercent) : null;
            bool reductionHit = !needsReduction || (mainContext.Cache != null && mainContext.Cache.TryGet(reductionKey, dependencyHash, out reductionFacts));

            if (!factsHit || (needsReduction && !reductionHit))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip == null) throw new InvalidOperationException("AnimationClip could not be loaded for background preparation: " + assetPath);
                if (!factsHit)
                {
                    facts = MeasureStandaloneClip(clip, out bindings, out sourceCurves);
                    if (facts == null) throw new InvalidOperationException("Animation facts could not be extracted: " + assetPath);
                    backgroundContext.SeedCached(factsKey, facts, persist: true);
                }
                else
                {
                    bindings = AnimationUtility.GetCurveBindings(clip);
                    sourceCurves = new AnimationCurve[bindings.Length];
                    for (int i = 0; i < bindings.Length; i++) sourceCurves[i] = AnimationUtility.GetEditorCurve(clip, bindings[i]);
                }

                if (needsReduction && !reductionHit)
                {
                    var managed = BuildManagedReductionInput(bindings, sourceCurves, mainContext.Settings.animationKeyTolerancePercent);
                    backgroundContext.SeedCached(ReductionInputKey(assetPath), managed);
                }
            }
            else
            {
                backgroundContext.SeedCached(factsKey, facts);
                if (needsReduction) backgroundContext.SeedCached(reductionKey, reductionFacts);
            }

            if (factsHit && !backgroundContext.TryGetFact(factsKey, out StandaloneAnimationFacts _))
                backgroundContext.SeedCached(factsKey, facts);
            if (needsReduction && reductionHit && !backgroundContext.TryGetFact(reductionKey, out AnimationReductionFacts _))
                backgroundContext.SeedCached(reductionKey, reductionFacts);
        }

        public AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context)
        {
            if (!SupportsBackground(assetPath)) return null;
            string factsKey = ScanCache.ScopedKey(assetPath, typeof(StandaloneAnimationFacts).FullName);
            if (!context.TryGetFact(factsKey, out StandaloneAnimationFacts facts))
                throw new InvalidOperationException("Background animation facts were not prepared: " + assetPath);

            int redundantKeys = 0;
            float tolerancePercent = context.Settings.animationKeyTolerancePercent;
            using (RuleTiming.Begin("ANI009", assetPath))
            {
                if (tolerancePercent > 0f && context.Settings.IsRuleEnabled("ANI009"))
                {
                    string reductionKey = ReductionFactsCacheKey(assetPath, tolerancePercent);
                    if (!context.TryGetFact(reductionKey, out AnimationReductionFacts reductionFacts))
                    {
                        if (!context.TryGetFact(ReductionInputKey(assetPath), out List<ManagedAnimationReductionEngine.ManagedCurveWork> managedInput))
                            throw new InvalidOperationException("Background animation reduction data was not prepared: " + assetPath);
                        ManagedAnimationReductionEngine.ManagedReductionResult reduction = ManagedAnimationReductionEngine.ReduceClip(managedInput, tolerancePercent);
                        reductionFacts = new AnimationReductionFacts
                        {
                            algorithmVersion = k_ReductionAlgorithmVersion,
                            tolerancePercent = tolerancePercent,
                            removedKeys = reduction.RemovedKeys,
                            reducedCurves = reduction.Curves.Count
                        };
                        context.SeedCached(reductionKey, reductionFacts, persist: true);
                    }
                    redundantKeys = reductionFacts.removedKeys;
                }
            }

            return BuildStandaloneEntry(assetPath, facts, redundantKeys, context.Settings, context.GetGuid(assetPath), context.GetFileSize(assetPath));
        }

        AssetEntry BuildStandaloneEntry(string assetPath, StandaloneAnimationFacts facts, int redundantKeys, OptimizerSettings settings, string guid, long fileSize)
        {
            if (facts == null) return null;
            var entry = new AssetEntry
            {
                Guid = guid ?? string.Empty,
                Path = assetPath,
                Name = System.IO.Path.GetFileName(assetPath),
                Directory = AssetPathUtility.Directoryish(assetPath),
                Extension = System.IO.Path.GetExtension(assetPath),
                Category = AssetCategory.Animation,
                TypeLabel = "AnimationClip",
                FileSize = fileSize
            };
            entry.RuntimeSize = facts.runtimeSize;
            entry.Set("length", facts.length);
            entry.Set("lengthText", facts.length.ToString("0.00") + " s");
            entry.Set("curves", (long)facts.curves);
            entry.Set("keyframes", facts.keyframes);
            entry.Set("frameRate", (long)facts.frameRate);
            entry.Set("legacy", facts.legacy ? "Yes" : "No");
            entry.Set("discreteCurves", (long)facts.discreteCurves);
            entry.Set("objectReferenceCurves", (long)facts.objectReferenceCurves);
            entry.Set("objectReferenceKeys", (long)facts.objectReferenceKeys);
            entry.Set("animationEvents", (long)facts.animationEvents);

            if (settings.animationKeyTolerancePercent > 0f && settings.IsRuleEnabled("ANI009") && redundantKeys > 0)
            {
                float percentTolerance = settings.animationKeyTolerancePercent;
                long originalKeyCount = facts.keyframes;
                entry.AddIssue("ANI009",
                    "Up to " + redundantKeys + " of " + originalKeyCount + " keys can be reduced",
                    originalKeyCount - redundantKeys + " keys",
                    0,
                    c => ReduceClip((AnimationClip)c.Asset, percentTolerance, c),
                    verify: c => VerifyReducedClip(c.AssetPath, originalKeyCount, redundantKeys))
                    ?.Note("Uses incremental error-priority key reduction with exact cubic error checks where possible, and caches the measured removable-key count against the asset dependency hash.");
            }

            using (RuleTiming.Begin("ANI006", entry.Path))
                if (facts.constantCurves > 0)
                    entry.AddIssue("ANI006", facts.constantCurves + " constant curve(s)", "collapsed to 2 keys", 0,
                        c => CollapseSafeConstantCurves((AnimationClip)c.Asset, c),
                        verify: c => CountUncollapsedSafeConstantCurves(c.AssetPath) == 0)
                        ?.Note("Clip-data path: this edits existing provably constant curves. Compare the imported result with ANI002 before estimating any incremental savings; the two recommendations must not be added as if independent.", "Related rules");

            using (RuleTiming.Begin("ANI005", entry.Path))
                if (facts.keyframes > settings.animationKeyframeWarn)
                    entry.AddIssue("ANI005", DisplayUtility.FormatCount(facts.keyframes) + " keys", "under " + DisplayUtility.FormatCount(settings.animationKeyframeWarn) + " keys");

            using (RuleTiming.Begin("ANI007", entry.Path))
                if (facts.legacy && facts.curves > settings.animationCurveWarn)
                    entry.AddIssue("ANI007", "Legacy clip with " + facts.curves + " curves", "Consider Animator/Mecanim for complex animation", 0, null,
                        "Legacy can be efficient for simple clips; this warning is limited to clips whose curve count suggests a more complex workload.");

            using (RuleTiming.Begin("ANI008", entry.Path))
                if (facts.curves > settings.animationCurveWarn)
                    entry.AddIssue("ANI008", facts.curves + " curves", "under " + settings.animationCurveWarn);

            using (RuleTiming.Begin("ANI004", entry.Path))
                if (facts.nonConstantScaleCurves > 0)
                    entry.AddIssue("ANI004", facts.nonConstantScaleCurves + " non-constant scale curve(s)", "Remove unnecessary scale animation", 0, null,
                        "Scale animation is more expensive than translation/rotation animation; keep it where the rig genuinely requires it.");

            entry.Finalise();
            return entry;
        }

        static string ReductionFactsCacheKey(string assetPath, float tolerancePercent)
            => ScanCache.ScopedKey(assetPath, "AnimationReductionFacts|v" + k_ReductionAlgorithmVersion + "|tol=" + tolerancePercent.ToString("0.######", CultureInfo.InvariantCulture));

        static string ReductionInputKey(string assetPath)
            => ScanCache.ScopedKey(assetPath, "AnimationReductionInput");


		static StandaloneAnimationFacts MeasureStandaloneClip(
			AnimationClip clip,
			out EditorCurveBinding[] measuredBindings,
			out AnimationCurve[] measuredCurves)
		{
			measuredBindings = null;
			measuredCurves = null;
			if (clip == null) return null;

			var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
			int objectReferenceKeys = 0;
			for (int i = 0; i < objectBindings.Length; i++)
			{
				var keys = AnimationUtility.GetObjectReferenceCurve(clip, objectBindings[i]);
				if (keys != null) objectReferenceKeys += keys.Length;
			}

			var facts = new StandaloneAnimationFacts
			{
				runtimeSize = UnityEditorUtility.RuntimeSizeOf(clip),
				length = clip.length,
				frameRate = Mathf.RoundToInt(clip.frameRate),
				legacy = clip.legacy,
				objectReferenceCurves = objectBindings.Length,
				objectReferenceKeys = objectReferenceKeys,
				animationEvents = AnimationUtility.GetAnimationEvents(clip).Length
			};

			var bindings = AnimationUtility.GetCurveBindings(clip);
			facts.curves = bindings.Length;
			var sourceCurves = new AnimationCurve[bindings.Length];
			measuredBindings = bindings;
			measuredCurves = sourceCurves;
			for (int i = 0; i < bindings.Length; i++)
			{
				var binding = bindings[i];
				var curve = AnimationUtility.GetEditorCurve(clip, binding);
				sourceCurves[i] = curve;
				if (curve == null) continue;
				facts.keyframes += curve.length;
				if (binding.isDiscreteCurve)
				{
					facts.discreteCurves++;
					continue;
				}
				if (binding.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal) && !IsConstant(curve, out _))
					facts.nonConstantScaleCurves++;
				if (IsConstant(curve, out _) && curve.length > 2 && IsSafeConstantCurve(curve))
					facts.constantCurves++;
			}
			return facts;
		}

		static void CollapseSafeConstantCurves(AnimationClip target, FixContext context)
		{
			if (target == null) throw new InvalidOperationException("AnimationClip could not be loaded for constant-curve reduction.");
			context.EnsureWritable();
			context.RequireBackup(context.AssetPath);
			var bindings = GetSafeConstantCurveBindings(target);
			var batchBindings = new List<EditorCurveBinding>(bindings.Count);
			var batchCurves = new List<AnimationCurve>(bindings.Count);
			for (int i = 0; i < bindings.Count; i++)
			{
				var binding = bindings[i];
				var curve = AnimationUtility.GetEditorCurve(target, binding);
				if (curve == null || curve.length < 3) continue;
				var first = curve[0];
				var last = curve[curve.length - 1];
				var collapsed = AnimationCurve.Constant(first.time, last.time, first.value);
				collapsed.preWrapMode = curve.preWrapMode;
				collapsed.postWrapMode = curve.postWrapMode;
				batchBindings.Add(binding);
				batchCurves.Add(collapsed);
			}
			ApplyCurveBatch(target, batchBindings, batchCurves);
			if (batchBindings.Count == 0) throw new InvalidOperationException("No constant curve could be collapsed.");
			context.NeedsSave = true;
		}

		static List<EditorCurveBinding> GetSafeConstantCurveBindings(AnimationClip clip)
		{
			var result = new List<EditorCurveBinding>();
			if (clip == null) return result;
			foreach (var binding in AnimationUtility.GetCurveBindings(clip))
			{
				var curve = AnimationUtility.GetEditorCurve(clip, binding);
				if (curve != null && curve.length > 2 && !binding.isDiscreteCurve
					&& IsConstant(curve, out _) && IsSafeConstantCurve(curve))
					result.Add(binding);
			}
			return result;
		}

		static int CountUncollapsedSafeConstantCurves(string assetPath)
		{
			var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
			if (clip == null) return int.MaxValue;
			return GetSafeConstantCurveBindings(clip).Count;
		}

		/// <summary>
		/// Total keyframes across every curve in a clip. Used to verify that a reduction actually
		/// removed keys: the reduction is allowed to remove fewer than it predicted - the plan is
		/// recomputed against the live curves - but a run that removed none did nothing.
		/// </summary>
		static long CountKeys(AnimationClip clip)
		{
			if (clip == null) return long.MaxValue;

			long total = 0;
			foreach (var binding in AnimationUtility.GetCurveBindings(clip))
			{
				var curve = AnimationUtility.GetEditorCurve(clip, binding);
				if (curve != null) total += curve.length;
			}
			return total;
		}

		AssetEntry AnalyzeModelClips(string assetPath, ScanContext ctx)
		{
			var facts = ModelAnalysisShared.GetAnimation(assetPath, ctx);
			if (facts == null || !facts.importAnimation || facts.clipCount == 0) return null;

			long keyCount = facts.animationKeyCount;
			long curveCount = facts.animationCurveCount;
			long constantScaleCurves = facts.constantScaleCurves;
			long nonConstantScaleCurves = facts.nonConstantScaleCurves;
			long discreteCurveCount = facts.discreteCurveCount;
			long objectReferenceCurveCount = facts.objectReferenceCurveCount;
			long objectReferenceKeyCount = facts.objectReferenceKeyCount;
			long animationEventCount = facts.animationEventCount;
			int maxClipCurveCount = facts.maxClipCurveCount;
			bool hasEulerRotationCurves = facts.hasEulerRotationCurves;
			int clipCount = facts.clipCount;
			double totalLength = facts.totalAnimationLength;
			var entry = NewEntry(assetPath, "Model clips", ctx);
			entry.Set("length", totalLength);
			entry.Set("lengthText", totalLength.ToString("0.00") + " s");
			entry.Set("curves", curveCount);
			entry.Set("keyframes", keyCount);
			entry.Set("clips", (long)clipCount);
			entry.Set("compression", facts.animationCompression.ToString());
			entry.Set("animationType", facts.animationType.ToString());
			entry.Set("optimizeGameObjects", facts.optimizeGameObjects ? "On" : "Off");
			entry.Set("discreteCurves", discreteCurveCount);
			entry.Set("objectReferenceCurves", objectReferenceCurveCount);
			entry.Set("objectReferenceKeys", objectReferenceKeyCount);
			entry.Set("animationEvents", animationEventCount);
			entry.Set("humanoidOversampling", facts.humanoidOversampling.ToString());

			{
				bool optimalSupported =
					facts.animationType == ModelImporterAnimationType.Generic
					|| facts.animationType == ModelImporterAnimationType.Human;

				ModelImporterAnimationCompression recommended;
				bool needsCompression;

				if (optimalSupported)
				{
					recommended = ModelImporterAnimationCompression.Optimal;
					// ANI001 enforces the registered target compression mode for the model animation type.
					// Generic/Humanoid imports target Optimal; Legacy imports target Keyframe Reduction and Compression.
					needsCompression = facts.animationCompression != recommended;
				}
				else
				{
					recommended = ModelImporterAnimationCompression.KeyframeReductionAndCompression;
					needsCompression = facts.animationCompression != recommended;
				}

				using (RuleTiming.Begin("ANI001", entry.Path))
					if (needsCompression)
					{
						entry.AddIssue("ANI001", facts.animationCompression.ToString(), recommended.ToString(), 0,
							c => ApplyModelImporterChange(c, mi => mi.animationCompression = recommended),
							"Use the deterministic compression target for this importer animation type; verify the setting after reimport.",
							verify: c => VerifyModelImporterSetting(c, mi => mi.animationCompression == recommended, requireClips: true));
					}
			}

			// Unity removes constant scale curves only when their value equals the object's initial scale.
			// Do not flag the importer setting merely because it is Off: without observed constant scale
			// curves in the imported clips, enabling it has no demonstrated benefit for this asset.
			using (RuleTiming.Begin("ANI002", entry.Path))
				if (!facts.removeConstantScaleCurves && constantScaleCurves > 0)
				{
					entry.AddIssue("ANI002", "Off / " + constantScaleCurves + " constant scale curve(s)", "On", 0,
						c => ApplyModelImporterChange(c, mi => mi.removeConstantScaleCurves = true),
						"Unity documents that this option removes constant animation curves whose values match the object's initial scale. "
						+ "This finding is emitted only when constant scale curves were actually observed in the imported clips.",
						verify: VerifyConstantScaleCurvesRemoved)
						.Note("Import-stage path: this changes importer output on reimport. ANI006 edits constant curves already present on standalone clips; compare the post-import result before estimating savings so overlapping curve reduction is not counted twice.", "Related rules");
				}

			using (RuleTiming.Begin("ANI003", entry.Path))
				if (!facts.resampleCurves
					&& hasEulerRotationCurves
					&& facts.animationType != ModelImporterAnimationType.Human)
				{
					entry.AddIssue("ANI003", "Off with Euler rotation curves", "On", 0,
						c => ApplyModelImporterChange(c, mi => mi.resampleCurves = true),
						verify: c => VerifyModelImporterSetting(c, mi => mi.resampleCurves, requireClips: true));
				}

			using (RuleTiming.Begin("ANI005", entry.Path))
				if (keyCount > ctx.Settings.animationKeyframeWarn)
				{
					entry.AddIssue("ANI005", DisplayUtility.FormatCount(keyCount) + " keys across " + clipCount + " clip(s)",
						"under " + DisplayUtility.FormatCount(ctx.Settings.animationKeyframeWarn) + " keys");
				}

			using (RuleTiming.Begin("ANI007", entry.Path))
				if (facts.animationType == ModelImporterAnimationType.Legacy
					&& maxClipCurveCount > ctx.Settings.animationCurveWarn)
				{
					entry.AddIssue("ANI007", "Legacy clip with " + maxClipCurveCount + " curves",
						"Consider Animator/Mecanim for complex animation", 0, null,
						"Legacy can be efficient for simple clips; this warning is limited to complex imported clips.");
				}

			using (RuleTiming.Begin("ANI004", entry.Path))
				if (nonConstantScaleCurves > 0)
				{
					entry.AddIssue("ANI004", nonConstantScaleCurves + " non-constant scale curve(s)",
						"Remove unnecessary scale animation", 0, null,
						"Scale animation costs more than translation/rotation animation; keep it where the rig genuinely requires it.");
				}

			var overlapSettings = ctx.Settings ?? OptimizerSettings.Instance;
			bool mdl015OwnsSkinnedModelOverlap = facts.hasSkinnedMesh
				&& !facts.optimizeGameObjects
				&& overlapSettings != null
				&& overlapSettings.IsRuleEnabled("MDL015")
				&& !overlapSettings.IsSuppressed("MDL015", entry.Path);
			using (RuleTiming.Begin("ANI010", entry.Path))
				if (!mdl015OwnsSkinnedModelOverlap
					&& !facts.optimizeGameObjects
					&& (facts.animationType == ModelImporterAnimationType.Generic || facts.animationType == ModelImporterAnimationType.Human)
					&& facts.transformPathCount > 16
					&& curveCount > Mathf.Max(16, ctx.Settings.animationCurveWarn / 2))
					entry.AddIssue("ANI010", "Off / " + facts.transformPathCount + " transforms / " + curveCount + " curves", "On", 0,
						c => ApplyModelImporterChange(c, mi => mi.optimizeGameObjects = true),
						"Enable Optimize Game Objects for non-trivial animated hierarchies. MDL015 retains ownership of skinned-model overlap; if a runtime consumer needs a stripped transform, expose it or revert this importer setting.",
						verify: c => VerifyModelImporterSetting(c, mi => mi.optimizeGameObjects, requireClips: true));

			using (RuleTiming.Begin("ANI011", entry.Path))
				if (facts.animationType == ModelImporterAnimationType.Human
					&& (facts.humanoidOversampling == ModelImporterHumanoidOversampling.X4
						|| facts.humanoidOversampling == ModelImporterHumanoidOversampling.X8))
				{
					entry.Set("humanoidOversampling", facts.humanoidOversampling.ToString());
					entry.AddIssue("ANI011", facts.humanoidOversampling.ToString(), "X1", 0,
						c => ApplyModelImporterChange(c, mi => SetHumanoidOversamplingX1(mi)),
						"Set high Humanoid oversampling to X1 by default. Verify the imported setting; restore higher sampling on an asset where motion quality requires it.",
						verify: c => VerifyModelImporterSetting(c, mi => IsHumanoidOversamplingX1(mi), requireClips: true));
				}

			entry.Finalise();
			return entry;
		}

		static bool IsEulerRotationProperty(string propertyName)
		{
			if (string.IsNullOrEmpty(propertyName)) return false;
			return propertyName.IndexOf("m_LocalEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
				|| propertyName.IndexOf("localEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool MatchesImportedInitialScale(string assetPath, EditorCurveBinding binding, float value)
		{
			char component = GetScaleComponent(binding.propertyName);
			if (component == '\0') return false;

			GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (root == null) return false;

			Transform target = string.IsNullOrEmpty(binding.path)
				? root.transform
				: root.transform.Find(binding.path);
			if (target == null) return false;

			float initial = component == 'x'
				? target.localScale.x
				: component == 'y'
					? target.localScale.y
					: target.localScale.z;
			return Mathf.Approximately(initial, value);
		}

		static char GetScaleComponent(string propertyName)
		{
			if (string.IsNullOrEmpty(propertyName)) return '\0';
			if (propertyName.EndsWith(".x", StringComparison.OrdinalIgnoreCase)) return 'x';
			if (propertyName.EndsWith(".y", StringComparison.OrdinalIgnoreCase)) return 'y';
			if (propertyName.EndsWith(".z", StringComparison.OrdinalIgnoreCase)) return 'z';
			return '\0';
		}

		static int CountObjectReferenceKeys(AnimationClip clip)
		{
			if (clip == null) return 0;
			int total = 0;
			EditorCurveBinding[] bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
			for (int i = 0; i < bindings.Length; i++)
			{
				ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, bindings[i]);
				if (keys != null) total += keys.Length;
			}
			return total;
		}

		static AssetEntry NewEntry(string assetPath, string typeLabel, ScanContext ctx)
		{
			return new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.Animation,
				TypeLabel = typeLabel,
				FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath)
			};
		}

		// --- keyframe reduction ---------------------------------------------------------

		enum CurveKind
		{
			Generic,
			Position,
			Rotation,
			Scale
		}

		/// <summary>Prepared reduction result for one float curve.</summary>
		sealed class CurveReductionPlan
		{
			public List<int> RemovedIndices;
			public Keyframe[] NewKeys;
			public AnimationUtility.TangentMode[] LeftModes;
			public AnimationUtility.TangentMode[] RightModes;
			public bool[] Broken;
			public float[] InTangents;
			public float[] OutTangents;
			public float ErrorTolerance;
		}

		sealed class CurveWork
		{
			public EditorCurveBinding Binding;
			public AnimationCurve Original;
			public AnimationCurve Reduced;
			public CurveKind Kind;
			public float Tolerance;
			public float RelativeRatio;
		}

		sealed class CachedClipReduction
		{
			public int Signature;
			public float TolerancePercent;
			public int RemovedKeys;
			public List<CachedCurveReduction> Curves;
		}

		sealed class CachedCurveReduction
		{
			public string BindingKey;
			public AnimationCurve Reduced;
		}

		/// <summary>One lazily-evaluated removal candidate in the min-heap.</summary>
		struct ReductionCandidate : IComparable<ReductionCandidate>
		{
			public int Index;
			public int Version;
			public float Priority;
			public bool Exact;

			public ReductionCandidate(int index, int version, float priority, bool exact)
			{
				Index = index;
				Version = version;
				Priority = priority;
				Exact = exact;
			}

			public int CompareTo(ReductionCandidate other)
			{
				int c = Priority.CompareTo(other.Priority);
				if (c != 0) return c;
				// Exact candidates win ties so they can terminate the search quickly.
				if (Exact != other.Exact) return Exact ? -1 : 1;
				return Index.CompareTo(other.Index);
			}
		}

		static readonly Dictionary<string, CachedClipReduction> s_ReductionCache =
			new Dictionary<string, CachedClipReduction>(StringComparer.Ordinal);

		sealed class MinHeap
		{
			readonly List<ReductionCandidate> m_Items = new List<ReductionCandidate>();

			public int Count => m_Items.Count;

			public void Push(ReductionCandidate item)
			{
				int i = m_Items.Count;
				m_Items.Add(item);
				while (i > 0)
				{
					int parent = (i - 1) >> 1;
					if (m_Items[parent].CompareTo(m_Items[i]) <= 0) break;
					Swap(parent, i);
					i = parent;
				}
			}

			public ReductionCandidate Pop()
			{
				if (m_Items.Count == 0) throw new InvalidOperationException("Heap is empty.");
				ReductionCandidate result = m_Items[0];
				int last = m_Items.Count - 1;
				if (last == 0)
				{
					m_Items.RemoveAt(0);
					return result;
				}

				m_Items[0] = m_Items[last];
				m_Items.RemoveAt(last);
				int i = 0;
				while (true)
				{
					int left = (i << 1) + 1;
					if (left >= m_Items.Count) break;
					int right = left + 1;
					int best = left;
					if (right < m_Items.Count && m_Items[right].CompareTo(m_Items[left]) < 0)
						best = right;
					if (m_Items[i].CompareTo(m_Items[best]) <= 0) break;
					Swap(i, best);
					i = best;
				}
				return result;
			}

			void Swap(int a, int b)
			{
				ReductionCandidate temp = m_Items[a];
				m_Items[a] = m_Items[b];
				m_Items[b] = temp;
			}
		}

		struct Cubic
		{
			public float C0, C1, C2, C3;
		}

		struct SpanTestResult
		{
			public bool Valid;
			public bool UsedRefit;
			public float OutTangent;
			public float InTangent;
			public float MaxError;
		}

		sealed class SharedKeyCandidate
		{
			public float Time;
			public int[] Indices;
		}

		struct ReductionData
		{
			public Keyframe[] Keys;
			public AnimationUtility.TangentMode[] LeftModes;
			public AnimationUtility.TangentMode[] RightModes;
			public bool[] Broken;
			public int[] StepPrefix;
			public int[] WeightedPrefix;
		}

		const int k_FallbackValidationSteps = 24;
		const int k_FallbackMaxValidationDepth = 8;
		const int k_FitSteps = 24;
		const float k_TimeEpsilon = 1e-5f;
		const float k_ErrorComfortRatio = 0.25f;

		/// <summary>
		/// Legacy public helper retained for compatibility.
		/// Uses an incremental error-priority reducer instead of the old all-pairs shortest path.
		/// </summary>
		public static AnimationCurve ReduceCurve(AnimationCurve curve, float tolerance, out int removed)
		{
			var plan = ComputeReductionPlan(curve, Mathf.Max(0f, tolerance), true);
			removed = plan?.RemovedIndices?.Count ?? 0;
			return plan == null || removed == 0 ? null : BuildCurveFromPlan(curve, plan);
		}

		static CurveKind GetCurveKind(EditorCurveBinding binding)
		{
			string property = binding.propertyName ?? string.Empty;
			if (property.StartsWith("m_LocalPosition", StringComparison.Ordinal)) return CurveKind.Position;
			if (property.StartsWith("m_LocalScale", StringComparison.Ordinal)) return CurveKind.Scale;
			if (property.IndexOf("LocalRotation", StringComparison.OrdinalIgnoreCase) >= 0
				|| property.IndexOf("m_LocalEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
				|| property.IndexOf("localEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0)
				return CurveKind.Rotation;
			return CurveKind.Generic;
		}

		static float ToleranceFor(EditorCurveBinding binding, AnimationCurve curve, float percent)
		{
			float ratio = Mathf.Max(0f, percent) * 0.01f;
			if (ratio <= 0f) return 0f;

			CurveKind kind = GetCurveKind(binding);
			if (kind == CurveKind.Rotation)
				return Mathf.Max(0.001f, 360f * ratio);

			Keyframe[] keys = curve.keys;
			float maxAbs = 0f, min = float.MaxValue, max = float.MinValue;
			for (int i = 0; i < keys.Length; i++)
			{
				float v = keys[i].value;
				float a = Mathf.Abs(v);
				if (a > maxAbs) maxAbs = a;
				if (v < min) min = v;
				if (v > max) max = v;
			}

			if (kind == CurveKind.Position || kind == CurveKind.Scale)
				return Mathf.Max(1e-7f, Mathf.Max(maxAbs, 1e-3f) * ratio);

			float range = max - min;
			if (!(range > 0f) || float.IsNaN(range) || float.IsInfinity(range))
				range = Mathf.Max(maxAbs, 1f);
			return Mathf.Max(1e-7f, range * ratio);
		}

		struct CandidateEvaluation
		{
			public bool Valid;
			public bool UsedRefit;
			public float Error;
			public float OutTangent;
			public float InTangent;
		}

		sealed class ActiveCurveState
		{
			public readonly Keyframe[] Keys;
			public readonly AnimationUtility.TangentMode[] LeftModes;
			public readonly AnimationUtility.TangentMode[] RightModes;
			public readonly bool[] Broken;
			public readonly int[] Previous;
			public readonly int[] Next;
			public readonly int[] Versions;
			public readonly bool[] Alive;
			public readonly float[] EffectiveIn;
			public readonly float[] EffectiveOut;
			public readonly bool[] RefittedIn;
			public readonly bool[] RefittedOut;
			public readonly bool[] WeightedBoundary;
			public readonly bool[] SteppedBoundary;
			public readonly Cubic[] SourceCubics;

			public ActiveCurveState(AnimationCurve curve)
			{
				Keys = curve.keys;
				int n = Keys.Length;
				LeftModes = new AnimationUtility.TangentMode[n];
				RightModes = new AnimationUtility.TangentMode[n];
				Broken = new bool[n];
				Previous = new int[n];
				Next = new int[n];
				Versions = new int[n];
				Alive = new bool[n];
				EffectiveIn = new float[n];
				EffectiveOut = new float[n];
				RefittedIn = new bool[n];
				RefittedOut = new bool[n];
				WeightedBoundary = new bool[Mathf.Max(0, n - 1)];
				SteppedBoundary = new bool[Mathf.Max(0, n - 1)];
				SourceCubics = new Cubic[Mathf.Max(0, n - 1)];

				for (int i = 0; i < n; i++)
				{
					LeftModes[i] = AnimationUtility.GetKeyLeftTangentMode(curve, i);
					RightModes[i] = AnimationUtility.GetKeyRightTangentMode(curve, i);
					Broken[i] = AnimationUtility.GetKeyBroken(curve, i);
					Previous[i] = i - 1;
					Next[i] = i + 1 < n ? i + 1 : -1;
					Alive[i] = true;
					EffectiveIn[i] = Keys[i].inTangent;
					EffectiveOut[i] = Keys[i].outTangent;
				}
				Previous[0] = -1;
				if (n > 0) Next[n - 1] = -1;

				for (int i = 0; i < n - 1; i++)
				{
					SteppedBoundary[i] = RightModes[i] == AnimationUtility.TangentMode.Constant
						|| LeftModes[i + 1] == AnimationUtility.TangentMode.Constant;
					WeightedBoundary[i] = IsWeightedOut(Keys[i]) || IsWeightedIn(Keys[i + 1]);
					SourceCubics[i] = CubicFromKeyPair(Keys[i], Keys[i + 1]);
				}
			}
		}

		static CurveReductionPlan ComputeReductionPlan(AnimationCurve curve, float tolerance, bool allowTangentRefit)
		{
			if (curve == null || curve.length < 3 || tolerance <= 0f || HasNonFiniteKeys(curve))
				return null;

			// Common authored curves need no priority-queue machinery. A constant or straight linear
			// curve can collapse to its endpoints after a single exact validation.
			if (IsEffectivelyLinearCurve(curve, tolerance, out _))
			{
				Keyframe[] keys = curve.keys;
				var directKept = new List<Keyframe>(2) { keys[0], keys[keys.Length - 1] };
				var directPlan = BuildDirectPlan(curve, directKept, tolerance);
				if (directPlan != null) return directPlan;
			}

			ActiveCurveState state = new ActiveCurveState(curve);
			int n = state.Keys.Length;
			var heap = new MinHeap();

			// The initial heap contains cheap lower bounds. Expensive exact span errors are evaluated
			// lazily only when a candidate reaches the top of the heap. This is the key performance
			// improvement over the former all-pairs search.
			for (int i = 1; i < n - 1; i++)
				QueueRemovalCandidate(curve, state, i, tolerance, allowTangentRefit, heap, false);

			var removed = new List<int>(Mathf.Min(n / 2, n - 2));

			while (heap.Count > 0)
			{
				ReductionCandidate candidate = heap.Pop();
				int index = candidate.Index;
				if (!IsCurrentCandidate(candidate, state))
					continue;

				// A non-exact priority is only a lower bound. Resolve it once before making a decision.
				if (!candidate.Exact)
				{
					QueueRemovalCandidate(curve, state, index, tolerance, allowTangentRefit, heap, true);
					continue;
				}

				// Because the heap contains the current minimum exact/lower-bound cost, once this is
				// above tolerance no remaining candidate can be safely removed at the current state.
				if (candidate.Priority > tolerance)
					break;

				int left = state.Previous[index];
				int right = state.Next[index];
				if (left < 0 || right < 0)
					continue;

				CandidateEvaluation evaluation = EvaluateRemoval(curve, state, index, left, right,
					tolerance, allowTangentRefit, true);
				if (!evaluation.Valid || evaluation.Error > tolerance)
				{
					// This should be rare because the candidate was exact when queued, but keeping the
					// defensive path makes stale/edge-state transitions safe.
					continue;
				}

				state.Alive[index] = false;
				state.Previous[right] = left;
				state.Next[left] = right;
				removed.Add(index);

				// The newly created edge owns its endpoint tangents. Only those two tangent directions
				// change; the opposite sides of the endpoint keys remain untouched.
				state.EffectiveOut[left] = state.Keys[left].outTangent;
				state.EffectiveIn[right] = state.Keys[right].inTangent;
				state.RefittedOut[left] = false;
				state.RefittedIn[right] = false;
				if (evaluation.UsedRefit)
				{
					state.EffectiveOut[left] = evaluation.OutTangent;
					state.EffectiveIn[right] = evaluation.InTangent;
					state.RefittedOut[left] = true;
					state.RefittedIn[right] = true;
				}

				// Only candidates adjacent to the removed key have changed neighborhoods.
				if (left > 0 && state.Alive[left])
				{
					state.Versions[left]++;
					QueueRemovalCandidate(curve, state, left, tolerance, allowTangentRefit, heap, false);
				}
				if (right < n - 1 && state.Alive[right])
				{
					state.Versions[right]++;
					QueueRemovalCandidate(curve, state, right, tolerance, allowTangentRefit, heap, false);
				}
			}

			if (removed.Count == 0)
				return null;

			var kept = new List<int>(n - removed.Count);
			for (int i = 0; i < n; i++)
				if (state.Alive[i]) kept.Add(i);
			if (kept.Count >= n)
				return null;

			var newKeys = new Keyframe[kept.Count];
			var leftModes = new AnimationUtility.TangentMode[kept.Count];
			var rightModes = new AnimationUtility.TangentMode[kept.Count];
			var broken = new bool[kept.Count];
			var inTangents = new float[kept.Count];
			var outTangents = new float[kept.Count];
			for (int i = 0; i < kept.Count; i++)
			{
				int source = kept[i];
				newKeys[i] = state.Keys[source];
				leftModes[i] = state.LeftModes[source];
				rightModes[i] = state.RightModes[source];
				broken[i] = state.Broken[source];
				inTangents[i] = state.EffectiveIn[source];
				outTangents[i] = state.EffectiveOut[source];
				if (state.RefittedIn[source])
				{
					leftModes[i] = AnimationUtility.TangentMode.Free;
					broken[i] = true;
				}
				if (state.RefittedOut[source])
				{
					rightModes[i] = AnimationUtility.TangentMode.Free;
					broken[i] = true;
				}
			}

			for (int i = 0; i < newKeys.Length; i++)
			{
				newKeys[i].inTangent = inTangents[i];
				newKeys[i].outTangent = outTangents[i];
			}

			removed.Sort();
			var plan = new CurveReductionPlan
			{
				RemovedIndices = removed,
				NewKeys = newKeys,
				LeftModes = leftModes,
				RightModes = rightModes,
				Broken = broken,
				InTangents = inTangents,
				OutTangents = outTangents,
				ErrorTolerance = tolerance
			};

			AnimationCurve rebuilt = BuildCurveFromPlan(curve, plan);
			return CurveStaysWithinTolerance(curve, rebuilt, tolerance) ? plan : null;
		}

		static bool IsEffectivelyLinearCurve(AnimationCurve curve, float tolerance, out float maxError)
		{
			maxError = 0f;
			if (curve == null || curve.length < 3) return false;
			Keyframe[] keys = curve.keys;
			for (int i = 0; i < keys.Length - 1; i++)
			{
				if (keys[i].weightedMode != WeightedMode.None || keys[i + 1].weightedMode != WeightedMode.None) return false;
				if (AnimationUtility.GetKeyLeftTangentMode(curve, i) == AnimationUtility.TangentMode.Constant
					|| AnimationUtility.GetKeyRightTangentMode(curve, i) == AnimationUtility.TangentMode.Constant
					|| AnimationUtility.GetKeyLeftTangentMode(curve, i + 1) == AnimationUtility.TangentMode.Constant) return false;
				float span = keys[i + 1].time - keys[i].time;
				if (span <= k_TimeEpsilon) return false;
			}
			Keyframe a = keys[0], b = keys[keys.Length - 1];
			float spanAll = b.time - a.time;
			if (spanAll <= k_TimeEpsilon) return false;
			float slope = (b.value - a.value) / spanAll;
			for (int i = 1; i < keys.Length - 1; i++)
			{
				float expected = a.value + slope * (keys[i].time - a.time);
				maxError = Mathf.Max(maxError, Mathf.Abs(expected - keys[i].value));
				if (maxError > tolerance) return false;
			}
			return true;
		}

		static CurveReductionPlan BuildDirectPlan(AnimationCurve curve, List<Keyframe> kept, float tolerance)
		{
			if (curve == null || kept == null || kept.Count != 2) return null;
			Keyframe[] source = curve.keys;
			Keyframe[] newKeys = kept.ToArray();
			float span = newKeys[1].time - newKeys[0].time;
			if (span <= k_TimeEpsilon) return null;
			float slope = (newKeys[1].value - newKeys[0].value) / span;
			newKeys[0].outTangent = slope;
			newKeys[1].inTangent = slope;
			var test = new AnimationCurve(newKeys);
			if (!CurveStaysWithinTolerance(curve, test, tolerance)) return null;
			var removed = new List<int>(Mathf.Max(0, source.Length - 2));
			for (int i = 1; i < source.Length - 1; i++) removed.Add(i);
			return new CurveReductionPlan
			{
				RemovedIndices = removed, NewKeys = newKeys,
				LeftModes = new[] { AnimationUtility.TangentMode.Free, AnimationUtility.TangentMode.Free },
				RightModes = new[] { AnimationUtility.TangentMode.Free, AnimationUtility.TangentMode.Free },
				Broken = new[] { true, true },
				InTangents = new[] { 0f, slope },
				OutTangents = new[] { slope, 0f },
				ErrorTolerance = tolerance
			};
		}

		static bool IsCurrentCandidate(ReductionCandidate candidate, ActiveCurveState state)
		{
			int index = candidate.Index;
			return index > 0 && index < state.Keys.Length - 1
				&& state.Alive[index]
				&& candidate.Version == state.Versions[index];
		}

		static void QueueRemovalCandidate(AnimationCurve original, ActiveCurveState state, int index,
			float tolerance, bool allowTangentRefit, MinHeap heap, bool forceExact)
		{
			if (index <= 0 || index >= state.Keys.Length - 1 || !state.Alive[index]) return;
			int left = state.Previous[index], right = state.Next[index];
			if (left < 0 || right < 0) return;
			if (HasForbiddenBoundary(state, left, right)) return;

			CandidateEvaluation fast = EvaluateRemoval(original, state, index, left, right,
				tolerance, allowTangentRefit: false, exact: forceExact);
			if (forceExact)
			{
				CandidateEvaluation best = fast;
				if (allowTangentRefit && fast.Error > tolerance && CanRefit(state, left, right))
				{
					CandidateEvaluation refit = EvaluateRemoval(original, state, index, left, right,
						tolerance, allowTangentRefit: true, exact: true);
					if (refit.Error < best.Error)
						best = refit;
				}
				heap.Push(new ReductionCandidate(index, state.Versions[index], best.Error, true));
				return;
			}

			// Lower-bound phase: exact error is not required. The removed key itself plus three
			// normalized interior samples provide a cheap bound. It can never underestimate the
			// true error because every measured sample is a real point on the candidate span.
			float lowerBound = EstimateRemovalLowerBound(state, left, right, index);
			heap.Push(new ReductionCandidate(index, state.Versions[index], lowerBound, false));
		}

		static bool HasForbiddenBoundary(ActiveCurveState state, int left, int right)
		{
			for (int i = left; i < right && i < state.SteppedBoundary.Length; i++)
				if (state.SteppedBoundary[i] || state.WeightedBoundary[i]) return true;
			return false;
		}

		static float EstimateRemovalLowerBound(ActiveCurveState state, int left, int right, int removedIndex)
		{
			Keyframe a = GetEffectiveKey(state.Keys[left], state.EffectiveIn, state.EffectiveOut, left);
			Keyframe b = GetEffectiveKey(state.Keys[right], state.EffectiveIn, state.EffectiveOut, right);
			float span = b.time - a.time;
			if (span <= k_TimeEpsilon) return float.PositiveInfinity;
			Cubic candidate = CubicFromKeyPair(a, b);
			float bound = Mathf.Abs(EvaluateCubic(candidate, (state.Keys[removedIndex].time - a.time) / span)
				- state.Keys[removedIndex].value);
			for (int s = 1; s <= 3; s++)
			{
				float u = s * 0.25f;
				float time = a.time + span * u;
				float source = EvaluateKeyArray(state.Keys, left, right, time);
				bound = Mathf.Max(bound, Mathf.Abs(EvaluateCubic(candidate, u) - source));
			}
			return bound;
		}

		static CandidateEvaluation EvaluateRemoval(AnimationCurve original, ActiveCurveState state,
			int removedIndex, int left, int right, float tolerance, bool allowTangentRefit, bool exact)
		{
			CandidateEvaluation result = new CandidateEvaluation
			{
				Error = float.PositiveInfinity,
				OutTangent = state.EffectiveOut[left],
				InTangent = state.EffectiveIn[right]
			};

			if (!exact)
			{
				result.Error = EstimateRemovalLowerBound(state, left, right, removedIndex);
				return result;
			}

			Keyframe candidateLeft = GetEffectiveKey(state.Keys[left], state.EffectiveIn, state.EffectiveOut, left);
			Keyframe candidateRight = GetEffectiveKey(state.Keys[right], state.EffectiveIn, state.EffectiveOut, right);
			if (AnalyticSpanError(state.Keys, state.SourceCubics, left, right, candidateLeft, candidateRight, tolerance,
				out float directError))
			{
				result.Valid = directError <= tolerance;
				result.Error = directError;
				// Once the authored-tangent version already satisfies the error budget, do not
				// perturb tangents merely to shave a little more numerical error. Preserving the
				// authored tangent representation is the safer and more stable result.
				if (result.Valid || !allowTangentRefit)
					return result;
			}
			else
			{
				// AnalyticSpanError returns false both for an out-of-tolerance edge and for invalid
				// spans; directError still contains the measured maximum in the former case.
				result.Error = directError;
			}

			if (!allowTangentRefit || !CanRefit(state, left, right))
				return result;
			if (!TryFitTangentsFast(original, state.Keys, left, right, out float fitOut, out float fitIn))
				return result;

			Keyframe fittedLeft = WithOutTangent(candidateLeft, fitOut);
			Keyframe fittedRight = WithInTangent(candidateRight, fitIn);
			bool fitValid = AnalyticSpanError(state.Keys, state.SourceCubics, left, right, fittedLeft, fittedRight, tolerance,
				out float fitError);
			if (fitError < result.Error)
			{
				result.Error = fitError;
				result.OutTangent = fitOut;
				result.InTangent = fitIn;
				result.UsedRefit = true;
				result.Valid = fitValid && fitError <= tolerance;
			}
			return result;
		}

		static bool CanRefit(ActiveCurveState state, int left, int right)
		{
			if (left < 0 || right <= left) return false;
			if (HasForbiddenBoundary(state, left, right)) return false;
			return !float.IsInfinity(state.Keys[left].outTangent)
				&& !float.IsInfinity(state.Keys[right].inTangent);
		}

		static bool IsWeightedOut(Keyframe key)
		{
			return key.weightedMode == WeightedMode.Out || key.weightedMode == WeightedMode.Both;
		}

		static bool IsWeightedIn(Keyframe key)
		{
			return key.weightedMode == WeightedMode.In || key.weightedMode == WeightedMode.Both;
		}

		static Keyframe GetEffectiveKey(Keyframe source, float[] effectiveIn, float[] effectiveOut, int index)
		{
			source.inTangent = effectiveIn[index];
			source.outTangent = effectiveOut[index];
			return source;
		}

		static bool AnalyticSpanError(Keyframe[] keys, Cubic[] sourceCubics, int start, int end,
			Keyframe candidateStart, Keyframe candidateEnd, float tolerance, out float maxError)
		{
			maxError = 0f;
			float candidateSpan = candidateEnd.time - candidateStart.time;
			if (candidateSpan <= k_TimeEpsilon)
			{
				maxError = float.PositiveInfinity;
				return false;
			}

			Cubic candidate = CubicFromKeyPair(candidateStart, candidateEnd);
			for (int k = start + 1; k < end; k++)
			{
				float u = (keys[k].time - candidateStart.time) / candidateSpan;
				float error = Mathf.Abs(EvaluateCubic(candidate, u) - keys[k].value);
				if (error > maxError) maxError = error;
				if (error > tolerance) return false;
			}

			// Every source interval in the proposed span is itself a cubic. The difference between
			// two cubics is cubic, so its exact maximum can be found from the quadratic derivative.
			for (int s = start; s < end; s++)
			{
				float t0 = keys[s].time, t1 = keys[s + 1].time;
				if (t1 - t0 <= k_TimeEpsilon) continue;
				Cubic sourceCubic = sourceCubics != null && s < sourceCubics.Length
					? sourceCubics[s] : CubicFromKeyPair(keys[s], keys[s + 1]);
				TryFindMaxCubicDifference(candidate, candidateStart.time, candidateSpan,
					sourceCubic, t0, t1,
					out float error, out _);
				if (error > maxError) maxError = error;
				if (maxError > tolerance) return false;
			}
			return true;
		}

		static Cubic CubicFromKeyPair(Keyframe a, Keyframe b)
		{
			float span = b.time - a.time;
			return new Cubic
			{
				C0 = a.value,
				C1 = span * a.outTangent,
				C2 = -3f * a.value + 3f * b.value - 2f * span * a.outTangent - span * b.inTangent,
				C3 = 2f * a.value - 2f * b.value + span * a.outTangent + span * b.inTangent
			};
		}

		static float EvaluateCubic(Cubic c, float u)
		{
			return ((c.C3 * u + c.C2) * u + c.C1) * u + c.C0;
		}

		static Cubic ComposeCubic(Cubic c, float alpha, float beta)
		{
			float a2 = alpha * alpha;
			float a3 = a2 * alpha;
			float b2 = beta * beta;
			float b3 = b2 * beta;
			return new Cubic
			{
				C0 = c.C0 + c.C1 * alpha + c.C2 * a2 + c.C3 * a3,
				C1 = beta * (c.C1 + 2f * c.C2 * alpha + 3f * c.C3 * a2),
				C2 = b2 * (c.C2 + 3f * c.C3 * alpha),
				C3 = b3 * c.C3
			};
		}

		static bool TryFindMaxCubicDifference(Cubic candidate, float candidateStart, float candidateSpan,
			Cubic original, float originalStart, float originalEnd, out float maxError, out float maxTime)
		{
			maxError = 0f;
			maxTime = originalStart;
			float sourceSpan = originalEnd - originalStart;
			if (sourceSpan <= k_TimeEpsilon || candidateSpan <= k_TimeEpsilon) return false;

			float alpha = (originalStart - candidateStart) / candidateSpan;
			float beta = sourceSpan / candidateSpan;
			Cubic c = ComposeCubic(candidate, alpha, beta);

			float d0 = c.C0 - original.C0;
			float d1 = c.C1 - original.C1;
			float d2 = c.C2 - original.C2;
			float d3 = c.C3 - original.C3;

			EvaluateDifferenceAt(d0, d1, d2, d3, 0f, originalStart, sourceSpan, ref maxError, ref maxTime);
			EvaluateDifferenceAt(d0, d1, d2, d3, 1f, originalStart, sourceSpan, ref maxError, ref maxTime);

			float qa = 3f * d3;
			float qb = 2f * d2;
			float qc = d1;
			if (Mathf.Abs(qa) <= 1e-12f)
			{
				if (Mathf.Abs(qb) > 1e-12f)
				{
					float root = -qc / qb;
					if (root > 0f && root < 1f)
						EvaluateDifferenceAt(d0, d1, d2, d3, root, originalStart, sourceSpan, ref maxError, ref maxTime);
				}
			}
			else
			{
				float discriminant = qb * qb - 4f * qa * qc;
				if (discriminant >= 0f)
				{
					float root = Mathf.Sqrt(discriminant);
					float r0 = (-qb - root) / (2f * qa);
					float r1 = (-qb + root) / (2f * qa);
					if (r0 > 0f && r0 < 1f)
						EvaluateDifferenceAt(d0, d1, d2, d3, r0, originalStart, sourceSpan, ref maxError, ref maxTime);
					if (r1 > 0f && r1 < 1f)
						EvaluateDifferenceAt(d0, d1, d2, d3, r1, originalStart, sourceSpan, ref maxError, ref maxTime);
				}
			}
			return true;
		}

		static void EvaluateDifferenceAt(float d0, float d1, float d2, float d3, float u,
			float startTime, float span, ref float maxError, ref float maxTime)
		{
			float difference = ((d3 * u + d2) * u + d1) * u + d0;
			float error = Mathf.Abs(difference);
			if (error > maxError)
			{
				maxError = error;
				maxTime = startTime + span * u;
			}
		}

		static float EvaluateKeyArray(Keyframe[] keys, int start, int end, float time)
		{
			int lo = start, hi = end;
			while (lo + 1 < hi)
			{
				int mid = (lo + hi) >> 1;
				if (keys[mid].time <= time) lo = mid;
				else hi = mid;
			}
			Keyframe a = keys[lo];
			Keyframe b = keys[Mathf.Min(lo + 1, end)];
			if (b.time - a.time <= k_TimeEpsilon) return a.value;
			return EvaluateTwoKeySegment(a, b, time);
		}

		static float EvaluateTwoKeySegment(Keyframe a, Keyframe b, float time)
		{
			float span = b.time - a.time;
			if (span <= 0f) return a.value;
			float u = Mathf.Clamp01((time - a.time) / span);
			float u2 = u * u;
			float u3 = u2 * u;
			return (2f * u3 - 3f * u2 + 1f) * a.value
				+ (u3 - 2f * u2 + u) * span * a.outTangent
				+ (-2f * u3 + 3f * u2) * b.value
				+ (u3 - u2) * span * b.inTangent;
		}

		static bool TryFitTangentsFast(AnimationCurve original, Keyframe[] keys,
			int start, int end, out float outTangent, out float inTangent)
		{
			outTangent = keys[start].outTangent;
			inTangent = keys[end].inTangent;
			if (keys[start].weightedMode != WeightedMode.None || keys[end].weightedMode != WeightedMode.None)
				return false;
			float span = keys[end].time - keys[start].time;
			if (span <= k_TimeEpsilon) return false;

			// Use a bounded sample count. This is only a fallback after the no-refit candidate fails.
			int steps = Mathf.Clamp(Mathf.CeilToInt(span * k_FitSteps), 8, 96);
			double aa = 0d, ab = 0d, bb = 0d, ar = 0d, br = 0d;
			for (int s = 1; s < steps; s++)
			{
				float u = s / (float)steps;
				float u2 = u * u;
				float u3 = u2 * u;
				float h00 = 2f * u3 - 3f * u2 + 1f;
				float h10 = u3 - 2f * u2 + u;
				float h01 = -2f * u3 + 3f * u2;
				float h11 = u3 - u2;
				float y = EvaluateKeyArray(keys, start, end, keys[start].time + span * u);
				float r = y - (h00 * keys[start].value + h01 * keys[end].value);
				float a = span * h10;
				float b = span * h11;
				aa += a * a;
				ab += a * b;
				bb += b * b;
				ar += a * r;
				br += b * r;
			}

			double determinant = aa * bb - ab * ab;
			if (Math.Abs(determinant) < 1e-12) return false;
			double m0 = (ar * bb - br * ab) / determinant;
			double m1 = (br * aa - ar * ab) / determinant;
			if (double.IsNaN(m0) || double.IsInfinity(m0) || double.IsNaN(m1) || double.IsInfinity(m1))
				return false;
			outTangent = (float)m0;
			inTangent = (float)m1;
			return true;
		}

		static Keyframe WithOutTangent(Keyframe key, float tangent)
		{
			key.outTangent = tangent;
			return key;
		}

		static Keyframe WithInTangent(Keyframe key, float tangent)
		{
			key.inTangent = tangent;
			return key;
		}

		static AnimationCurve BuildCurveFromPlan(AnimationCurve source, CurveReductionPlan plan)
		{
			var result = new AnimationCurve(plan.NewKeys)
			{
				preWrapMode = source.preWrapMode,
				postWrapMode = source.postWrapMode
			};
			for (int i = 0; i < result.length; i++) AnimationUtility.SetKeyBroken(result, i, plan.Broken[i]);
			for (int i = 0; i < result.length; i++)
			{
				AnimationUtility.SetKeyLeftTangentMode(result, i, plan.LeftModes[i]);
				AnimationUtility.SetKeyRightTangentMode(result, i, plan.RightModes[i]);
			}
			for (int i = 0; i < result.length; i++)
			{
				Keyframe key = result[i];
				if (plan.LeftModes[i] == AnimationUtility.TangentMode.Free) key.inTangent = plan.InTangents[i];
				if (plan.RightModes[i] == AnimationUtility.TangentMode.Free) key.outTangent = plan.OutTangents[i];
				if (plan.LeftModes[i] == AnimationUtility.TangentMode.Free || plan.RightModes[i] == AnimationUtility.TangentMode.Free)
					result.MoveKey(i, key);
			}
			return result;
		}

		static bool CurveStaysWithinTolerance(AnimationCurve original, AnimationCurve reduced, float tolerance)
		{
			if (original == null || reduced == null) return false;
			Keyframe[] originalKeys = original.keys;
			if (originalKeys.Length == 0) return true;

			if (CanUseAnalyticValidation(original, reduced))
				return AnalyticCurveComparison(original, reduced, originalKeys, tolerance);

			for (int i = 0; i < originalKeys.Length; i++)
				if (Mathf.Abs(originalKeys[i].value - reduced.Evaluate(originalKeys[i].time)) > tolerance)
					return false;
			for (int i = 0; i < originalKeys.Length - 1; i++)
			{
				float t0 = originalKeys[i].time, t1 = originalKeys[i + 1].time;
				if (t1 - t0 > k_TimeEpsilon && !FallbackCompareInterval(original, reduced, t0, t1, tolerance, 0))
					return false;
			}
			return true;
		}

		static bool CanUseAnalyticValidation(AnimationCurve original, AnimationCurve reduced)
		{
			Keyframe[] a = original.keys, b = reduced.keys;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i].weightedMode != WeightedMode.None) return false;
				if (i < a.Length - 1 && AnimationUtility.GetKeyRightTangentMode(original, i) == AnimationUtility.TangentMode.Constant) return false;
				if (i > 0 && AnimationUtility.GetKeyLeftTangentMode(original, i) == AnimationUtility.TangentMode.Constant) return false;
			}
			for (int i = 0; i < b.Length; i++)
			{
				if (b[i].weightedMode != WeightedMode.None) return false;
				if (i < b.Length - 1 && AnimationUtility.GetKeyRightTangentMode(reduced, i) == AnimationUtility.TangentMode.Constant) return false;
				if (i > 0 && AnimationUtility.GetKeyLeftTangentMode(reduced, i) == AnimationUtility.TangentMode.Constant) return false;
			}
			return true;
		}

		static bool AnalyticCurveComparison(AnimationCurve original, AnimationCurve reduced,
			Keyframe[] originalKeys, float tolerance)
		{
			Keyframe[] reducedKeys = reduced.keys;
			int r = 0;
			for (int s = 0; s < originalKeys.Length - 1; s++)
			{
				float t0 = originalKeys[s].time, t1 = originalKeys[s + 1].time;
				if (t1 - t0 <= k_TimeEpsilon) continue;
				while (r + 1 < reducedKeys.Length && reducedKeys[r + 1].time <= t0 + k_TimeEpsilon) r++;
				if (r + 1 >= reducedKeys.Length) return false;
				if (!TryFindMaxCubicDifference(
					CubicFromKeyPair(reducedKeys[r], reducedKeys[r + 1]),
					reducedKeys[r].time, reducedKeys[r + 1].time - reducedKeys[r].time,
					CubicFromKeyPair(originalKeys[s], originalKeys[s + 1]),
					t0, t1, out float error, out _))
					return false;
				if (error > tolerance) return false;
			}
			return true;
		}

		static bool FallbackCompareInterval(AnimationCurve original, AnimationCurve reduced,
			float t0, float t1, float tolerance, int depth)
		{
			float tm = (t0 + t1) * 0.5f;
			float e0 = Mathf.Abs(original.Evaluate(t0) - reduced.Evaluate(t0));
			float em = Mathf.Abs(original.Evaluate(tm) - reduced.Evaluate(tm));
			float e1 = Mathf.Abs(original.Evaluate(t1) - reduced.Evaluate(t1));
			if (e0 > tolerance || em > tolerance || e1 > tolerance) return false;
			if (depth >= k_FallbackMaxValidationDepth) return true;
			if (Mathf.Max(e0, Mathf.Max(em, e1)) <= tolerance * k_ErrorComfortRatio) return true;
			return FallbackCompareInterval(original, reduced, t0, tm, tolerance, depth + 1)
				&& FallbackCompareInterval(original, reduced, tm, t1, tolerance, depth + 1);
		}

		// --- shared Transform reduction ------------------------------------------------

		static void OptimizeSharedTransformGroups(List<CurveWork> work)
		{
			if (work == null || work.Count == 0) return;
			var groups = BuildTransformGroups(work);
			foreach (KeyValuePair<string, List<CurveWork>> pair in groups)
				if (IsCompleteTransformGroup(pair.Value))
					RemoveCommonTransformKeys(pair.Value);
		}

		static bool ContainsQuaternion(List<CurveWork> group)
		{
			bool x = false, y = false, z = false, w = false;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].Binding.propertyName ?? string.Empty;
				if (EndsWithComponent(p, ".x")) x = true;
				else if (EndsWithComponent(p, ".y")) y = true;
				else if (EndsWithComponent(p, ".z")) z = true;
				else if (EndsWithComponent(p, ".w")) w = true;
			}
			return x && y && z && w;
		}

		static void RemoveCommonTransformKeys(List<CurveWork> group)
		{
			// Candidates are computed once. Removing a key cannot create a new common key time,
			// so the previous repeated do/while pass was unnecessary.
			List<SharedKeyCandidate> candidates = FindCommonInteriorKeyTimes(group);
			if (candidates.Count == 0) return;

			int count = group.Count;
			var originals = new AnimationCurve[count];
			for (int i = 0; i < count; i++) originals[i] = group[i].Original;
			List<float> validationTimes = BuildValidationTimes(originals);

			for (int i = candidates.Count - 1; i >= 0; i--)
				TryRemoveSharedTime(group, candidates[i], originals, validationTimes);
		}

		static List<SharedKeyCandidate> FindCommonInteriorKeyTimes(List<CurveWork> group)
		{
			var result = new List<SharedKeyCandidate>();
			if (group.Count == 0) return result;

			var keySets = new Keyframe[group.Count][];
			for (int g = 0; g < group.Count; g++)
				keySets[g] = group[g].Reduced.keys;

			Keyframe[] baseKeys = keySets[0];
			var cursors = new int[group.Count];

			for (int baseIndex = 1; baseIndex < baseKeys.Length - 1; baseIndex++)
			{
				float time = baseKeys[baseIndex].time;
				var indices = new int[group.Count];
				indices[0] = baseIndex;
				bool common = true;

				for (int g = 1; g < group.Count; g++)
				{
					Keyframe[] keys = keySets[g];
					int cursor = cursors[g];
					while (cursor < keys.Length && keys[cursor].time < time - k_TimeEpsilon) cursor++;
					cursors[g] = cursor;
					if (cursor >= keys.Length || cursor <= 0 || cursor >= keys.Length - 1
						|| Mathf.Abs(keys[cursor].time - time) > k_TimeEpsilon)
					{
						common = false;
						break;
					}
					indices[g] = cursor;
				}

				if (common)
					result.Add(new SharedKeyCandidate { Time = time, Indices = indices });
			}
			return result;
		}

		static bool TryRemoveSharedTime(List<CurveWork> group, SharedKeyCandidate candidate,
			AnimationCurve[] originals, List<float> validationTimes)
		{
			int count = group.Count;
			var candidates = new AnimationCurve[count];

			for (int i = 0; i < count; i++)
			{
				int index = candidate.Indices[i];
				AnimationCurve source = group[i].Reduced;
				if (index <= 0 || index >= source.length - 1) return false;
				AnimationCurve temp = CopyCurve(source);
				temp.RemoveKey(index);
				candidates[i] = temp;
			}

			CurveKind kind = group[0].Kind;
			bool ok = kind == CurveKind.Rotation
				? ValidateRotationGroup(originals, candidates, group, validationTimes)
				: kind == CurveKind.Position
					? ValidateVectorGroup(originals, candidates, group, validationTimes, false)
					: kind == CurveKind.Scale
						&& ValidateVectorGroup(originals, candidates, group, validationTimes, true);

			if (!ok) return false;
			for (int i = 0; i < count; i++) group[i].Reduced = candidates[i];
			return true;
		}

		static bool ValidateVectorGroup(AnimationCurve[] originals, AnimationCurve[] candidates,
			List<CurveWork> group, List<float> times, bool relative)
		{
			if (times == null || times.Count == 0) return true;

			float ratio = GetRelativeRatio(group);
			for (int i = 0; i < times.Count; i++)
			{
				Vector3 a = EvaluateVector3(originals, group, times[i]);
				Vector3 b = EvaluateVector3(candidates, group, times[i]);
				if (relative)
				{
					float dx = Mathf.Abs(a.x - b.x) / Mathf.Max(Mathf.Abs(a.x), 1e-3f);
					float dy = Mathf.Abs(a.y - b.y) / Mathf.Max(Mathf.Abs(a.y), 1e-3f);
					float dz = Mathf.Abs(a.z - b.z) / Mathf.Max(Mathf.Abs(a.z), 1e-3f);
					if (Mathf.Max(dx, Mathf.Max(dy, dz)) > Mathf.Max(ratio, 1e-7f)) return false;
				}
				else if (VectorGroupAbsoluteError(a, b, group) > 1f) return false;
			}
			return true;
		}

		static float VectorGroupAbsoluteError(Vector3 original, Vector3 candidate, List<CurveWork> group)
		{
			float xTolerance = GetComponentTolerance(group, ".x");
			float yTolerance = GetComponentTolerance(group, ".y");
			float zTolerance = GetComponentTolerance(group, ".z");
			float dx = Mathf.Abs(original.x - candidate.x) / Mathf.Max(xTolerance, 1e-7f);
			float dy = Mathf.Abs(original.y - candidate.y) / Mathf.Max(yTolerance, 1e-7f);
			float dz = Mathf.Abs(original.z - candidate.z) / Mathf.Max(zTolerance, 1e-7f);
			return Mathf.Max(dx, Mathf.Max(dy, dz));
		}

		static float GetComponentTolerance(List<CurveWork> group, string component)
		{
			for (int i = 0; i < group.Count; i++)
				if (EndsWithComponent(group[i].Binding.propertyName ?? string.Empty, component))
					return group[i].Tolerance;
			return float.PositiveInfinity;
		}

		static bool ValidateRotationGroup(AnimationCurve[] originals, AnimationCurve[] candidates,
			List<CurveWork> group, List<float> times)
		{
			if (times == null || times.Count == 0) return true;
			bool quaternion = ContainsQuaternion(group);
			float tolerance = 0f;
			for (int i = 0; i < group.Count; i++) tolerance = Mathf.Max(tolerance, group[i].Tolerance);

			for (int i = 0; i < times.Count; i++)
			{
				float t = times[i];
				Quaternion a = quaternion
					? NormalizeQuaternion(EvaluateRotation4(originals, group, t))
					: Quaternion.Euler(EvaluateVector3(originals, group, t));
				Quaternion b = quaternion
					? NormalizeQuaternion(EvaluateRotation4(candidates, group, t))
					: Quaternion.Euler(EvaluateVector3(candidates, group, t));
				if (Quaternion.Angle(a, b) > tolerance) return false;
			}
			return true;
		}

		static List<float> BuildValidationTimes(AnimationCurve[] originals)
		{
			int estimated = 0;
			for (int i = 0; i < originals.Length; i++) estimated += Mathf.Max(1, originals[i].length * 4);
			var times = new List<float>(estimated);
			for (int c = 0; c < originals.Length; c++)
			{
				Keyframe[] keys = originals[c].keys;
				for (int i = 0; i < keys.Length; i++)
				{
					times.Add(keys[i].time);
					if (i + 1 < keys.Length)
					{
						float t = keys[i].time, next = keys[i + 1].time;
						if (next > t)
						{
							times.Add(Mathf.Lerp(t, next, 0.25f));
							times.Add(Mathf.Lerp(t, next, 0.5f));
							times.Add(Mathf.Lerp(t, next, 0.75f));
						}
					}
				}
			}
			times.Sort();
			int write = 0;
			for (int i = 0; i < times.Count; i++)
				if (write == 0 || Mathf.Abs(times[i] - times[write - 1]) > k_TimeEpsilon)
					times[write++] = times[i];
			if (write < times.Count) times.RemoveRange(write, times.Count - write);
			return times;
		}

		static Vector3 EvaluateVector3(AnimationCurve[] curves, List<CurveWork> group, float time)
		{
			float x = 0f, y = 0f, z = 0f;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].Binding.propertyName ?? string.Empty;
				float v = curves[i].Evaluate(time);
				if (EndsWithComponent(p, ".x")) x = v;
				else if (EndsWithComponent(p, ".y")) y = v;
				else if (EndsWithComponent(p, ".z")) z = v;
			}
			return new Vector3(x, y, z);
		}

		static Vector4 EvaluateRotation4(AnimationCurve[] curves, List<CurveWork> group, float time)
		{
			float x = 0f, y = 0f, z = 0f, w = 1f;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].Binding.propertyName ?? string.Empty;
				float v = curves[i].Evaluate(time);
				if (EndsWithComponent(p, ".x")) x = v;
				else if (EndsWithComponent(p, ".y")) y = v;
				else if (EndsWithComponent(p, ".z")) z = v;
				else if (EndsWithComponent(p, ".w")) w = v;
			}
			return new Vector4(x, y, z, w);
		}

		static Quaternion NormalizeQuaternion(Vector4 q)
		{
			float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
			return m <= 1e-7f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
		}

		static float GetRelativeRatio(List<CurveWork> group)
		{
			float ratio = float.PositiveInfinity;
			for (int i = 0; i < group.Count; i++) ratio = Mathf.Min(ratio, group[i].RelativeRatio);
			return float.IsInfinity(ratio) || float.IsNaN(ratio) ? 0f : Mathf.Max(ratio, 1e-7f);
		}

		static bool IsCompleteTransformGroup(List<CurveWork> group)
		{
			if (group == null || group.Count == 0) return false;
			CurveKind kind = group[0].Kind;
			bool x = false, y = false, z = false, w = false;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].Binding.propertyName ?? string.Empty;
				if (EndsWithComponent(p, ".x")) x = true;
				else if (EndsWithComponent(p, ".y")) y = true;
				else if (EndsWithComponent(p, ".z")) z = true;
				else if (EndsWithComponent(p, ".w")) w = true;
			}
			return x && y && z && (kind != CurveKind.Rotation || !ContainsQuaternion(group) || w);
		}

		static Dictionary<string, List<CurveWork>> BuildTransformGroups(List<CurveWork> work)
		{
			var groups = new Dictionary<string, List<CurveWork>>(StringComparer.Ordinal);
			for (int i = 0; i < work.Count; i++)
			{
				CurveWork item = work[i];
				if (item.Kind == CurveKind.Generic) continue;
				string representation = item.Kind == CurveKind.Rotation
					? GetRotationRepresentation(item.Binding.propertyName)
					: string.Empty;
				string typeName = item.Binding.type != null ? item.Binding.type.AssemblyQualifiedName : string.Empty;
				string key = item.Kind + "\n" + (item.Binding.path ?? string.Empty)
					+ "\n" + representation + "\n" + typeName;
				if (!groups.TryGetValue(key, out List<CurveWork> list))
				{
					list = new List<CurveWork>(4);
					groups.Add(key, list);
				}
				list.Add(item);
			}
			return groups;
		}

		static string GetRotationRepresentation(string property)
		{
			property = property ?? string.Empty;
			return property.IndexOf("m_LocalRotation", StringComparison.OrdinalIgnoreCase) >= 0
				? "Quaternion"
				: property.IndexOf("m_LocalEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
					|| property.IndexOf("localEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
					? "Euler"
					: "Rotation";
		}

		static void ValidateAndRevertTransformGroups(List<CurveWork> work)
		{
			if (work == null || work.Count == 0) return;
			var groups = BuildTransformGroups(work);
			foreach (KeyValuePair<string, List<CurveWork>> pair in groups)
			{
				List<CurveWork> group = pair.Value;
				if (!IsCompleteTransformGroup(group) || ValidateTransformGroup(group)) continue;
				for (int i = 0; i < group.Count; i++) group[i].Reduced = CopyCurve(group[i].Original);
			}
		}

		static bool ValidateTransformGroup(List<CurveWork> group)
		{
			int count = group.Count;
			var originals = new AnimationCurve[count];
			var candidates = new AnimationCurve[count];
			for (int i = 0; i < count; i++) { originals[i] = group[i].Original; candidates[i] = group[i].Reduced; }

			CurveKind kind = group[0].Kind;
			float ratio = GetRelativeRatio(group);
			float absTol = 0f;
			for (int i = 0; i < group.Count; i++)
				absTol = Mathf.Max(absTol, group[i].Tolerance);

			List<float> times = BuildValidationTimes(originals);
			for (int i = 0; i < times.Count; i++)
			{
				if (TransformErrorMagnitude(originals, candidates, group, kind, times[i], ratio, absTol) > 1f)
					return false;
			}

			// The sampled/key-time phase is the fast rejection pass. The adaptive interval pass
			// is retained as the final transform-level safety net for narrow deviations between
			// validation times. It is run only after all cheap checks pass.
			for (int i = 0; i + 1 < times.Count; i++)
			{
				float t0 = times[i], t1 = times[i + 1];
				if (t1 - t0 <= k_TimeEpsilon) continue;
				if (!AdaptiveTransformComparison(originals, candidates, group, kind,
					t0, t1, ratio, 0f, 0))
					return false;
			}
			return true;
		}

		static bool AdaptiveTransformComparison(AnimationCurve[] originals, AnimationCurve[] candidates,
			List<CurveWork> group, CurveKind kind, float t0, float t1,
			float ratio, float absoluteTolerance, int depth)
		{
			float tm = (t0 + t1) * 0.5f;
			float e0 = TransformErrorMagnitude(originals, candidates, group, kind, t0, ratio, absoluteTolerance);
			float em = TransformErrorMagnitude(originals, candidates, group, kind, tm, ratio, absoluteTolerance);
			float e1 = TransformErrorMagnitude(originals, candidates, group, kind, t1, ratio, absoluteTolerance);
			if (e0 > 1f || em > 1f || e1 > 1f) return false;
			if (depth >= 8) return true;
			float maxError = Mathf.Max(e0, Mathf.Max(em, e1));
			if (maxError <= k_ErrorComfortRatio) return true;
			return AdaptiveTransformComparison(originals, candidates, group, kind, t0, tm, ratio, absoluteTolerance, depth + 1)
				&& AdaptiveTransformComparison(originals, candidates, group, kind, tm, t1, ratio, absoluteTolerance, depth + 1);
		}

		static float TransformErrorMagnitude(AnimationCurve[] originals, AnimationCurve[] candidates,
			List<CurveWork> group, CurveKind kind, float time, float ratio, float unusedAbsoluteTolerance)
		{
			if (kind == CurveKind.Rotation)
			{
				Quaternion a, b;
				if (ContainsQuaternion(group))
				{
					a = NormalizeQuaternion(EvaluateRotation4(originals, group, time));
					b = NormalizeQuaternion(EvaluateRotation4(candidates, group, time));
				}
				else
				{
					a = Quaternion.Euler(EvaluateVector3(originals, group, time));
					b = Quaternion.Euler(EvaluateVector3(candidates, group, time));
				}
				return Quaternion.Angle(a, b) / Mathf.Max(360f * ratio, 0.001f);
			}

			Vector3 a3 = EvaluateVector3(originals, group, time);
			Vector3 b3 = EvaluateVector3(candidates, group, time);
			if (kind == CurveKind.Scale)
			{
				float dx = Mathf.Abs(a3.x - b3.x) / Mathf.Max(Mathf.Abs(a3.x), 1e-3f);
				float dy = Mathf.Abs(a3.y - b3.y) / Mathf.Max(Mathf.Abs(a3.y), 1e-3f);
				float dz = Mathf.Abs(a3.z - b3.z) / Mathf.Max(Mathf.Abs(a3.z), 1e-3f);
				return Mathf.Max(dx, Mathf.Max(dy, dz)) / Mathf.Max(ratio, 1e-7f);
			}
			return VectorGroupAbsoluteError(a3, b3, group);
		}

		static string MakeBindingKey(EditorCurveBinding binding)
		{
			return (binding.path ?? string.Empty) + "\n" + (binding.propertyName ?? string.Empty)
				+ "\n" + (binding.type != null ? binding.type.AssemblyQualifiedName : string.Empty)
				;
		}

		static int ComputeClipSignature(AnimationClip clip)
		{
			EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
			var curves = new AnimationCurve[bindings.Length];
			for (int i = 0; i < bindings.Length; i++)
				curves[i] = AnimationUtility.GetEditorCurve(clip, bindings[i]);
			return ComputeClipSignature(bindings, curves);
		}

		static int ComputeClipSignature(EditorCurveBinding[] bindings, AnimationCurve[] curves)
		{
			unchecked
			{
				int hash = 17;
				for (int b = 0; b < bindings.Length; b++)
				{
					EditorCurveBinding binding = bindings[b];
					AnimationCurve curve = curves != null && b < curves.Length ? curves[b] : null;
					hash = hash * 31 + MakeBindingKey(binding).GetHashCode();
					if (curve == null) continue;
					hash = hash * 31 + curve.length;
					hash = hash * 31 + curve.preWrapMode.GetHashCode();
					hash = hash * 31 + curve.postWrapMode.GetHashCode();
					Keyframe[] keys = curve.keys;
					for (int i = 0; i < keys.Length; i++)
					{
						Keyframe k = keys[i];
						hash = hash * 31 + k.time.GetHashCode();
						hash = hash * 31 + k.value.GetHashCode();
						hash = hash * 31 + k.inTangent.GetHashCode();
						hash = hash * 31 + k.outTangent.GetHashCode();
						hash = hash * 31 + k.inWeight.GetHashCode();
						hash = hash * 31 + k.outWeight.GetHashCode();
						hash = hash * 31 + k.weightedMode.GetHashCode();
						hash = hash * 31 + AnimationUtility.GetKeyLeftTangentMode(curve, i).GetHashCode();
						hash = hash * 31 + AnimationUtility.GetKeyRightTangentMode(curve, i).GetHashCode();
						hash = hash * 31 + AnimationUtility.GetKeyBroken(curve, i).GetHashCode();
					}
				}
				return hash;
			}
		}
        static CachedClipReduction BuildReductionCache(AnimationClip clip, float tolerancePercent,
            EditorCurveBinding[] bindings, AnimationCurve[] sourceCurves)
        {
            var managedInput = BuildManagedReductionInput(bindings, sourceCurves, tolerancePercent);
            var reduced = ManagedAnimationReductionEngine.ReduceClip(managedInput, tolerancePercent);
            var cache = new CachedClipReduction
            {
                Signature = ComputeClipSignature(bindings, sourceCurves),
                TolerancePercent = tolerancePercent,
                RemovedKeys = reduced.RemovedKeys,
                Curves = new List<CachedCurveReduction>(reduced.Curves.Count)
            };
            for (int i = 0; i < reduced.Curves.Count; i++)
            {
                var item = reduced.Curves[i];
                cache.Curves.Add(new CachedCurveReduction
                {
                    BindingKey = item.BindingKey,
                    Reduced = ConvertManagedCurve(item.Reduced)
                });
            }
            return cache;
        }

        static List<ManagedAnimationReductionEngine.ManagedCurveWork> BuildManagedReductionInput(
            EditorCurveBinding[] bindings, AnimationCurve[] sourceCurves, float tolerancePercent)
        {
            var result = new List<ManagedAnimationReductionEngine.ManagedCurveWork>(bindings?.Length ?? 0);
            if (bindings == null) return result;
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                AnimationCurve curve = sourceCurves != null && i < sourceCurves.Length ? sourceCurves[i] : null;
                if (curve == null || curve.length < 3 || binding.isDiscreteCurve || binding.isSerializeReferenceCurve) continue;
                Keyframe[] curveKeys = curve.keys;
                bool weighted = false;
                for (int keyIndex = 0; keyIndex < curveKeys.Length; keyIndex++)
                    if (curveKeys[keyIndex].weightedMode != WeightedMode.None) { weighted = true; break; }
                if (weighted) continue;
                if (IsConstant(curve, out _) && IsSafeConstantCurve(curve)) continue;
                result.Add(new ManagedAnimationReductionEngine.ManagedCurveWork
                {
                    Path = binding.path ?? string.Empty,
                    PropertyName = binding.propertyName ?? string.Empty,
                    TypeName = binding.type != null ? binding.type.AssemblyQualifiedName : string.Empty,
                    Kind = ToManagedCurveKind(GetCurveKind(binding)),
                    Curve = ConvertManagedCurve(curve),
                    Tolerance = ToleranceFor(binding, curve, tolerancePercent)
                });
            }
            return result;
        }

        static ManagedAnimationReductionEngine.CurveKind ToManagedCurveKind(CurveKind kind)
            => (ManagedAnimationReductionEngine.CurveKind)(int)kind;

        static ManagedAnimationReductionEngine.ManagedAnimationCurve ConvertManagedCurve(AnimationCurve curve)
        {
            if (curve == null) return null;
            var keys = curve.keys;
            var managed = new ManagedAnimationReductionEngine.ManagedKeyframe[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                managed[i] = new ManagedAnimationReductionEngine.ManagedKeyframe
                {
                    time = key.time, value = key.value, inTangent = key.inTangent, outTangent = key.outTangent,
                    inWeight = key.inWeight, outWeight = key.outWeight,
                    weightedMode = ToManagedWeightedMode(key.weightedMode),
                    LeftMode = ToManagedTangentMode(AnimationUtility.GetKeyLeftTangentMode(curve, i)),
                    RightMode = ToManagedTangentMode(AnimationUtility.GetKeyRightTangentMode(curve, i)),
                    Broken = AnimationUtility.GetKeyBroken(curve, i)
                };
            }
            return new ManagedAnimationReductionEngine.ManagedAnimationCurve(managed,
                (int)curve.preWrapMode, (int)curve.postWrapMode);
        }

        static ManagedAnimationReductionEngine.ManagedTangentMode ToManagedTangentMode(AnimationUtility.TangentMode mode)
        {
            switch (mode)
            {
                case AnimationUtility.TangentMode.Auto: return ManagedAnimationReductionEngine.ManagedTangentMode.Auto;
                case AnimationUtility.TangentMode.Constant: return ManagedAnimationReductionEngine.ManagedTangentMode.Constant;
                case AnimationUtility.TangentMode.Linear: return ManagedAnimationReductionEngine.ManagedTangentMode.Linear;
                case AnimationUtility.TangentMode.ClampedAuto: return ManagedAnimationReductionEngine.ManagedTangentMode.ClampedAuto;
                default: return ManagedAnimationReductionEngine.ManagedTangentMode.Free;
            }
        }

        static ManagedAnimationReductionEngine.ManagedWeightedMode ToManagedWeightedMode(WeightedMode mode)
        {
            switch (mode)
            {
                case WeightedMode.In: return ManagedAnimationReductionEngine.ManagedWeightedMode.In;
                case WeightedMode.Out: return ManagedAnimationReductionEngine.ManagedWeightedMode.Out;
                case WeightedMode.Both: return ManagedAnimationReductionEngine.ManagedWeightedMode.Both;
                default: return ManagedAnimationReductionEngine.ManagedWeightedMode.None;
            }
        }

        static AnimationCurve ConvertManagedCurve(ManagedAnimationReductionEngine.ManagedAnimationCurve source)
        {
            if (source == null) return null;
            var keys = source.keys;
            var unityKeys = new Keyframe[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                unityKeys[i] = new Keyframe(keys[i].time, keys[i].value, keys[i].inTangent, keys[i].outTangent, keys[i].inWeight, keys[i].outWeight)
                {
                    weightedMode = ToUnityWeightedMode(keys[i].weightedMode)
                };
            }
            var curve = new AnimationCurve(unityKeys) { preWrapMode = (WrapMode)source.PreWrapMode, postWrapMode = (WrapMode)source.PostWrapMode };
            for (int i = 0; i < keys.Length; i++)
            {
                AnimationUtility.SetKeyBroken(curve, i, keys[i].Broken);
                AnimationUtility.SetKeyLeftTangentMode(curve, i, ToUnityTangentMode(keys[i].LeftMode));
                AnimationUtility.SetKeyRightTangentMode(curve, i, ToUnityTangentMode(keys[i].RightMode));
                var key = curve[i];
                key.inTangent = keys[i].inTangent;
                key.outTangent = keys[i].outTangent;
                curve.MoveKey(i, key);
            }
            return curve;
        }

        static WeightedMode ToUnityWeightedMode(ManagedAnimationReductionEngine.ManagedWeightedMode mode)
            => (WeightedMode)(int)mode;

        static AnimationUtility.TangentMode ToUnityTangentMode(ManagedAnimationReductionEngine.ManagedTangentMode mode)
        {
            switch (mode)
            {
                case ManagedAnimationReductionEngine.ManagedTangentMode.Auto: return AnimationUtility.TangentMode.Auto;
                case ManagedAnimationReductionEngine.ManagedTangentMode.Constant: return AnimationUtility.TangentMode.Constant;
                case ManagedAnimationReductionEngine.ManagedTangentMode.Linear: return AnimationUtility.TangentMode.Linear;
                case ManagedAnimationReductionEngine.ManagedTangentMode.ClampedAuto: return AnimationUtility.TangentMode.ClampedAuto;
                default: return AnimationUtility.TangentMode.Free;
            }
        }


		static bool TryGetCachedReduction(AnimationClip clip, float tolerancePercent, out CachedClipReduction cache)
		{
			cache = null;
			string path = AssetDatabase.GetAssetPath(clip);
			if (string.IsNullOrEmpty(path) || !s_ReductionCache.TryGetValue(path, out cache)) return false;
			if (!Mathf.Approximately(cache.TolerancePercent, tolerancePercent)) return false;
			return cache.Signature == ComputeClipSignature(clip);
		}

		static void CacheReduction(AnimationClip clip, CachedClipReduction cache)
		{
			string path = AssetDatabase.GetAssetPath(clip);
			if (!string.IsNullOrEmpty(path) && cache != null) s_ReductionCache[path] = cache;
		}

		static void ReduceClip(AnimationClip clip, float tolerancePercent, FixContext context)
		{
			if (clip == null) throw new InvalidOperationException("AnimationClip could not be loaded for key reduction.");
			if (tolerancePercent <= 0f) throw new InvalidOperationException("A positive, configured key-reduction tolerance is required.");

			if (!TryGetCachedReduction(clip, tolerancePercent, out CachedClipReduction cache))
			{
				EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
				var sourceCurves = new AnimationCurve[bindings.Length];
				for (int i = 0; i < bindings.Length; i++)
					sourceCurves[i] = AnimationUtility.GetEditorCurve(clip, bindings[i]);
				cache = BuildReductionCache(clip, tolerancePercent, bindings, sourceCurves);
				CacheReduction(clip, cache);
			}

			if (cache == null || cache.Curves == null || cache.Curves.Count == 0)
				throw new InvalidOperationException("No eligible curves remained after refreshing key-reduction evidence.");

			EditorCurveBinding[] current = AnimationUtility.GetCurveBindings(clip);
			var map = new Dictionary<string, EditorCurveBinding>(current.Length, StringComparer.Ordinal);
			for (int i = 0; i < current.Length; i++) map[MakeBindingKey(current[i])] = current[i];

			var setBindings = new List<EditorCurveBinding>(cache.Curves.Count);
			var setCurves = new List<AnimationCurve>(cache.Curves.Count);
			for (int i = 0; i < cache.Curves.Count; i++)
			{
				CachedCurveReduction item = cache.Curves[i];
				if (!map.TryGetValue(item.BindingKey, out EditorCurveBinding binding)) continue;
				setBindings.Add(binding);
				setCurves.Add(CopyCurve(item.Reduced));
			}

			if (setBindings.Count == 0) throw new InvalidOperationException("No eligible curve reduction remained after refreshing the evidence.");
			context.EnsureWritable();
			context.RequireBackup(context.AssetPath);
			ApplyCurveBatch(clip, setBindings, setCurves);
			context.NeedsSave = true;
		}

		static bool VerifyReducedClip(string assetPath, long originalKeyCount, int expectedRemovedKeys)
		{
			var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
			if (clip == null || expectedRemovedKeys <= 0) return false;
			long currentKeyCount = CountKeys(clip);
			return currentKeyCount == originalKeyCount - expectedRemovedKeys && currentKeyCount >= 0;
		}

		static bool CurvesEquivalent(AnimationCurve a, AnimationCurve b)
		{
			if (a == null || b == null || a.length != b.length) return false;
			if (a.preWrapMode != b.preWrapMode || a.postWrapMode != b.postWrapMode) return false;
			Keyframe[] ak = a.keys, bk = b.keys;
			for (int i = 0; i < ak.Length; i++)
			{
				if (ak[i].time != bk[i].time || ak[i].value != bk[i].value
					|| ak[i].inTangent != bk[i].inTangent || ak[i].outTangent != bk[i].outTangent
					|| ak[i].inWeight != bk[i].inWeight || ak[i].outWeight != bk[i].outWeight
					|| ak[i].weightedMode != bk[i].weightedMode
					|| AnimationUtility.GetKeyLeftTangentMode(a, i) != AnimationUtility.GetKeyLeftTangentMode(b, i)
					|| AnimationUtility.GetKeyRightTangentMode(a, i) != AnimationUtility.GetKeyRightTangentMode(b, i)
					|| AnimationUtility.GetKeyBroken(a, i) != AnimationUtility.GetKeyBroken(b, i)) return false;
			}
			return true;
		}

		static bool EndsWithComponent(string property, string component)
		{
			return property.EndsWith(component, StringComparison.OrdinalIgnoreCase);
		}

		static AnimationCurve CopyCurve(AnimationCurve source)
		{
			var copy = new AnimationCurve(source.keys)
			{
				preWrapMode = source.preWrapMode,
				postWrapMode = source.postWrapMode
			};
			return copy;
		}

		static bool HasNonFiniteKeys(AnimationCurve curve)
		{
			Keyframe[] keys = curve.keys;
			for (int i = 0; i < keys.Length; i++)
			{
				Keyframe k = keys[i];
				if (float.IsNaN(k.time) || float.IsInfinity(k.time)
					|| float.IsNaN(k.value) || float.IsInfinity(k.value)
					|| float.IsNaN(k.inTangent) || float.IsInfinity(k.inTangent)
					|| float.IsNaN(k.outTangent) || float.IsInfinity(k.outTangent))
					return true;
			}
			return false;
		}

		static bool IsConstant(AnimationCurve curve, out float value)
		{
			value = 0f;
			if (curve == null || curve.length == 0) return false;
			Keyframe[] keys = curve.keys;
			value = keys[0].value;
			for (int i = 1; i < keys.Length; i++) if (keys[i].value != value) return false;
			return true;
		}

		static bool IsSafeConstantCurve(AnimationCurve curve)
		{
			if (!IsConstant(curve, out _)) return false;
			Keyframe[] keys = curve.keys;
			for (int i = 0; i < keys.Length - 1; i++)
			{
				var rightMode = AnimationUtility.GetKeyRightTangentMode(curve, i);
				var leftMode = AnimationUtility.GetKeyLeftTangentMode(curve, i + 1);
				bool constantSegment = rightMode == AnimationUtility.TangentMode.Constant
					|| leftMode == AnimationUtility.TangentMode.Constant
					|| (Mathf.Approximately(keys[i].outTangent, 0f)
						&& Mathf.Approximately(keys[i + 1].inTangent, 0f));
				if (!constantSegment) return false;
			}
			return true;
		}

		static void ApplyCurveBatch(AnimationClip clip, List<EditorCurveBinding> bindings, List<AnimationCurve> curves)
		{
			if (clip == null || bindings == null || bindings.Count == 0) return;
#if UNITY_2020_1_OR_NEWER
			AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
#else
			for (int i = 0; i < bindings.Count; i++)
				AnimationUtility.SetEditorCurve(clip, bindings[i], curves[i]);
#endif
		}
		static void ApplyModelImporterChange(FixContext context, Action<ModelImporter> mutation)
		{
			if (context == null) throw new ArgumentNullException(nameof(context));
			if (mutation == null) throw new ArgumentNullException(nameof(mutation));
			context.EnsureWritable();
			var importer = context.Importer as ModelImporter;
			if (importer == null) throw new InvalidOperationException("Expected a ModelImporter for " + context.AssetPath + ".");
			mutation(importer);
			context.NeedsReimport = true;
		}

		static bool VerifyModelImporterSetting(FixContext context, Func<ModelImporter, bool> predicate, bool requireMeshes = false, bool requireClips = false)
		{
			if (context == null || predicate == null || string.IsNullOrEmpty(context.AssetPath)) return false;
			var importer = AssetImporter.GetAtPath(context.AssetPath) as ModelImporter;
			if (importer == null || !predicate(importer) || AssetDatabase.LoadAssetAtPath<GameObject>(context.AssetPath) == null) return false;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			bool foundMesh = false, foundClip = false;
			for (int i = 0; i < imported.Length; i++)
			{
				if (imported[i] is Mesh) foundMesh = true;
				if (imported[i] is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) foundClip = true;
			}
			return (!requireMeshes || foundMesh) && (!requireClips || foundClip);
		}

		static bool VerifySkinnedModelImporterSetting(FixContext context, Func<ModelImporter, bool> predicate)
		{
			if (!VerifyModelImporterSetting(context, predicate, requireMeshes: true)) return false;
			var root = AssetDatabase.LoadAssetAtPath<GameObject>(context.AssetPath);
			var renderers = root != null ? root.GetComponentsInChildren<SkinnedMeshRenderer>(true) : Array.Empty<SkinnedMeshRenderer>();
			for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null && renderers[i].sharedMesh != null) return true;
			return false;
		}

		static bool VerifyConstantScaleCurvesRemoved(FixContext context)
		{
			if (!VerifyModelImporterSetting(context, mi => mi.removeConstantScaleCurves, requireClips: true)) return false;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			for (int i = 0; i < imported.Length; i++)
			{
				var clip = imported[i] as AnimationClip;
				if (clip == null || clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) continue;
				foreach (var binding in AnimationUtility.GetCurveBindings(clip))
				{
					if (!binding.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal)) continue;
					var curve = AnimationUtility.GetEditorCurve(clip, binding);
					if (curve != null && IsConstant(curve, out float value) && MatchesImportedInitialScale(context.AssetPath, binding, value)) return false;
				}
			}
			return true;
		}

		static void SetHumanoidOversamplingX1(ModelImporter importer)
		{
			if (importer == null) throw new ArgumentNullException(nameof(importer));
			importer.humanoidOversampling = ModelImporterHumanoidOversampling.X1;
		}

		static bool IsHumanoidOversamplingX1(ModelImporter importer)
		{
			return importer != null && importer.humanoidOversampling == ModelImporterHumanoidOversampling.X1;
		}

	}
}

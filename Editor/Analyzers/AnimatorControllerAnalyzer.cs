using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Animator Controller structure and static graph diagnostics.</summary>
	public sealed class AnimatorControllerAnalyzer : IAssetAnalyzer
	{
		public AssetCategory Category => AssetCategory.Controller;

		public AssetKindMask SupportedKinds => AssetKindMask.Animation;

		static AnimatorControllerAnalyzer()
		{
			EditorApplication.projectChanged += InvalidateProjectEvidenceCaches;
			Rules.Register(id: "CTL001", title: "Many animator layers", category: AssetCategory.Controller,
							why: "Additional Animator layers add state-machine and blending work to the controller. The cost depends on active layers, masks, synchronization and how the controller is used.",
							fix: "Merge layers only when their behaviors are genuinely mutually exclusive or otherwise can be represented more simply.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "CTL002", title: "Very large state machine", category: AssetCategory.Controller,
							why: "Large Animator graphs contain more states and transitions to maintain and evaluate. Controller size alone does not prove a runtime bottleneck, but very large graphs deserve profiling.",
							fix: "Split unrelated behaviors into separate controllers or simplify state machines where the gameplay architecture permits.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "CTL003", title: "Animator parameters not referenced by the controller", category: AssetCategory.Controller,
							why: "A parameter not referenced by the controller graph can be removed automatically only when fresh C# and ForceText serialized-asset scans find no name consumers, no dynamic Animator-parameter calls, no StateMachineBehaviours, and no uninspectable managed plug-in assemblies. Incomplete or ambiguous evidence remains diagnostic-only.",
							fix: "Automatically remove parameters only when a fresh graph/source/serialized-asset scan proves the common case safe; otherwise keep the finding with no action.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "CTL004", title: "State machine contains many Any State transitions", category: AssetCategory.Controller,
							why: "Any State transitions are available from the current state and can increase transition-condition evaluation work. Large numbers also make controller behavior harder to reason about.",
							fix: "Consolidate overlapping Any State transitions where the state logic allows it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "CTL005", title: "Animator layer has IK Pass enabled", category: AssetCategory.Controller,
							why: "An Animator layer with IK Pass enabled performs an IK pass when evaluated and can trigger OnAnimatorIK. That work is unnecessary for layers that do not actually use IK callbacks or IK processing.",
							fix: "Review Fix: disable IK Pass on layers that do not require IK evaluation or OnAnimatorIK callbacks.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "CTL006", title: "Animator state has excessive transition conditions", category: AssetCategory.Controller,
							why: "Transitions with many conditions require more controller-side condition checks and are harder to reason about. This is a heuristic, not a proof of a runtime bottleneck.",
							fix: "Consolidate conditions or restructure the state graph where the resulting logic is equivalent.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "CTL007", title: "Animator states use Write Defaults", category: AssetCategory.Controller,
							why: "Write Defaults causes a state to write default values for properties it does not animate. Mixed Write Defaults configurations can create harder-to-predict state behavior and may cause additional property writes.",
							fix: "Review Write Defaults usage and adopt a consistent project policy rather than changing it blindly.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.High,
							backupRequired: true
							);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath) =>
			assetPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase);

		[Serializable]
		sealed class ControllerFacts
		{
			public int layers;
			public int states;
			public int transitions;
			public int anyStateTransitions;
			public int parameters;
			public int behaviours;
			public List<string> unusedParameters;
			public List<string> conditionHeavyTransitions;
			public List<string> writeDefaultStates;
			public int maxTransitionConditions;
			public List<string> ikLayers;
			public List<int> ikLayerIndices;
		}

		static ControllerFacts Measure(string assetPath, ScanContext ctx)
		{
			string hash = ctx.GetDependencyHash(assetPath);
			if (ctx.Cache.TryGet(ScanCache.ScopedKey(assetPath, typeof(ControllerFacts).FullName), hash, out ControllerFacts cached)) return cached;

			var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
			if (controller == null) return null;

			var facts = new ControllerFacts
			{
				layers = controller.layers.Length,
				parameters = controller.parameters.Length,
				unusedParameters = new List<string>(),
				conditionHeavyTransitions = new List<string>(),
				writeDefaultStates = new List<string>(),
				ikLayers = new List<string>(),
				ikLayerIndices = new List<int>()
			};

			var used = new HashSet<string>(StringComparer.Ordinal);
			var countedMachines = new HashSet<int>();
			for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
			{
				var layer = controller.layers[layerIndex];
				if (layer.stateMachine == null) continue;
				int sourceIndex = ResolveSourceLayerIndex(controller.layers, layerIndex);
				var sourceLayer = controller.layers[sourceIndex];
				if (sourceLayer.stateMachine != null && countedMachines.Add(sourceLayer.stateMachine.GetInstanceID()))
					CountStateMachine(sourceLayer.stateMachine, null, ref facts.states, ref facts.transitions, ref facts.anyStateTransitions, ref facts.behaviours, new HashSet<int>());
				if (sourceLayer.stateMachine != null)
					CollectUsedParametersForLayer(sourceLayer.stateMachine, null, used, layer);
			}

			foreach (var parameter in controller.parameters)
				if (!used.Contains(parameter.name)) facts.unusedParameters.Add(parameter.name);

			CollectControllerHeuristics(controller, facts.conditionHeavyTransitions, facts.writeDefaultStates, ref facts.maxTransitionConditions);
			for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
			{
				var layer = controller.layers[layerIndex];
				if (!layer.iKPass) continue;
				facts.ikLayers.Add(layer.name);
				facts.ikLayerIndices.Add(layerIndex);
			}

			ctx.Cache.Set(ScanCache.ScopedKey(assetPath, typeof(ControllerFacts).FullName), facts, hash);
			return facts;
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var facts = Measure(assetPath, ctx);
			if (facts == null) return null;

			var entry = new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = ".controller",
				Category = AssetCategory.Controller,
				TypeLabel = "Animator Controller",
				FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath)
			};

			using (RuleTiming.Begin("CTL001", entry.Path))
				if (facts.layers > ctx.Settings.animatorLayerWarn)
					entry.AddIssue("CTL001", facts.layers + " layers", "review under " + ctx.Settings.animatorLayerWarn);

			using (RuleTiming.Begin("CTL002", entry.Path))
				if (facts.states > ctx.Settings.animatorStateWarn)
					entry.AddIssue("CTL002", facts.states + " states, " + facts.transitions + " counted transitions", "under " + ctx.Settings.animatorStateWarn + " states");

			using (RuleTiming.Begin("CTL003", entry.Path))
				if (facts.unusedParameters.Count > 0)
				{
					var unused = new List<string>(facts.unusedParameters);
					var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
					string safetyReason;
					bool safeAtScan = CanSafelyRemoveParameters(controller, unused, out safetyReason);
					var issue = entry.AddIssue("CTL003", unused.Count + " parameter(s) not referenced by the controller graph",
						safeAtScan ? "Remove parameters proven unused by controller and project code" : "No safe automatic action: " + safetyReason,
						0,
						c =>
						{
							var target = c.Asset as AnimatorController;
						string preflightReason;
						if (!CanSafelyRemoveParameters(target, unused, out preflightReason, true))
							throw new InvalidOperationException("CTL003 automatic fix preflight blocked: " + preflightReason);
						Controller(c, ac => RemoveParameters(ac, unused));
						},
						"Parameters are removed only when the complete source scan proves no project consumer; any incomplete evidence leaves this finding diagnostic-only.",
						SavingsBasis.Unknown,
						c => HasNoParameters(AssetDatabase.LoadAssetAtPath<AnimatorController>(c.AssetPath), unused));
					if (issue != null && !safeAtScan)
					{
						issue.FixKind = FixKind.None;
						issue.Fix = null;
						issue.Verify = null;
						issue.Recommended = "No automatic change: " + safetyReason;
					}
					issue?.Items("Potentially unused parameters", unused);
				}

			int anyStateWarn = Mathf.Max(4, ctx.Settings.animatorStateWarn / 4);
			using (RuleTiming.Begin("CTL004", entry.Path))
				if (facts.anyStateTransitions > anyStateWarn)
					entry.AddIssue("CTL004", facts.anyStateTransitions + " Any State transitions", "review under " + anyStateWarn, 0, null, "This is a review heuristic. Any State usage is legitimate for many controller designs.");

			using (RuleTiming.Begin("CTL006", entry.Path))
				if (facts.conditionHeavyTransitions.Count > 0)
					entry.AddIssue("CTL006", facts.conditionHeavyTransitions.Count + " transition(s) with " + facts.maxTransitionConditions + "+ conditions",
						"Review complex condition sets", 0, null, "Static heuristic; complex conditions are not inherently incorrect.")?.Items("Condition-heavy transitions", facts.conditionHeavyTransitions);

			using (RuleTiming.Begin("CTL007", entry.Path))
				if (facts.writeDefaultStates.Count > 0)
				{
					var expectedWriteDefaultStates = new List<string>(facts.writeDefaultStates);
					entry.AddIssue("CTL007", expectedWriteDefaultStates.Count + " state(s) with Write Defaults enabled",
						"Review project-wide Write Defaults policy", 0, null, "This is intentionally advisory. The selected action disables Write Defaults only for the states flagged in this controller; the controller asset is backed up and the resulting state is verified.",
						options: new List<FixOption> { new FixOption {
							Label = "Disable Write Defaults in this controller",
							ResultDescription = "Sets Write Defaults off for all states currently flagged by this controller finding.",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This may change animation property values when states do not animate every relevant property. Confirm this controller should use Write Defaults Off before applying.",
							Apply = c => Controller(c, ac =>
							{
								if (!WriteDefaultStateIdentitiesMatch(ac, expectedWriteDefaultStates))
									throw new InvalidOperationException("CTL007 preflight blocked: the exact Write Defaults state paths changed since analysis.");
								foreach (var layer in ac.layers) RemoveWriteDefaultsFromStateMachine(layer.stateMachine);
							}),
							Verify = c => CountWriteDefaultStates(AssetDatabase.LoadAssetAtPath<AnimatorController>(c.AssetPath)) == 0
						} }
					)?.Items("Write Defaults states", facts.writeDefaultStates);
				}

			using (RuleTiming.Begin("CTL005", entry.Path))
				if (facts.ikLayers.Count > 0)
				{
					var ikLayers = facts.ikLayers;
					var ikLayerIndices = new List<int>(facts.ikLayerIndices);
					var controllerAtScan = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
					var expectedLayerNames = CaptureLayerNames(controllerAtScan, ikLayerIndices);
					entry.AddIssue("CTL005", facts.ikLayers.Count + " layer(s) with IK Pass enabled",
						"Review Fix: disable IK Pass on these layers", 0, null,
						"IK Pass can invoke OnAnimatorIK. Disabling it changes runtime behavior, so this remains Review. The action checks the analyzed layer identities, requires backup, and verifies that only the selected layers are disabled.",
						options: new List<FixOption>
						{
						new FixOption
						{
							Label = "Disable IK Pass on flagged layers",
							ResultDescription = "Disables IK Pass only on the layer indices recorded in this finding.",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "Any script relying on OnAnimatorIK being called for these layers will stop receiving it. A backup is required.",
							Apply = c => Controller(c, ac =>
							{
								if (!IkTargetsStillMatch(ac, ikLayerIndices, expectedLayerNames))
									throw new InvalidOperationException("CTL005 preflight blocked: controller layer identities or IK Pass flags changed since analysis.");
								SetIkPass(ac, ikLayerIndices, false);
							}),
							Verify = c => CountIkLayers(AssetDatabase.LoadAssetAtPath<AnimatorController>(c.AssetPath), ikLayerIndices) == 0
								&& IkTargetNamesStillMatch(AssetDatabase.LoadAssetAtPath<AnimatorController>(c.AssetPath), ikLayerIndices, expectedLayerNames)
						}
						})
						?.Items("IK Pass layers", ikLayers);
				}

			entry.Set("layers", (long)facts.layers);
			entry.Set("states", (long)facts.states);
			entry.Set("transitions", (long)facts.transitions);
			entry.Set("anyStateTransitions", (long)facts.anyStateTransitions);
			entry.Set("parameters", (long)facts.parameters);
			entry.Set("behaviours", (long)facts.behaviours);
			entry.Finalise();
			return entry;
		}

		/// <summary>CTL003 AutoFix: removes the named parameters by value so removing one cannot shift another out from under it.</summary>
		static void RemoveParameters(AnimatorController controller, List<string> names)
		{
			if (controller == null) return;
			var toRemove = new List<AnimatorControllerParameter>();
			foreach (var parameter in controller.parameters)
				if (names.Contains(parameter.name)) toRemove.Add(parameter);
			foreach (var parameter in toRemove) controller.RemoveParameter(parameter);
		}

		static bool HasNoParameters(AnimatorController controller, List<string> names)
		{
			if (controller == null) return false;
			foreach (var parameter in controller.parameters)
				if (names.Contains(parameter.name)) return false;
			return true;
		}

		/// <summary>CTL005's Review Fix: turns off IK Pass only on the layers that generated the finding.</summary>
		static void SetIkPass(AnimatorController controller, List<int> layerIndices, bool enabled)
		{
			if (controller == null || layerIndices == null || layerIndices.Count == 0) return;
			var layers = controller.layers;
			bool changed = false;
			for (int i = 0; i < layerIndices.Count; i++)
			{
				int index = layerIndices[i];
				if (index < 0 || index >= layers.Length || layers[index].iKPass == enabled) continue;
				layers[index].iKPass = enabled;
				changed = true;
			}
			if (changed) controller.layers = layers;
		}

		static int CountIkLayers(AnimatorController controller, List<int> layerIndices)
		{
			if (controller == null || layerIndices == null) return int.MaxValue;
			var layers = controller.layers;
			int count = 0;
			for (int i = 0; i < layerIndices.Count; i++)
			{
				int index = layerIndices[i];
				if (index >= 0 && index < layers.Length && layers[index].iKPass) count++;
			}
			return count;
		}

		static int ResolveSourceLayerIndex(AnimatorControllerLayer[] layers, int layerIndex)
		{
			if (layers == null || layerIndex < 0 || layerIndex >= layers.Length) return layerIndex;
			var visited = new HashSet<int>();
			int current = layerIndex;
			while (current >= 0 && current < layers.Length && layers[current].syncedLayerIndex >= 0)
			{
				if (!visited.Add(current)) return layerIndex;
				int source = layers[current].syncedLayerIndex;
				if (source < 0 || source >= layers.Length) return current;
				current = source;
			}
			return current;
		}

		static void CountStateMachine(AnimatorStateMachine machine, AnimatorStateMachine parent,
			ref int states, ref int transitions, ref int anyStateTransitions, ref int behaviours, HashSet<int> visited)
		{
			if (machine == null || visited == null || !visited.Add(machine.GetInstanceID())) return;

			states += machine.states.Length;
			transitions += machine.anyStateTransitions.Length;
			anyStateTransitions += machine.anyStateTransitions.Length;
			transitions += machine.entryTransitions.Length;
			behaviours += machine.behaviours != null ? machine.behaviours.Length : 0;

			// Outgoing transitions from this child state machine are stored on its parent.
			if (parent != null)
			{
				var machineTransitions = parent.GetStateMachineTransitions(machine);
				if (machineTransitions != null)
					transitions += machineTransitions.Length;
			}

			foreach (var state in machine.states)
			{
				transitions += state.state.transitions.Length;
				behaviours += state.state.behaviours != null ? state.state.behaviours.Length : 0;
			}

			foreach (var child in machine.stateMachines)
				CountStateMachine(child.stateMachine, machine, ref states, ref transitions, ref anyStateTransitions, ref behaviours, visited);
		}

		static void CollectUsedParameters(AnimatorStateMachine machine, AnimatorStateMachine parent, HashSet<string> used)
		{
			CollectUsedParameters(machine, parent, used, default(AnimatorControllerLayer), false);
		}

		static void CollectUsedParametersForLayer(AnimatorStateMachine machine, AnimatorStateMachine parent,
			HashSet<string> used, AnimatorControllerLayer effectiveLayer)
		{
			CollectUsedParameters(machine, parent, used, effectiveLayer, true);
		}

		static void CollectUsedParameters(AnimatorStateMachine machine, AnimatorStateMachine parent,
			HashSet<string> used, AnimatorControllerLayer effectiveLayer, bool hasEffectiveLayer)
		{
			if (machine == null) return;

			foreach (var transition in machine.anyStateTransitions)
				AddConditions(transition.conditions, used);

			foreach (var transition in machine.entryTransitions)
				AddConditions(transition.conditions, used);

			// State-machine transitions live on the parent state machine.
			if (parent != null)
			{
				var machineTransitions = parent.GetStateMachineTransitions(machine);
				if (machineTransitions != null)
					foreach (var transition in machineTransitions)
						AddConditions(transition.conditions, used);
			}

			foreach (var state in machine.states)
			{
				foreach (var transition in state.state.transitions)
					AddConditions(transition.conditions, used);

				if (state.state.mirrorParameterActive && !string.IsNullOrEmpty(state.state.mirrorParameter))
					used.Add(state.state.mirrorParameter);
				if (state.state.speedParameterActive && !string.IsNullOrEmpty(state.state.speedParameter))
					used.Add(state.state.speedParameter);
				if (state.state.timeParameterActive && !string.IsNullOrEmpty(state.state.timeParameter))
					used.Add(state.state.timeParameter);
				if (state.state.cycleOffsetParameterActive && !string.IsNullOrEmpty(state.state.cycleOffsetParameter))
					used.Add(state.state.cycleOffsetParameter);

				Motion motion = state.state.motion;
				if (hasEffectiveLayer)
				{
					Motion overrideMotion = effectiveLayer.GetOverrideMotion(state.state);
					if (overrideMotion != null) motion = overrideMotion;
				}
				if (motion is BlendTree tree) CollectBlendTree(tree, used);
			}

			foreach (var child in machine.stateMachines)
				CollectUsedParameters(child.stateMachine, machine, used, effectiveLayer, hasEffectiveLayer);
		}

		static void CollectBlendTree(BlendTree tree, HashSet<string> used)
		{
			if (tree == null) return;
			if (!string.IsNullOrEmpty(tree.blendParameter)) used.Add(tree.blendParameter);
			if (!string.IsNullOrEmpty(tree.blendParameterY)) used.Add(tree.blendParameterY);
			var children = tree.children;
			for (int i = 0; i < children.Length; i++)
			{
				if (!string.IsNullOrEmpty(children[i].directBlendParameter))
					used.Add(children[i].directBlendParameter);
				if (children[i].motion is BlendTree nested) CollectBlendTree(nested, used);
			}
		}

		static void CollectControllerHeuristics(AnimatorController controller, List<string> conditionHeavy,
			List<string> writeDefaults, ref int maxConditions)
		{
			if (controller == null) return;
			var visited = new HashSet<int>();
			for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
			{
				var layer = controller.layers[layerIndex];
				if (layer.stateMachine == null) continue;
				if (layer.syncedLayerIndex >= 0) continue;
				CollectControllerHeuristics(layer.stateMachine, layer.name, conditionHeavy, writeDefaults, ref maxConditions, visited);
			}
		}

		static void CollectControllerHeuristics(AnimatorStateMachine machine, string prefix, List<string> conditionHeavy,
			List<string> writeDefaults, ref int maxConditions, HashSet<int> visited)
		{
			if (machine == null || visited == null || !visited.Add(machine.GetInstanceID())) return;
			foreach (var transition in machine.anyStateTransitions)
				RecordTransitionConditions(prefix + " / Any State", transition.conditions, conditionHeavy, ref maxConditions);
			foreach (var transition in machine.entryTransitions)
				RecordTransitionConditions(prefix + " / Entry", transition.conditions, conditionHeavy, ref maxConditions);
			foreach (var state in machine.states)
			{
				string stateLabel = prefix + " / " + state.state.name;
				if (state.state.writeDefaultValues) writeDefaults.Add(stateLabel);
				foreach (var transition in state.state.transitions)
					RecordTransitionConditions(stateLabel, transition.conditions, conditionHeavy, ref maxConditions);
			}
			foreach (var child in machine.stateMachines)
			{
				string childPrefix = prefix + " / " + child.stateMachine.name;
				var machineTransitions = machine.GetStateMachineTransitions(child.stateMachine);
				if (machineTransitions != null)
					foreach (var transition in machineTransitions)
						RecordTransitionConditions(childPrefix + " / StateMachine Transition", transition.conditions, conditionHeavy, ref maxConditions);
				CollectControllerHeuristics(child.stateMachine, childPrefix, conditionHeavy, writeDefaults, ref maxConditions, visited);
			}
		}

		static void RecordTransitionConditions(string label, AnimatorCondition[] conditions, List<string> heavy, ref int maxConditions)
		{
			int count = conditions != null ? conditions.Length : 0;
			if (count > maxConditions) maxConditions = count;
			if (count >= 4) heavy.Add(label + " (" + count + " conditions)");
		}

		static void AddConditions(AnimatorCondition[] conditions, HashSet<string> used)
		{
			if (conditions == null) return;
			foreach (var condition in conditions)
				if (!string.IsNullOrEmpty(condition.parameter)) used.Add(condition.parameter);
		}

		static void Controller(FixContext c, Action<UnityEditor.Animations.AnimatorController> mutate)
		{
			c.EnsureWritable();
			var ac = c.Asset as UnityEditor.Animations.AnimatorController;
			if (ac == null) throw new InvalidOperationException("Not an AnimatorController");
			mutate(ac);
			EditorUtility.SetDirty(ac);
			c.NeedsSave = true;
		}
		static void RemoveWriteDefaultsFromStateMachine(UnityEditor.Animations.AnimatorStateMachine sm)
		{
			if (sm == null) return;
			foreach (var child in sm.states) child.state.writeDefaultValues = false;
			foreach (var child in sm.stateMachines) RemoveWriteDefaultsFromStateMachine(child.stateMachine);
		}

		static bool WriteDefaultStateIdentitiesMatch(AnimatorController controller, List<string> expected)
		{
			if (controller == null || expected == null) return false;
			var conditionHeavy = new List<string>();
			var actual = new List<string>();
			int maxConditions = 0;
			CollectControllerHeuristics(controller, conditionHeavy, actual, ref maxConditions);
			if (actual.Count != expected.Count) return false;
			for (int i = 0; i < expected.Count; i++)
				if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal)) return false;
			return true;
		}

		static int CountWriteDefaultStates(AnimatorController controller)
		{
			if (controller == null) return int.MaxValue;
			int count = 0;
			var visited = new HashSet<int>();
			foreach (var layer in controller.layers)
				CountWriteDefaultStates(layer.stateMachine, visited, ref count);
			return count;
		}

		static void CountWriteDefaultStates(AnimatorStateMachine machine, HashSet<int> visited, ref int count)
		{
			if (machine == null || !visited.Add(machine.GetInstanceID())) return;
			foreach (var child in machine.states)
				if (child.state != null && child.state.writeDefaultValues) count++;
			foreach (var child in machine.stateMachines) CountWriteDefaultStates(child.stateMachine, visited, ref count);
		}

		static List<string> CaptureLayerNames(AnimatorController controller, List<int> indices)
		{
			var names = new List<string>(indices != null ? indices.Count : 0);
			if (controller == null || indices == null) return names;
			var layers = controller.layers;
			foreach (int index in indices)
				names.Add(index >= 0 && index < layers.Length ? (layers[index].name ?? string.Empty) : string.Empty);
			return names;
		}

		static bool IkTargetsStillMatch(AnimatorController controller, List<int> indices, List<string> expectedNames)
		{
			if (controller == null || indices == null || expectedNames == null || indices.Count == 0 || indices.Count != expectedNames.Count) return false;
			var layers = controller.layers;
			for (int i = 0; i < indices.Count; i++)
			{
				int index = indices[i];
				if (index < 0 || index >= layers.Length || !string.Equals(layers[index].name ?? string.Empty, expectedNames[i], StringComparison.Ordinal)) return false;
				if (!layers[index].iKPass) return false;
			}
			return true;
		}

		static bool IkTargetNamesStillMatch(AnimatorController controller, List<int> indices, List<string> expectedNames)
		{
			if (controller == null || indices == null || expectedNames == null || indices.Count != expectedNames.Count) return false;
			var layers = controller.layers;
			for (int i = 0; i < indices.Count; i++)
			{
				int index = indices[i];
				if (index < 0 || index >= layers.Length || !string.Equals(layers[index].name ?? string.Empty, expectedNames[i], StringComparison.Ordinal)) return false;
			}
			return true;
		}

		static bool CanSafelyRemoveParameters(AnimatorController controller, List<string> names, out string reason, bool forceRefresh = false)
		{
			reason = null;
			if (controller == null) { reason = "controller asset is unavailable"; return false; }
			if (names == null || names.Count == 0) { reason = "no parameter targets were recorded"; return false; }
			if (HasStateMachineBehaviours(controller)) { reason = "the controller contains StateMachineBehaviours whose parameter access cannot be proven absent"; return false; }

			var graphUsed = GetGraphUsedParameters(controller);
			foreach (string name in names)
			{
				if (string.IsNullOrEmpty(name) || graphUsed.Contains(name)) { reason = "controller graph usage changed or is ambiguous for parameter '" + name + "'"; return false; }
				bool exists = false;
				foreach (var parameter in controller.parameters) if (string.Equals(parameter.name, name, StringComparison.Ordinal)) { exists = true; break; }
				if (!exists) { reason = "parameter '" + name + "' no longer exists on the controller"; return false; }
			}

			if (!TryGetProjectSourceSnapshot(out var sourceTexts, out reason, forceRefresh)) return false;
			for (int i = 0; i < sourceTexts.Count; i++)
			{
				string source = sourceTexts[i];
				for (int n = 0; n < names.Count; n++)
					if (ContainsIdentifier(source, names[n]))
					{
						reason = "source code mentions parameter '" + names[n] + "'; cross-controller and reflection usage cannot be ruled out";
						return false;
					}
				if (HasDynamicAnimatorParameterAccess(source))
				{
					reason = "source code contains dynamic Animator parameter access that cannot be attributed to a specific controller parameter";
					return false;
				}
			}

			// Serialized project assets can also store parameter names (for example, visual scripting
			// graphs and custom data assets). Binary/mixed serialization cannot support a complete
			// name search, so it must fail closed rather than pretend the source scan is exhaustive.
			if (EditorSettings.serializationMode != SerializationMode.ForceText)
			{
				reason = "project asset serialization is not ForceText, so serialized parameter consumers cannot be ruled out";
				return false;
			}
			if (!TryGetSerializedProjectIdentifiers(out var serializedIdentifiers, out reason, forceRefresh)) return false;
			for (int n = 0; n < names.Count; n++)
			{
				if (!Regex.IsMatch(names[n], @"^[A-Za-z_][A-Za-z0-9_]*$"))
				{
					reason = "non-standard parameter name '" + names[n] + "' cannot be proven absent from the compact serialized-asset index";
					return false;
				}
				if (serializedIdentifiers.Contains(names[n]))
				{
					reason = "serialized project assets mention parameter '" + names[n] + "'; this could be a non-C# consumer";
					return false;
				}
			}
			return true;
		}

		static HashSet<string> GetGraphUsedParameters(AnimatorController controller)
		{
			var used = new HashSet<string>(StringComparer.Ordinal);
			var layers = controller.layers;
			var visitedMachines = new HashSet<int>();
			for (int i = 0; i < layers.Length; i++)
			{
				int sourceIndex = ResolveSourceLayerIndex(layers, i);
				if (sourceIndex < 0 || sourceIndex >= layers.Length) continue;
				var sourceLayer = layers[sourceIndex];
				if (sourceLayer.stateMachine == null || !visitedMachines.Add(sourceLayer.stateMachine.GetInstanceID())) continue;
				CollectUsedParametersForLayer(sourceLayer.stateMachine, null, used, layers[i]);
			}
			return used;
		}

		static bool HasStateMachineBehaviours(AnimatorController controller)
		{
			var visited = new HashSet<int>();
			foreach (var layer in controller.layers)
				if (HasStateMachineBehaviours(layer.stateMachine, visited)) return true;
			return false;
		}

		static bool HasStateMachineBehaviours(AnimatorStateMachine machine, HashSet<int> visited)
		{
			if (machine == null || !visited.Add(machine.GetInstanceID())) return false;
			if (machine.behaviours != null && machine.behaviours.Length > 0) return true;
			foreach (var state in machine.states)
				if (state.state != null && state.state.behaviours != null && state.state.behaviours.Length > 0) return true;
			foreach (var child in machine.stateMachines)
				if (HasStateMachineBehaviours(child.stateMachine, visited)) return true;
			return false;
		}

		static bool TryGetProjectSourceSnapshot(out List<string> texts, out string reason, bool forceRefresh = false)
		{
			texts = new List<string>();
			reason = null;
			if (!forceRefresh && s_SourceScanTexts != null) { texts = s_SourceScanTexts; return true; }
			if (!forceRefresh && !string.IsNullOrEmpty(s_SourceScanBlockedReason)) { reason = s_SourceScanBlockedReason; return false; }
			try
			{
				string projectRoot = Directory.GetParent(Application.dataPath).FullName;
				string[] sourceRoots = { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages"), Path.Combine(projectRoot, "Library", "PackageCache") };
				var files = new List<string>();
				for (int i = 0; i < sourceRoots.Length; i++)
				{
					if (!Directory.Exists(sourceRoots[i])) continue;
					files.AddRange(Directory.GetFiles(sourceRoots[i], "*.cs", SearchOption.AllDirectories));
				}
				files.Sort(StringComparer.OrdinalIgnoreCase);
				var fingerprint = new System.Text.StringBuilder();
				for (int i = 0; i < files.Count; i++)
				{
					var info = new FileInfo(files[i]);
					fingerprint.Append(files[i]).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
				}
				string currentFingerprint = fingerprint.ToString();
				// Compiled managed plug-ins cannot be searched for parameter names. Fail closed if one is present.
				string[] binaryRoots = { Path.Combine(projectRoot, "Assets", "Plugins"), Path.Combine(projectRoot, "Packages"), Path.Combine(projectRoot, "Library", "PackageCache") };
				for (int i = 0; i < binaryRoots.Length; i++)
				{
					if (!Directory.Exists(binaryRoots[i])) continue;
					string[] dlls = Directory.GetFiles(binaryRoots[i], "*.dll", SearchOption.AllDirectories);
					if (dlls.Length > 0)
					{
						reason = "user plug-in or package-cache assemblies are present and cannot be inspected for Animator parameter-name/hash references";
						s_SourceScanTexts = null;
						s_SourceScanFingerprint = null;
						s_SourceScanBlockedReason = reason;
						return false;
					}
				}

				if (string.Equals(s_SourceScanFingerprint, currentFingerprint, StringComparison.Ordinal) && s_SourceScanTexts != null)
				{
					texts = s_SourceScanTexts;
					return true;
				}

				var refreshed = new List<string>(files.Count);
				for (int i = 0; i < files.Count; i++) refreshed.Add(File.ReadAllText(files[i]));
				s_SourceScanFingerprint = currentFingerprint;
				s_SourceScanTexts = refreshed;
				s_SourceScanBlockedReason = null;
				texts = refreshed;
				return true;
			}
			catch (Exception e)
			{
				reason = "whole-project source scan was incomplete: " + e.Message;
				s_SourceScanTexts = null;
				s_SourceScanFingerprint = null;
				s_SourceScanBlockedReason = reason;
				return false;
			}
		}

		static string s_SourceScanBlockedReason;

		static bool TryGetSerializedProjectIdentifiers(out HashSet<string> identifiers, out string reason, bool forceRefresh = false)
		{
			identifiers = null;
			reason = null;
			if (!forceRefresh && s_SerializedIdentifiers != null) { identifiers = s_SerializedIdentifiers; return true; }
			if (!forceRefresh && !string.IsNullOrEmpty(s_SerializedScanBlockedReason)) { reason = s_SerializedScanBlockedReason; return false; }
			try
			{
				string projectRoot = Directory.GetParent(Application.dataPath).FullName;
				string[] roots = { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages"), Path.Combine(projectRoot, "Library", "PackageCache") };
				var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				{
					".asset", ".prefab", ".unity", ".overridecontroller", ".anim", ".playable",
					".json", ".xml", ".yaml", ".yml", ".uxml", ".uss", ".asmdef", ".asmref", ".shadergraph"
				};
				var files = new List<string>();
				for (int i = 0; i < roots.Length; i++)
				{
					if (!Directory.Exists(roots[i])) continue;
					foreach (string file in Directory.EnumerateFiles(roots[i], "*", SearchOption.AllDirectories))
						if (extensions.Contains(Path.GetExtension(file))) files.Add(file);
				}
				files.Sort(StringComparer.OrdinalIgnoreCase);
				var fingerprint = new System.Text.StringBuilder();
				for (int i = 0; i < files.Count; i++)
				{
					var info = new FileInfo(files[i]);
					fingerprint.Append(files[i]).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
				}
				string currentFingerprint = fingerprint.ToString();
				if (string.Equals(s_SerializedScanFingerprint, currentFingerprint, StringComparison.Ordinal) && s_SerializedIdentifiers != null)
				{
					identifiers = s_SerializedIdentifiers;
					return true;
				}

				// Index identifiers rather than retaining every serialized file's full text in memory.
				var refreshed = new HashSet<string>(StringComparer.Ordinal);
				for (int i = 0; i < files.Count; i++)
				{
					string serializedText = File.ReadAllText(files[i]);
					if (serializedText.IndexOf((char)0) >= 0 || serializedText.IndexOf((char)0xFFFD) >= 0)
						throw new InvalidDataException("serialized asset is not reliably readable as text: " + files[i]);
					MatchCollection matches = Regex.Matches(serializedText, @"(?<![A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*(?![A-Za-z0-9_])");
					for (int m = 0; m < matches.Count; m++) refreshed.Add(matches[m].Value);
				}
				s_SerializedScanFingerprint = currentFingerprint;
				s_SerializedIdentifiers = refreshed;
				s_SerializedScanBlockedReason = null;
				identifiers = refreshed;
				return true;
			}
			catch (Exception e)
			{
				reason = "serialized-asset scan was incomplete: " + e.Message;
				s_SerializedScanFingerprint = null;
				s_SerializedIdentifiers = null;
				s_SerializedScanBlockedReason = reason;
				return false;
			}
		}

		static string s_SerializedScanBlockedReason;
		static string s_SerializedScanFingerprint;
		static HashSet<string> s_SerializedIdentifiers;

		static string s_SourceScanFingerprint;

		static void InvalidateProjectEvidenceCaches()
		{
			s_SourceScanFingerprint = null;
			s_SourceScanTexts = null;
			s_SourceScanBlockedReason = null;
			s_SerializedScanFingerprint = null;
			s_SerializedIdentifiers = null;
			s_SerializedScanBlockedReason = null;
		}
		static List<string> s_SourceScanTexts;
		static readonly Regex s_AnimatorParameterApi = new Regex(@"\b(?:SetFloat|GetFloat|SetInteger|GetInteger|SetBool|GetBool|SetTrigger|ResetTrigger|StringToHash)\s*\(\s*([^,\)\r\n]+)", RegexOptions.Compiled);

		static bool HasDynamicAnimatorParameterAccess(string source)
		{
			var matches = s_AnimatorParameterApi.Matches(source ?? string.Empty);
			for (int i = 0; i < matches.Count; i++)
			{
				string argument = matches[i].Groups[1].Value.Trim();
				if (argument.Length == 0) return true;
				if ((argument[0] == '"' || argument[0] == '\'') && argument.Length >= 2) continue;
				// Even a hard-coded integer hash cannot be mapped back to a controller parameter with certainty.
				return true;
			}
			return false;
		}

		static bool ContainsIdentifier(string source, string identifier)
		{
			if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(identifier)) return false;
			return Regex.IsMatch(source, @"(?<![A-Za-z0-9_])" + Regex.Escape(identifier) + @"(?![A-Za-z0-9_])");
		}
	}
}

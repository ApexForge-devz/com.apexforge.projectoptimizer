using System;
using System.Collections.Generic;
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
							why: "A parameter that no transition, blend tree or state parameter reference uses is not needed by the controller graph itself. However, scripts and StateMachineBehaviours can still access parameters by name or hash, so this is a static-analysis warning rather than a safe automatic deletion.",
							fix: "Review Fix: delete parameters confirmed unused by scripts, StateMachineBehaviours and other runtime tooling.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
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
							risk: FixRisk.Unspecified,
							backupRequired: false
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

			Rules.Register(id: "CTL008", title: "Animator states use Write Defaults", category: AssetCategory.Controller,
							why: "Write Defaults causes a state to write default values for properties it does not animate. Mixed Write Defaults configurations can create harder-to-predict state behavior and may cause additional property writes.",
							fix: "Review Write Defaults usage and adopt a consistent project policy rather than changing it blindly.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
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
					var unused = facts.unusedParameters;
					entry.AddIssue("CTL003", facts.unusedParameters.Count + " parameter(s) not referenced by the controller graph",
						"Review Fix: remove them", 0, null,
						"Animator parameters are often part of a runtime contract with script. Confirm nothing "
						+ "sets or reads these by name/hash before removing them.",
						options: new List<FixOption>
						{
						new FixOption
						{
							Label = "Remove unused parameter(s)",
							ResultDescription = "Deletes every parameter listed below from the controller.",
							Recommended = true,
							Warning = "Breaks any script or StateMachineBehaviour that sets or reads these parameters by name or hash.",
							Apply = c =>
							{
								RemoveParameters((AnimatorController)c.Asset, unused);
								c.NeedsSave = true;
							},
							Verify = c => HasNoParameters(AssetDatabase.LoadAssetAtPath<AnimatorController>(c.AssetPath), unused)
						}
						})
						?.Items("Potentially unused parameters", unused);
				}

			int anyStateWarn = Mathf.Max(4, ctx.Settings.animatorStateWarn / 4);
			using (RuleTiming.Begin("CTL004", entry.Path))
				if (facts.anyStateTransitions > anyStateWarn)
					entry.AddIssue("CTL004", facts.anyStateTransitions + " Any State transitions", "review under " + anyStateWarn, 0, null, "This is a review heuristic. Any State usage is legitimate for many controller designs.");

			using (RuleTiming.Begin("CTL006", entry.Path))
				if (facts.conditionHeavyTransitions.Count > 0)
					entry.AddIssue("CTL006", facts.conditionHeavyTransitions.Count + " transition(s) with " + facts.maxTransitionConditions + "+ conditions",
						"Review complex condition sets", 0, null, "Static heuristic; complex conditions are not inherently incorrect.")?.Items("Condition-heavy transitions", facts.conditionHeavyTransitions);

			using (RuleTiming.Begin("CTL008", entry.Path))
				if (facts.writeDefaultStates.Count > 0)
					entry.AddIssue("CTL008", facts.writeDefaultStates.Count + " state(s) with Write Defaults enabled",
						"Review project-wide Write Defaults policy", 0, null, "This is intentionally advisory; changing Write Defaults can alter animation behavior.",
						options: new List<FixOption> { new FixOption {
							Label = "Disable Write Defaults",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => Controller(c, ac => { foreach (var layer in ac.layers) RemoveWriteDefaultsFromStateMachine(layer.stateMachine); })
						} }
					)?.Items("Write Defaults states", facts.writeDefaultStates);

			using (RuleTiming.Begin("CTL005", entry.Path))
				if (facts.ikLayers.Count > 0)
				{
					var ikLayers = facts.ikLayers;
					var ikLayerIndices = facts.ikLayerIndices;
					entry.AddIssue("CTL005", facts.ikLayers.Count + " layer(s) with IK Pass enabled",
						"Review Fix: disable IK Pass on these layers", 0, null,
						"IK Pass triggers an IK evaluation pass and can invoke OnAnimatorIK. Disabling it stops OnAnimatorIK being called for these layers.",
						options: new List<FixOption>
						{
						new FixOption
						{
							Label = "Disable IK Pass",
							ResultDescription = "Disables IK Pass on the layers listed below.",
							Recommended = true,
							Warning = "Any script relying on OnAnimatorIK being called for these layers will stop receiving it.",
							Apply = c =>
							{
								SetIkPass((AnimatorController)c.Asset, ikLayerIndices, false);
								c.NeedsSave = true;
							},
							Verify = c => CountIkLayers(AssetDatabase.LoadAssetAtPath<AnimatorController>(c.AssetPath), ikLayerIndices) == 0
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

		/// <summary>CTL003's Review Fix: removes the named parameters, by value rather than by index so removing one cannot shift another out from under it.</summary>
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
	}
}

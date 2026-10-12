using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// A single imported-model measurement shared by ModelAnalyzer and AnimationAnalyzer.
	/// Both analyzers used to load and traverse the same imported object graph independently.
	/// Keeping one cache row for the combined measurement makes that expensive work happen once.
	/// </summary>
	[Serializable]
	public sealed class ModelAnalysisFacts
	{
		public long vertices;
		public long largestMesh;
		public long triangles;
		public int submeshes;
		public int meshCount;
		public bool hasBlendShapes;
		public bool hasSkinnedMesh;
		public bool hasClips;
		public bool anyNormalMap;
		public bool hasImportedCamera;
		public bool hasImportedLight;
		public bool hasVisibilityCurves;
		public int hierarchyObjects;
		public long runtimeSize;
		public long meshCpuBytes;
		public long tangentBytes;
		public long secondaryUvBytes;
		public long indexBytes32;
		public long indexBytes16;

		public long animationKeyCount;
		public long animationCurveCount;
		public long constantScaleCurves;
		public long nonConstantScaleCurves;
		public long discreteCurveCount;
		public long objectReferenceCurveCount;
		public long objectReferenceKeyCount;
		public long animationEventCount;
		public int maxClipCurveCount;
		public int clipCount;
		public double totalAnimationLength;
		public int transformPathCount;
		public bool hasEulerRotationCurves;

		public bool isReadable;
		public ModelImporterMeshCompression meshCompression;
		public ModelImporterAnimationType animationType;
		public bool importAnimation;
		public bool importBlendShapes;
		public MeshOptimizationFlags meshOptimizationFlags;
		public ModelImporterIndexFormat indexFormat;
		public bool optimizeBones;
		public bool optimizeGameObjects;
		public ModelImporterMaterialImportMode materialImportMode;
		public ModelImporterMaterialLocation materialLocation;
		public ModelImporterSkinWeights skinWeights;
		public bool generateSecondaryUV;
		public int maxBonesPerVertex;
		public float minBoneWeight;
		public bool importCameras;
		public bool importLights;
		public bool importVisibility;
		public ModelImporterTangents importTangents;
		public ModelImporterAvatarSetup avatarSetup;
		public bool weldVertices;
		public ModelImporterAnimationCompression animationCompression;
		public bool removeConstantScaleCurves;
		public bool resampleCurves;
		public ModelImporterHumanoidOversampling humanoidOversampling;
	}

	public static class ModelAnalysisShared
	{

		public static ModelAnalysisFacts GetGeometry(string assetPath, ScanContext ctx)
		{
			return Get(assetPath, ctx, false);
		}

		public static ModelAnalysisFacts GetAnimation(string assetPath, ScanContext ctx)
		{
			return Get(assetPath, ctx, true);
		}

		static ModelAnalysisFacts Get(string assetPath, ScanContext ctx, bool animation)
		{
			string hash = ctx.GetDependencyHash(assetPath);
			string suffix = animation ? "animation" : "geometry";
			string cacheKey = ScanCache.ScopedKey(assetPath, typeof(ModelAnalysisFacts).FullName + "|" + suffix);
			if (ctx.Cache != null && ctx.Cache.TryGet(cacheKey, hash, out ModelAnalysisFacts cached))
				return cached;

			var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
			if (importer == null) return null;

			var facts = Measure(assetPath, ctx, importer, animation);
			if (facts != null) ctx.Cache?.Set(cacheKey, facts, hash);
			return facts;
		}

		static ModelAnalysisFacts Measure(string assetPath, ScanContext ctx, ModelImporter importer, bool animation)
		{
			var facts = new ModelAnalysisFacts
			{
				isReadable = importer.isReadable,
				meshCompression = importer.meshCompression,
				animationType = importer.animationType,
				importAnimation = importer.importAnimation,
				importBlendShapes = importer.importBlendShapes,
				meshOptimizationFlags = importer.meshOptimizationFlags,
				indexFormat = importer.indexFormat,
				optimizeBones = importer.optimizeBones,
				optimizeGameObjects = importer.optimizeGameObjects,
				materialImportMode = importer.materialImportMode,
				materialLocation = importer.materialLocation,
				skinWeights = importer.skinWeights,
				generateSecondaryUV = importer.generateSecondaryUV,
				maxBonesPerVertex = importer.maxBonesPerVertex,
				minBoneWeight = importer.minBoneWeight,
				importCameras = importer.importCameras,
				importLights = importer.importLights,
				importVisibility = importer.importVisibility,
				importTangents = importer.importTangents,
				avatarSetup = importer.avatarSetup,
				weldVertices = importer.weldVertices,
				animationCompression = importer.animationCompression,
				removeConstantScaleCurves = importer.removeConstantScaleCurves,
				resampleCurves = importer.resampleCurves,
				humanoidOversampling = importer.humanoidOversampling,
				transformPathCount = importer.transformPaths != null ? importer.transformPaths.Length : 0
			};

			UnityEngine.Object[] objects = ctx.GetLoadedObjects(assetPath);
			GameObject root = null;
			for (int i = 0; i < objects.Length; i++)
			{
				root = objects[i] as GameObject;
				if (root != null) break;
			}
			if (root == null) root = AssetLoadUtility.LoadAssetAtPath<GameObject>(assetPath);

			Dictionary<string, Vector3> scales = null;
			if (animation && root != null)
			{
				var transforms = root.GetComponentsInChildren<Transform>(true);
				var scaleCapacity = transforms != null ? Mathf.Max(4, transforms.Length) : 4;
				scales = new Dictionary<string, Vector3>(scaleCapacity, StringComparer.Ordinal);
				for (int i = 0; transforms != null && i < transforms.Length; i++)
				{
					var t = transforms[i];
					if (t != null) scales[GetTransformPath(root.transform, t)] = t.localScale;
				}
			}
			for (int i = 0; i < objects.Length; i++)
			{
				var obj = objects[i];
				var mesh = obj as Mesh;
				if (!animation && mesh != null)
				{
					facts.meshCount++;
					facts.vertices += mesh.vertexCount;
					if (mesh.vertexCount > facts.largestMesh) facts.largestMesh = mesh.vertexCount;
					if (mesh.subMeshCount > facts.submeshes) facts.submeshes = mesh.subMeshCount;
					long meshIndexBytes = 0;
					for (int sub = 0; sub < mesh.subMeshCount; sub++)
					{
						long indexCount = mesh.GetIndexCount(sub);
						facts.triangles += indexCount / 3;
						if (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32) { long bytes = indexCount * 4L; facts.indexBytes32 += bytes; meshIndexBytes += bytes; }
						else { long bytes = indexCount * 2L; facts.indexBytes16 += bytes; meshIndexBytes += bytes; }
					}
					var attrs = mesh.GetVertexAttributes();
					for (int a = 0; a < attrs.Length; a++)
					{
						long bytes = (long)mesh.vertexCount * VertexAttributeBytes(attrs[a].format, attrs[a].dimension);
						if (attrs[a].attribute == UnityEngine.Rendering.VertexAttribute.Tangent) facts.tangentBytes += bytes;
						if (attrs[a].attribute == UnityEngine.Rendering.VertexAttribute.TexCoord1) facts.secondaryUvBytes += bytes;
					}
					if (mesh.blendShapeCount > 0) facts.hasBlendShapes = true;
					for (int stream = 0; stream < 4; stream++) { int stride = mesh.GetVertexBufferStride(stream); if (stride <= 0) break; facts.meshCpuBytes += (long)stride * mesh.vertexCount; }
					facts.meshCpuBytes += meshIndexBytes;
					long size = UnityEditorUtility.RuntimeSizeOf(mesh);
					if (size > 0) facts.runtimeSize += size;
					continue;
				}

				var clip = obj as AnimationClip;
				if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
				{
					facts.hasClips = true;
					if (animation) MeasureClip(facts, root, scales, clip);
				}
				if (!animation)
				{
					var material = obj as Material;
					if (material != null && MaterialUtility.HasNormalMap(material)) facts.anyNormalMap = true;
				}
			}

			if (animation && root != null)
			{
				var animationSkinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				for (int i = 0; animationSkinned != null && i < animationSkinned.Length; i++)
					if (animationSkinned[i] != null) { facts.hasSkinnedMesh = true; break; }
			}

			if (!animation && root != null)
			{
				var transforms = root.GetComponentsInChildren<Transform>(true);
				facts.hierarchyObjects = transforms != null ? transforms.Length : 0;
				var cameras = root.GetComponentsInChildren<Camera>(true);
				var lights = root.GetComponentsInChildren<Light>(true);
				facts.hasImportedCamera = cameras != null && cameras.Length > 0;
				facts.hasImportedLight = lights != null && lights.Length > 0;
				var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				for (int i = 0; skinned != null && i < skinned.Length; i++)
					if (skinned[i] != null) { facts.hasSkinnedMesh = true; break; }
				var renderers = root.GetComponentsInChildren<Renderer>(true);
				for (int i = 0; renderers != null && i < renderers.Length && !facts.anyNormalMap; i++)
				{
					var materials = renderers[i] != null ? renderers[i].sharedMaterials : null;
					for (int m = 0; materials != null && m < materials.Length; m++)
						if (MaterialUtility.HasNormalMap(materials[m])) { facts.anyNormalMap = true; break; }
				}
			}

			return facts;
		}

		static int VertexAttributeBytes(UnityEngine.Rendering.VertexAttributeFormat format, int dimension)
		{
			int componentBytes;
			switch (format)
			{
				case UnityEngine.Rendering.VertexAttributeFormat.Float32:
				case UnityEngine.Rendering.VertexAttributeFormat.UInt32:
				case UnityEngine.Rendering.VertexAttributeFormat.SInt32: componentBytes = 4; break;
				case UnityEngine.Rendering.VertexAttributeFormat.Float16:
				case UnityEngine.Rendering.VertexAttributeFormat.UNorm16:
				case UnityEngine.Rendering.VertexAttributeFormat.SNorm16:
				case UnityEngine.Rendering.VertexAttributeFormat.UInt16:
				case UnityEngine.Rendering.VertexAttributeFormat.SInt16: componentBytes = 2; break;
				case UnityEngine.Rendering.VertexAttributeFormat.UNorm8:
				case UnityEngine.Rendering.VertexAttributeFormat.SNorm8:
				case UnityEngine.Rendering.VertexAttributeFormat.UInt8:
				case UnityEngine.Rendering.VertexAttributeFormat.SInt8: componentBytes = 1; break;
				default: componentBytes = 4; break;
			}
			return componentBytes * Mathf.Clamp(dimension, 1, 4);
		}

		static void MeasureClip(ModelAnalysisFacts facts, GameObject root, Dictionary<string, Vector3> scales, AnimationClip clip)
		{
			facts.clipCount++;
			facts.totalAnimationLength += clip.length;
			var bindings = AnimationUtility.GetCurveBindings(clip);
			facts.animationCurveCount += bindings.Length;
			if (bindings.Length > facts.maxClipCurveCount) facts.maxClipCurveCount = bindings.Length;
			for (int i = 0; i < bindings.Length; i++)
			{
				var binding = bindings[i];
				if (binding.isDiscreteCurve) facts.discreteCurveCount++;
				if (string.Equals(binding.propertyName, "m_Enabled", StringComparison.Ordinal)
					&& binding.type != null && typeof(Renderer).IsAssignableFrom(binding.type))
					facts.hasVisibilityCurves = true;

				if (IsEulerRotationProperty(binding.propertyName)) facts.hasEulerRotationCurves = true;
				var curve = AnimationUtility.GetEditorCurve(clip, binding);
				if (curve == null) continue;
				facts.animationKeyCount += curve.length;
				if (!binding.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal)) continue;

				if (IsConstant(curve, out float constantScaleValue))
				{
					if (MatchesInitialScale(scales, binding.path, binding.propertyName, constantScaleValue))
						facts.constantScaleCurves++;
				}
				else facts.nonConstantScaleCurves++;
			}

			var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
			facts.objectReferenceCurveCount += objectBindings.Length;
			for (int i = 0; i < objectBindings.Length; i++)
			{
				var objectCurve = AnimationUtility.GetObjectReferenceCurve(clip, objectBindings[i]);
				if (objectCurve != null) facts.objectReferenceKeyCount += objectCurve.Length;
			}
			facts.animationEventCount += AnimationUtility.GetAnimationEvents(clip).Length;
		}

		static bool MatchesInitialScale(Dictionary<string, Vector3> scales, string path, string propertyName, float value)
		{
			if (!scales.TryGetValue(path ?? string.Empty, out var scale)) return false;
			if (propertyName.EndsWith(".x", StringComparison.OrdinalIgnoreCase)) return Mathf.Approximately(scale.x, value);
			if (propertyName.EndsWith(".y", StringComparison.OrdinalIgnoreCase)) return Mathf.Approximately(scale.y, value);
			if (propertyName.EndsWith(".z", StringComparison.OrdinalIgnoreCase)) return Mathf.Approximately(scale.z, value);
			return false;
		}

		static string GetTransformPath(Transform root, Transform current)
		{
			if (current == root) return string.Empty;
			var parts = new List<string>(8);
			var node = current;
			while (node != null && node != root)
			{
				parts.Add(node.name);
				node = node.parent;
			}
			parts.Reverse();
			return string.Join("/", parts);
		}

		static bool IsEulerRotationProperty(string propertyName)
		{
			return !string.IsNullOrEmpty(propertyName)
				&& (propertyName.IndexOf("m_LocalEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
					|| propertyName.IndexOf("localEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		static bool IsConstant(AnimationCurve curve, out float value)
		{
			value = 0f;
			if (curve == null || curve.length == 0) return false;
			value = curve.keys[0].value;
			var keys = curve.keys;
			for (int i = 1; i < keys.Length; i++)
				if (!Mathf.Approximately(keys[i].value, value)) return false;
			return true;
		}
	}
}

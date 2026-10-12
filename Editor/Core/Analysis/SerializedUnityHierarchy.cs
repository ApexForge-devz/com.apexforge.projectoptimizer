using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Minimal UnityYAML reader for prefab/scene hierarchy facts.
    /// It is intentionally not a general YAML implementation: it recognizes the serialized
    /// structures needed by PrefabSceneAnalyzer and produces the same HierarchyFacts shape used by
    /// the existing Unity-object path. The parser never touches UnityEngine/UnityEditor APIs.
    /// </summary>
    public static class SerializedUnityHierarchy
    {
        public struct MeshStats
        {
            public int VertexCount;
            public int SubMeshCount;
            public bool Known;
        }

        [Flags]
        public enum NativeCapability
        {
            None = 0,
            MeshStats = 1 << 0,
            LightSemantics = 1 << 1,
            MeshColliderState = 1 << 2,
            ParticleState = 1 << 3,
            RigidbodyState = 1 << 4,
            UiInputSemantics = 1 << 5,
            StopNaNCameraState = 1 << 6
        }

        public sealed class ParseResult
        {
            public PrefabSceneAnalyzer.HierarchyFacts Facts;
            public NativeCapability NativeCapabilities;
            public bool Complete => Facts != null && NativeCapabilities == NativeCapability.None;
            public string Limitation;
            public int ObjectCount;
        }

        sealed class SerializedLine
        {
            public string Text;
            public int Indent;
        }

        sealed class Block
        {
            public int ClassId;
            public long FileId;
            public readonly List<SerializedLine> Lines = new List<SerializedLine>(16);

            public long GameObjectId;
            public string Name = string.Empty;
            public long FatherId;
            public bool HasFather;
            public int StaticFlags;
            public string ScriptGuid = string.Empty;
            public string ScriptPath = string.Empty;
            public readonly HashSet<long> ComponentIds = new HashSet<long>();

            public bool IsRenderer;
            public bool IsSkinned;
            public bool IsMeshFilter;
            public bool IsMeshCollider;
            public bool IsParticle;
            public bool IsAnimator;
            public bool IsCamera;
            public bool IsLight;
            public bool IsRigidbody;
            public bool IsJoint;
            public bool IsLodGroup;
            public bool IsCanvas;
            public bool IsGraphic;
            public bool IsGraphicRaycaster;
            public bool IsLayout;
            public bool HasUnknownMonoBehaviour;

            public bool HasEventHandler;
            public bool UnknownPointerHandler;
            public bool RaycastTarget;
            public bool UpdateWhenOffscreen;
            public bool HasAnimatorCulling;
            public int AnimatorCulling;

            public bool HasRigidbodyKinematic;
            public bool RigidbodyKinematic;
            public bool HasSolverIterations;
            public int SolverIterations;
            public bool HasCollisionDetection;
            public int CollisionDetection;

            public bool ParticleMaxKnown;
            public int ParticleMax;
            public int ParticleExpensiveModules;
            public readonly List<string> ExpensiveParticleModuleNames = new List<string>(3);

            public bool LightEnabled;
            public bool LightShadowsKnown;
            public int LightShadows;
            public bool LightBakeKnown;
            public int LightBake;

            public bool StopNaNKnown;
            public bool StopNaN;
            public bool StopNaNBaseKnown;
            public bool StopNaNIsBase;

            public readonly List<string> MaterialGuids = new List<string>(4);
            public string MeshGuid = string.Empty;
            public bool MeshReferenceKnown;

            public bool MeshColliderConvexKnown;
            public bool MeshColliderConvex;

            public readonly List<string> ExternalGuids = new List<string>(4);
            public string MissingPrefabGuid = string.Empty;
            public bool MissingPrefab;
        }

        sealed class TransformInfo
        {
            public long FileId;
            public long GameObjectId;
            public long ParentId;
            public string Name = string.Empty;
        }

        static readonly HashSet<int> k_Renderers = new HashSet<int> { 23, 96, 120, 137, 199, 212, 227 };
        static readonly HashSet<int> k_Joints = new HashSet<int> { 57, 59, 138, 144, 145, 153, 229, 230, 231, 232, 233, 234, 235, 254, 255, 256, 257 };
        static readonly HashSet<int> k_Colliders = new HashSet<int> { 64, 154 };

        public static ParseResult TryExtract(
            string projectRoot,
            string assetPath,
            bool isScene,
            Func<string, string> resolveGuidToPath,
            Func<string, MeshStats> resolveMeshStats)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(assetPath)) return null;

            string fullPath;
            try
            {
                fullPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            }
            catch
            {
                return null;
            }

            if (!File.Exists(fullPath)) return null;

            try
            {
                var blocks = ReadBlocks(fullPath, resolveGuidToPath);
                if (blocks == null || blocks.Count == 0)
                    return new ParseResult { Facts = null, NativeCapabilities = NativeCapability.MeshStats, Limitation = "The asset is not Unity text serialization." };
                return BuildResult(blocks, isScene, resolveGuidToPath, resolveMeshStats);
            }
            catch (Exception e)
            {
                return new ParseResult { Facts = null, NativeCapabilities = NativeCapability.MeshStats, Limitation = e.Message };
            }
        }

        static List<Block> ReadBlocks(string fullPath, Func<string, string> resolveGuidToPath)
        {
            var blocks = new List<Block>(256);
            Block current = null;
            using (var reader = new StreamReader(fullPath, Encoding.UTF8, true, 1024 * 64))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                    {
                        if (current != null)
                        {
                            FinalizeBlock(current, resolveGuidToPath);
                            blocks.Add(current);
                        }
                        current = ParseHeader(line);
                        continue;
                    }
                    if (current == null || line.Length == 0) continue;

                    string trim = line.TrimStart();
                    if (trim.StartsWith("%YAML", StringComparison.Ordinal) || trim.StartsWith("%TAG", StringComparison.Ordinal)) continue;
                    int indent = line.Length - trim.Length;
                    current.Lines.Add(new SerializedLine { Text = trim, Indent = indent });
                }
            }
            if (current != null)
            {
                FinalizeBlock(current, resolveGuidToPath);
                blocks.Add(current);
            }
            return blocks.Count == 0 ? null : blocks;
        }

        static Block ParseHeader(string line)
        {
            int typeStart = 7;
            int typeEnd = line.IndexOf(' ', typeStart);
            int amp = typeEnd >= 0 ? line.IndexOf('&', typeEnd + 1) : -1;
            int classId = 0;
            long fileId = 0;
            if (typeEnd > typeStart) int.TryParse(line.Substring(typeStart, typeEnd - typeStart), out classId);
            if (amp >= 0) long.TryParse(line.Substring(amp + 1).Trim(), out fileId);
            return new Block { ClassId = classId, FileId = fileId };
        }

        static void FinalizeBlock(Block b, Func<string, string> resolveGuidToPath)
        {
            if (b.ClassId == 1)
            {
                ParseGameObject(b);
                return;
            }
            if (b.ClassId == 4)
            {
                ParseTransform(b);
                return;
            }
            if (b.ClassId == 1001)
            {
                for (int i = 0; i < b.Lines.Count; i++)
                {
                    string line = b.Lines[i].Text;
                    if (!line.StartsWith("m_SourcePrefab:", StringComparison.Ordinal)) continue;
                    b.MissingPrefabGuid = ExtractGuid(line);
                    if (!string.IsNullOrEmpty(b.MissingPrefabGuid) && resolveGuidToPath != null)
                        b.MissingPrefab = string.IsNullOrEmpty(resolveGuidToPath(b.MissingPrefabGuid));
                }
                return;
            }

            ParseComponentHeader(b, resolveGuidToPath);
            if (b.ClassId == 20) b.IsCamera = true;
            if (k_Renderers.Contains(b.ClassId)) b.IsRenderer = true;
            if (b.ClassId == 137) b.IsSkinned = true;
            if (b.ClassId == 33) b.IsMeshFilter = true;
            if (b.ClassId == 54) b.IsRigidbody = true;
            if (k_Joints.Contains(b.ClassId)) b.IsJoint = true;
            if (b.ClassId == 64) b.IsMeshCollider = true;
            if (b.ClassId == 95) b.IsAnimator = true;
            if (b.ClassId == 108) b.IsLight = true;
            if (b.ClassId == 198) b.IsParticle = true;
            if (b.ClassId == 205) b.IsLodGroup = true;
            if (b.ClassId == 223) b.IsCanvas = true;
            if (b.ClassId == 114 && string.IsNullOrEmpty(b.ScriptPath)) b.HasUnknownMonoBehaviour = true;

            ParseComponentLines(b, resolveGuidToPath);
        }

        static void ParseGameObject(Block b)
        {
            for (int i = 0; i < b.Lines.Count; i++)
            {
                string line = b.Lines[i].Text;
                if (line.StartsWith("m_Name:", StringComparison.Ordinal)) b.Name = Unquote(ValueAfterColon(line));
                else if (line.StartsWith("m_StaticEditorFlags:", StringComparison.Ordinal)) TryInt(ValueAfterColon(line), out b.StaticFlags);
                else if (line.StartsWith("- component: {fileID:", StringComparison.Ordinal))
                {
                    string idText = Between(line, "{fileID:", "}");
                    if (long.TryParse(idText.Trim(), out long id)) b.ComponentIds.Add(id);
                }
            }
        }

        static void ParseTransform(Block b)
        {
            for (int i = 0; i < b.Lines.Count; i++)
            {
                string line = b.Lines[i].Text;
                if (line.StartsWith("m_GameObject:", StringComparison.Ordinal)) TryRefFileId(line, out b.GameObjectId);
                else if (line.StartsWith("m_Father:", StringComparison.Ordinal)) b.HasFather = TryRefFileId(line, out b.FatherId);
            }
        }

        static void ParseComponentHeader(Block b, Func<string, string> resolveGuidToPath)
        {
            for (int i = 0; i < b.Lines.Count; i++)
            {
                string line = b.Lines[i].Text;
                if (line.StartsWith("m_GameObject:", StringComparison.Ordinal)) TryRefFileId(line, out b.GameObjectId);
                if (!line.StartsWith("m_Script:", StringComparison.Ordinal)) continue;
                b.ScriptGuid = ExtractGuid(line);
                if (!string.IsNullOrEmpty(b.ScriptGuid) && resolveGuidToPath != null)
                    b.ScriptPath = resolveGuidToPath(b.ScriptGuid) ?? string.Empty;
            }
        }

        static void ParseComponentLines(Block b, Func<string, string> resolveGuidToPath)
        {
            bool inMaterials = false;
            string particleModule = null;
            bool anyUnknownPointerScript = false;
            bool hasRaycastField = false;

            for (int i = 0; i < b.Lines.Count; i++)
            {
                var item = b.Lines[i];
                string line = item.Text;
                int indent = item.Indent;

                if (inMaterials && indent <= 2 && !line.StartsWith("- {fileID:", StringComparison.Ordinal)) inMaterials = false;
                if (b.IsParticle && line.EndsWith("Module:", StringComparison.Ordinal)) particleModule = null;
                else if (particleModule != null && indent <= 2 && !line.StartsWith("m_", StringComparison.Ordinal)) particleModule = null;

                if (line.StartsWith("m_Materials:", StringComparison.Ordinal))
                {
                    inMaterials = true;
                    continue;
                }
                if (inMaterials && line.StartsWith("- {fileID:", StringComparison.Ordinal))
                {
                    string guid = ExtractGuid(line);
                    if (!string.IsNullOrEmpty(guid)) b.MaterialGuids.Add(guid);
                    continue;
                }

                if (line.StartsWith("m_Mesh:", StringComparison.Ordinal))
                {
                    b.MeshGuid = ExtractGuid(line);
                    b.MeshReferenceKnown = !string.IsNullOrEmpty(b.MeshGuid) || line.IndexOf("fileID: 0", StringComparison.Ordinal) >= 0;
                    continue;
                }

                if (line.StartsWith("m_Convex:", StringComparison.Ordinal) && b.IsMeshCollider)
                {
                    b.MeshColliderConvexKnown = TryBool(line, out b.MeshColliderConvex);
                    continue;
                }

                if (b.ClassId == 114 && line.StartsWith("m_Script:", StringComparison.Ordinal)) continue;

                if (b.ClassId == 114)
                {
                    string script = b.ScriptPath.ToLowerInvariant();
                    if (script.EndsWith("graphicraycaster.cs", StringComparison.Ordinal)) b.IsGraphicRaycaster = true;
                    if (script.Contains("layoutgroup.cs") || script.EndsWith("contentsizefitter.cs", StringComparison.Ordinal) || script.EndsWith("layoutelement.cs", StringComparison.Ordinal)) b.IsLayout = true;
                    if (script.Contains("selectable.cs") || script.Contains("eventtrigger.cs") || script.Contains("button.cs") || script.Contains("toggle.cs") || script.Contains("slider.cs") || script.Contains("scrollrect.cs") || script.Contains("inputfield.cs") || script.Contains("dropdown.cs") || script.Contains("scrollbar.cs")) b.HasEventHandler = true;
                    if (script.Contains("universaladditionalcameradata"))
                    {
                        if (line.StartsWith("m_StopNaN:", StringComparison.Ordinal) || line.StartsWith("stopNaN:", StringComparison.Ordinal))
                            b.StopNaNKnown = TryBool(line, out b.StopNaN);
                        if (line.StartsWith("m_RenderType:", StringComparison.Ordinal) || line.StartsWith("renderType:", StringComparison.Ordinal))
                        {
                            if (TryInt(ValueAfterColon(line), out int renderType))
                            {
                                b.StopNaNBaseKnown = true;
                                b.StopNaNIsBase = renderType == 0;
                            }
                        }
                    }
                    if (line.StartsWith("m_RaycastTarget:", StringComparison.Ordinal))
                    {
                        b.IsGraphic = true;
                        b.RaycastTarget = ParseBool(ValueAfterColon(line));
                        hasRaycastField = true;
                    }
                    if (line.StartsWith("m_UpdateWhenOffscreen:", StringComparison.Ordinal)) b.UpdateWhenOffscreen = ParseBool(ValueAfterColon(line));
                    if (line.StartsWith("m_CullingMode:", StringComparison.Ordinal) && TryInt(ValueAfterColon(line), out int culling))
                    {
                        b.HasAnimatorCulling = true;
                        b.AnimatorCulling = culling;
                    }
                }

                if (b.ClassId == 20 && line.StartsWith("m_RenderingPath:", StringComparison.Ordinal))
                {
                    // Camera extraction is otherwise fully source-backed; URP supplement is the only
                    // imported-camera semantic and is handled below as a conservative fallback.
                }

                if (b.IsRigidbody)
                {
                    if (line.StartsWith("m_IsKinematic:", StringComparison.Ordinal))
                    {
                        b.HasRigidbodyKinematic = TryBool(line, out b.RigidbodyKinematic);
                    }
                    else if (line.StartsWith("m_SolverIterations:", StringComparison.Ordinal) && TryInt(ValueAfterColon(line), out int iterations))
                    {
                        b.HasSolverIterations = true;
                        b.SolverIterations = iterations;
                    }
                    else if (line.StartsWith("m_CollisionDetection:", StringComparison.Ordinal) && TryInt(ValueAfterColon(line), out int mode))
                    {
                        b.HasCollisionDetection = true;
                        b.CollisionDetection = mode;
                    }
                }

                if (b.IsAnimator && line.StartsWith("m_CullingMode:", StringComparison.Ordinal) && TryInt(ValueAfterColon(line), out int cullingMode))
                {
                    b.HasAnimatorCulling = true;
                    b.AnimatorCulling = cullingMode;
                }

                if (b.IsLight)
                {
                    if (line.StartsWith("m_Enabled:", StringComparison.Ordinal)) b.LightEnabled = ParseBool(ValueAfterColon(line));
                    else if (line.StartsWith("m_Shadows:", StringComparison.Ordinal) && TryInt(ValueAfterColon(line), out int shadows))
                    {
                        b.LightShadowsKnown = true;
                        b.LightShadows = shadows;
                    }
                    else if ((line.StartsWith("m_Lightmapping:", StringComparison.Ordinal) || line.StartsWith("m_LightmappingMode:", StringComparison.Ordinal)) && TryInt(ValueAfterColon(line), out int bake))
                    {
                        b.LightBakeKnown = true;
                        b.LightBake = bake;
                    }
                }

                if (b.IsParticle)
                {
                    if (line.EndsWith("CollisionModule:", StringComparison.Ordinal) || line.StartsWith("CollisionModule:", StringComparison.Ordinal)) particleModule = "Collision";
                    else if (line.EndsWith("LightsModule:", StringComparison.Ordinal) || line.StartsWith("LightsModule:", StringComparison.Ordinal)) particleModule = "Lights";
                    else if (line.EndsWith("TrailModule:", StringComparison.Ordinal) || line.StartsWith("TrailModule:", StringComparison.Ordinal)) particleModule = "Trails";
                    else if ((line.StartsWith("maxParticles:", StringComparison.Ordinal) || line.StartsWith("m_MaxParticles:", StringComparison.Ordinal)) && TryInt(ValueAfterColon(line), out int maxParticles))
                    {
                        b.ParticleMaxKnown = true;
                        b.ParticleMax = maxParticles;
                    }
                    else if (particleModule != null && line.StartsWith("m_Enabled:", StringComparison.Ordinal) && ParseBool(ValueAfterColon(line)))
                    {
                        b.ParticleExpensiveModules++;
                        b.ExpensiveParticleModuleNames.Add(particleModule);
                    }
                }

                if (b.IsRenderer && line.StartsWith("m_UpdateWhenOffscreen:", StringComparison.Ordinal))
                    b.UpdateWhenOffscreen = ParseBool(ValueAfterColon(line));

                if (line.IndexOf("guid:", StringComparison.Ordinal) >= 0)
                {
                    string guid = ExtractGuid(line);
                    if (!string.IsNullOrEmpty(guid) && !guid.Equals("00000000000000000000000000000000", StringComparison.OrdinalIgnoreCase))
                    {
                        if (b.ExternalGuids.Count == 0 || !b.ExternalGuids.Contains(guid)) b.ExternalGuids.Add(guid);
                    }
                }
            }

            // A MonoBehaviour with an unknown script is exactly the situation where the current
            // Unity implementation can prove EventSystem handler behavior. Conservative fallback
            // is preferable to a serialized false negative.
            if (b.ClassId == 114 && string.IsNullOrEmpty(b.ScriptPath)) b.HasUnknownMonoBehaviour = true;
            if (b.ClassId == 114 && !IsKnownGraphicScript(b.ScriptPath) && !b.HasEventHandler) b.HasUnknownMonoBehaviour = true;
            if (b.ClassId == 114 && hasRaycastField && b.HasUnknownMonoBehaviour) anyUnknownPointerScript = true;
            b.UnknownPointerHandler = anyUnknownPointerScript;
        }

        static bool IsKnownGraphicScript(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath)) return false;
            string lower = scriptPath.ToLowerInvariant();
            return lower.Contains("image.cs")
                || lower.Contains("rawimage.cs")
                || lower.Contains("text.cs")
                || lower.Contains("textmeshpro")
                || lower.Contains("mask.cs")
                || lower.Contains("rectmask2d.cs")
                || lower.Contains("outline.cs");
        }

        static ParseResult BuildResult(
            List<Block> blocks,
            bool isScene,
            Func<string, string> resolveGuidToPath,
            Func<string, MeshStats> resolveMeshStats)
        {
            var gameObjects = new Dictionary<long, Block>();
            var transforms = new Dictionary<long, TransformInfo>();
            var componentsByGo = new Dictionary<long, List<Block>>();
            var componentById = new Dictionary<long, Block>();
            var prefabInstances = new List<Block>();

            for (int i = 0; i < blocks.Count; i++)
            {
                Block b = blocks[i];
                if (b.ClassId == 1)
                {
                    gameObjects[b.FileId] = b;
                }
                else if (b.ClassId == 4)
                {
                    transforms[b.FileId] = new TransformInfo
                    {
                        FileId = b.FileId,
                        GameObjectId = b.GameObjectId,
                        ParentId = b.HasFather ? b.FatherId : 0,
                        Name = gameObjects.TryGetValue(b.GameObjectId, out Block go) ? go.Name : string.Empty
                    };
                }
                else if (b.ClassId == 1001)
                {
                    prefabInstances.Add(b);
                }
                else
                {
                    componentById[b.FileId] = b;
                    if (b.GameObjectId == 0) continue;
                    if (!componentsByGo.TryGetValue(b.GameObjectId, out List<Block> list))
                        componentsByGo[b.GameObjectId] = list = new List<Block>(4);
                    list.Add(b);
                }
            }

            foreach (KeyValuePair<long, TransformInfo> pair in transforms)
            {
                if (string.IsNullOrEmpty(pair.Value.Name) && gameObjects.TryGetValue(pair.Value.GameObjectId, out Block go))
                    pair.Value.Name = go.Name;
            }

            var facts = new PrefabSceneAnalyzer.HierarchyFacts();
            string limitation = string.Empty;
            NativeCapability nativeCapabilities = NativeCapability.None;
            var pathByTransform = new Dictionary<long, string>();
            var depthByTransform = new Dictionary<long, int>();
            var lodByGo = new Dictionary<long, bool>();
            var bodyByGo = new Dictionary<long, bool>();
            var animatorByGo = new Dictionary<long, bool>();
            var pointerByGo = new Dictionary<long, bool>();
            var materialCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var meshGuidByGo = new Dictionary<long, string>();
            var unknownScriptByGo = new Dictionary<long, bool>();
            var stopNaNByGo = new Dictionary<long, bool>();
            var stopNaNKnownByGo = new Dictionary<long, bool>();
            var missingPrefabSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (KeyValuePair<long, List<Block>> pair in componentsByGo)
            {
                List<Block> list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    Block b = list[i];
                    if (b.IsMeshFilter && !string.IsNullOrEmpty(b.MeshGuid)) meshGuidByGo[pair.Key] = b.MeshGuid;
                    if (b.ClassId == 114 && b.HasUnknownMonoBehaviour) unknownScriptByGo[pair.Key] = true;
                    if (b.ClassId == 114 && b.ScriptPath.IndexOf("universaladditionalcameradata", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        stopNaNKnownByGo[pair.Key] = b.StopNaNKnown && b.StopNaNBaseKnown;
                        stopNaNByGo[pair.Key] = b.StopNaN;
                    }
                }
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                if (!blocks[i].IsLight) continue;
                if (!blocks[i].LightShadowsKnown || !blocks[i].LightBakeKnown)
                {
                    nativeCapabilities |= NativeCapability.LightSemantics;
                    limitation = "One or more light shadow/bake fields were not available in serialized data.";
                }
            }

            foreach (KeyValuePair<long, TransformInfo> pair in transforms)
            {
                TransformInfo t = pair.Value;
                string path = BuildPath(t.FileId, transforms, pathByTransform);
                int depth;
                if (t.ParentId != 0 && depthByTransform.TryGetValue(t.ParentId, out int parentDepth)) depth = parentDepth + 1;
                else depth = ComputeDepth(t.FileId, transforms);
                depthByTransform[t.FileId] = depth;
                if (depth > facts.maxDepth) facts.maxDepth = depth;
                facts.objectCount++;

                List<Block> components = componentsByGo.TryGetValue(t.GameObjectId, out List<Block> c) ? c : null;
                int componentCount = components != null ? components.Count : 0;
                bool hasLod = false;
                bool hasBody = false;
                bool hasAnimator = false;
                bool handlesPointer = false;
                bool hasUnknownScript = false;

                if (components != null)
                {
                    for (int i = 0; i < components.Count; i++)
                    {
                        Block b = components[i];
                        hasLod |= b.IsLodGroup;
                        hasBody |= b.IsRigidbody;
                        hasAnimator |= b.IsAnimator;
                        handlesPointer |= b.HasEventHandler;
                        hasUnknownScript |= b.HasUnknownMonoBehaviour;
                    }
                }
                if (t.ParentId != 0)
                {
                    if (lodByGo.TryGetValue(GetGameObjectId(t.ParentId, transforms), out bool parentLod)) hasLod |= parentLod;
                    if (bodyByGo.TryGetValue(GetGameObjectId(t.ParentId, transforms), out bool parentBody)) hasBody |= parentBody;
                    if (animatorByGo.TryGetValue(GetGameObjectId(t.ParentId, transforms), out bool parentAnimator)) hasAnimator |= parentAnimator;
                    if (pointerByGo.TryGetValue(GetGameObjectId(t.ParentId, transforms), out bool parentPointer)) handlesPointer |= parentPointer;
                    if (unknownScriptByGo.TryGetValue(GetGameObjectId(t.ParentId, transforms), out bool parentUnknown)) hasUnknownScript |= parentUnknown;
                }
                lodByGo[t.GameObjectId] = hasLod;
                bodyByGo[t.GameObjectId] = hasBody;
                animatorByGo[t.GameObjectId] = hasAnimator;
                pointerByGo[t.GameObjectId] = handlesPointer;

                if (componentCount == 0 || (componentCount == 1 && components[0].ClassId == 4)) facts.emptyObjects.Add(path);
                if (components != null)
                {
                    bool hasCameraComponent = false;
                    for (int si = 0; si < components.Count; si++) if (components[si].IsCamera) { hasCameraComponent = true; break; }
                    if (hasCameraComponent)
                    {
                        if (stopNaNKnownByGo.TryGetValue(t.GameObjectId, out bool knownStopNaN))
                        {
                            if (knownStopNaN && stopNaNByGo.TryGetValue(t.GameObjectId, out bool stopNaN) && stopNaN) facts.stopNaNCameras++;
                            else if (!knownStopNaN)
                            {
                                nativeCapabilities |= NativeCapability.StopNaNCameraState;
                                limitation = "URP camera extension state requires Unity component inspection.";
                            }
                        }
                    }
                }
                if (components == null) continue;

                for (int ci = 0; ci < components.Count; ci++)
                {
                    Block b = components[ci];
                    if (b.IsRenderer)
                    {
                        facts.rendererCount++;
                        for (int mi = 0; mi < b.MaterialGuids.Count; mi++)
                        {
                            string materialPath = resolveGuidToPath != null ? resolveGuidToPath(b.MaterialGuids[mi]) : string.Empty;
                            if (string.IsNullOrEmpty(materialPath)) { limitation = "A renderer material GUID could not be resolved from the project catalog."; continue; }
                            materialCounts.TryGetValue(materialPath, out int count);
                            materialCounts[materialPath] = count + 1;
                        }

                        if (b.IsSkinned && b.UpdateWhenOffscreen) facts.skinnedAlwaysUpdate.Add(path);

                        string rendererMeshGuid = !string.IsNullOrEmpty(b.MeshGuid) ? b.MeshGuid : (meshGuidByGo.TryGetValue(t.GameObjectId, out string filteredMeshGuid) ? filteredMeshGuid : string.Empty);
                        string meshPath = !string.IsNullOrEmpty(rendererMeshGuid) && resolveGuidToPath != null ? resolveGuidToPath(rendererMeshGuid) : string.Empty;
                        MeshStats mesh = !string.IsNullOrEmpty(meshPath) && resolveMeshStats != null ? resolveMeshStats(meshPath) : default(MeshStats);
                        if (!string.IsNullOrEmpty(rendererMeshGuid) && !mesh.Known)
                        {
                            nativeCapabilities |= NativeCapability.MeshStats;
                            limitation = "Imported mesh statistics were not available for one or more renderer references.";
                        }
                        if (mesh.Known)
                        {
                            if (mesh.VertexCount > 5000 && !hasLod) facts.denseNoLod.Add(path + " (" + mesh.VertexCount + " verts)");
                            int slots = b.MaterialGuids.Count;
                            if (mesh.SubMeshCount > 0 && slots != mesh.SubMeshCount)
                            {
                                facts.materialMismatches.Add(path + " (" + slots + " material" + (slots == 1 ? "" : "s") + ", " + mesh.SubMeshCount + " sub-mesh" + (mesh.SubMeshCount == 1 ? "" : "es") + " - " +
                                    (slots > mesh.SubMeshCount ? (slots - mesh.SubMeshCount) + " surplus, redrawing the last sub-mesh" : (mesh.SubMeshCount - slots) + " sub-mesh(es) not drawn") + ")");
                            }
                        }
                        if (isScene && b.ClassId == 23 && gameObjects.TryGetValue(t.GameObjectId, out Block go) && go.StaticFlags == 0 && !hasBody && !hasAnimator)
                            facts.unstaticGeometry.Add(path);
                    }

                    if (b.IsMeshCollider)
                    {
                        if (!b.MeshColliderConvexKnown)
                        {
                            nativeCapabilities |= NativeCapability.MeshColliderState;
                            limitation = "MeshCollider convex state was not present in serialized data.";
                        }
                        else if (!b.MeshColliderConvex)
                            facts.meshColliders.Add(path);
                    }

                    if (b.ClassId == 114 && string.IsNullOrEmpty(b.ScriptPath))
                        facts.missingScripts++;

                    if (b.IsParticle)
                    {
                        if (b.ParticleMaxKnown)
                        {
                            facts.particlePaths.Add(path);
                            facts.particleMax.Add(b.ParticleMax);
                        }
                        else
                        {
                            nativeCapabilities |= NativeCapability.ParticleState;
                            limitation = "Particle maxParticles was not available in serialized data.";
                        }
                        facts.particleExpensiveModuleCount += b.ParticleExpensiveModules;
                        for (int pi = 0; pi < b.ExpensiveParticleModuleNames.Count; pi++)
                            facts.expensiveParticleModules.Add(path + " (" + b.ExpensiveParticleModuleNames[pi] + ")");
                    }
                    if (b.IsAnimator && b.HasAnimatorCulling && b.AnimatorCulling == 0) facts.alwaysAnimate.Add(path);
                    if (b.IsRigidbody)
                    {
                        facts.rigidbodyPaths.Add(path);
                        facts.rigidbodySolverIterations.Add(b.HasSolverIterations ? b.SolverIterations : 0);
                        facts.rigidbodyCount++;
                        if (!b.HasSolverIterations || !b.HasCollisionDetection || !b.HasRigidbodyKinematic)
                        {
                            nativeCapabilities |= NativeCapability.RigidbodyState;
                            limitation = "Rigidbody solver/collision/kinematic fields were not fully represented in serialized data.";
                        }
                        if (b.HasCollisionDetection && b.CollisionDetection == 2 && b.RigidbodyKinematic) facts.continuousDynamicKinematicBodies.Add(path);
                        if (b.HasCollisionDetection && b.CollisionDetection != 0) facts.continuousBodies.Add(path + " (" + CollisionModeName(b.CollisionDetection) + ")");
                    }
                    if (b.IsJoint) { facts.jointCount++; facts.jointPaths.Add(path); }
                    if (b.IsCanvas) facts.canvasCount++;
                    if (b.IsGraphicRaycaster) facts.graphicRaycasterCount++;
                    if (b.IsLayout) facts.layoutComponentCount++;
                    if (b.IsGraphic && b.RaycastTarget)
                    {
                        if (handlesPointer || b.UnknownPointerHandler || hasUnknownScript)
                        {
                            nativeCapabilities |= NativeCapability.UiInputSemantics;
                            limitation = "UI event-handler semantics require Unity component type information.";
                        }
                        else facts.raycastTargets.Add(path);
                    }

                    for (int gi = 0; gi < b.ExternalGuids.Count; gi++)
                    {
                        string guid = b.ExternalGuids[gi];
                        if (string.Equals(guid, b.ScriptGuid, StringComparison.OrdinalIgnoreCase)) continue;
                        if (resolveGuidToPath != null && string.IsNullOrEmpty(resolveGuidToPath(guid)))
                        {
                            facts.missingRefs.Add(path + " (serialized reference " + guid + ")");
                            break;
                        }
                    }
                }
            }

            for (int i = 0; i < prefabInstances.Count; i++)
            {
                Block instance = prefabInstances[i];
                if (!instance.MissingPrefab) continue;
                string label = "Missing prefab " + instance.MissingPrefabGuid;
                if (missingPrefabSet.Add(label)) facts.missingPrefabs.Add(label);
                // The source object explicitly carries m_SourcePrefab; a missing GUID is enough to
                // report the broken prefab instance without opening the hierarchy.
            }

            foreach (KeyValuePair<string, int> pair in materialCounts)
            {
                facts.materialPaths.Add(pair.Key);
                facts.materialRendererCounts.Add(pair.Value);
            }

            int shadowLights = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                Block light = blocks[i];
                if (!light.IsLight || !light.LightEnabled || !light.LightShadowsKnown || !light.LightBakeKnown) continue;
                // UnityCsReference defines LightShadows.None=0 and LightmapBakeType.Baked=2; the
                // native rule counts enabled shadow lights that are not completely baked.
                if (light.LightShadows != 0 && light.LightBake != 2) shadowLights++;
            }
            facts.shadowLights = shadowLights;

            return new ParseResult
            {
                Facts = facts,
                NativeCapabilities = nativeCapabilities,
                Limitation = limitation,
                ObjectCount = facts.objectCount
            };
        }

        static long GetGameObjectId(long transformId, Dictionary<long, TransformInfo> transforms)
            => transforms.TryGetValue(transformId, out TransformInfo t) ? t.GameObjectId : 0L;

        static string BuildPath(long transformId, Dictionary<long, TransformInfo> transforms, Dictionary<long, string> cache)
        {
            if (cache.TryGetValue(transformId, out string known)) return known;
            if (!transforms.TryGetValue(transformId, out TransformInfo t)) return "<object>";
            string name = string.IsNullOrEmpty(t.Name) ? "<object>" : t.Name;
            if (t.ParentId == 0 || !transforms.ContainsKey(t.ParentId))
            {
                cache[transformId] = name;
                return name;
            }
            string path = BuildPath(t.ParentId, transforms, cache) + "/" + name;
            cache[transformId] = path;
            return path;
        }

        static int ComputeDepth(long id, Dictionary<long, TransformInfo> transforms)
        {
            int depth = 0;
            int guard = 0;
            long current = id;
            while (transforms.TryGetValue(current, out TransformInfo t) && t.ParentId != 0 && guard++ < 100000)
            {
                depth++;
                current = t.ParentId;
            }
            return depth;
        }

        static string CollisionModeName(int value)
        {
            switch (value)
            {
                case 1: return "Continuous";
                case 2: return "ContinuousDynamic";
                case 3: return "ContinuousSpeculative";
                default: return "Discrete";
            }
        }

        static bool TryRefFileId(string line, out long value)
        {
            value = 0;
            int start = line.IndexOf("fileID:", StringComparison.Ordinal);
            if (start < 0) return false;
            start += 7;
            int end = line.IndexOf(',', start);
            if (end < 0) end = line.IndexOf('}', start);
            if (end < 0) end = line.Length;
            return long.TryParse(line.Substring(start, end - start).Trim(), out value);
        }

        static string ExtractGuid(string line)
        {
            int start = line.IndexOf("guid:", StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += 5;
            int end = line.IndexOf(',', start);
            if (end < 0) end = line.IndexOf('}', start);
            if (end < 0) end = line.Length;
            return line.Substring(start, end - start).Trim();
        }

        static string ValueAfterColon(string line)
        {
            int colon = line.IndexOf(':');
            return colon < 0 ? string.Empty : line.Substring(colon + 1).Trim();
        }

        static string Between(string value, string start, string end)
        {
            int a = value.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return string.Empty;
            a += start.Length;
            int b = value.IndexOf(end, a, StringComparison.Ordinal);
            return b < 0 ? value.Substring(a) : value.Substring(a, b - a);
        }

        static bool TryInt(string value, out int result) => int.TryParse(value.Trim(), out result);

        static bool TryBool(string line, out bool result)
        {
            result = false;
            string value = ValueAfterColon(line);
            if (string.Equals(value, "1", StringComparison.Ordinal) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
            if (string.Equals(value, "0", StringComparison.Ordinal) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool ParseBool(string value)
            => string.Equals(value.Trim(), "1", StringComparison.Ordinal) || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') return value.Substring(1, value.Length - 2);
            return value;
        }
    }
}

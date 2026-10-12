using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Source-level rendering evidence for Unity's modern Render Graph migration path. This does not
    /// claim that a legacy pass is slow; it flags code patterns that deserve inspection and ties them
    /// to the current Unity Render Graph model. All findings remain Review/diagnostic because runtime
    /// rendering cost must be established with the Frame Debugger/Profiler rather than source text alone.
    /// </summary>
    public sealed class RenderingEvidenceAnalyzer : IProjectAnalyzer
    {
        float m_Progress;
        public string DisplayName => "Rendering evidence";
        public float Progress => m_Progress;
        public bool Enabled(OptimizerSettings settings) => IsScriptableRenderPipelineActive();

        static RenderingEvidenceAnalyzer()
        {
            Rules.Register(id: "REN001", title: "Renderer feature uses the legacy AddRenderPasses path", category: AssetCategory.Rendering,
                why: "Unity 6's URP Render Graph path uses RecordRenderGraph. Renderer features that only implement the legacy AddRenderPasses path should be reviewed for migration or intentional compatibility-mode use.",
                fix: "Open the renderer feature and determine whether it should implement RecordRenderGraph. Keep compatibility mode only when the pass genuinely depends on APIs that require it.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic, risk: FixRisk.Unspecified);
            Rules.Register(id: "REN002", title: "Renderer feature performs an explicit blit", category: AssetCategory.Rendering,
                why: "Full-screen blits can introduce additional render work and resource transitions. Unity's Render Graph guidance recommends expressing pass inputs/outputs so the graph can merge or avoid unnecessary copies where possible.",
                fix: "Inspect the pass in the Frame Debugger and Render Graph Viewer. Prefer direct graph resource usage and ConfigureInput declarations when the effect does not require an explicit intermediate copy.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic, risk: FixRisk.Unspecified);
            Rules.Register(id: "REN003", title: "Renderer feature configures an intermediate render target on the legacy path", category: AssetCategory.Rendering,
                why: "ConfigureTarget calls in a legacy ScriptableRenderPass can imply extra intermediate resources. Whether that is actually wasteful depends on the pass and renderer configuration.",
                fix: "Inspect the Render Graph resource lifetime and target declarations. Remove intermediate targets only when the graph can express the same effect without them.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic, risk: FixRisk.Unspecified);
            Rules.Register(id: "REN004", title: "Renderer feature uses a CommandBuffer blit/copy", category: AssetCategory.Rendering,
                why: "CommandBuffer.Blit/CopyTexture operations can create explicit copies outside an optimized Render Graph resource flow. The actual cost is workload and renderer dependent.",
                fix: "Inspect the copy in Frame Debugger/Render Graph Viewer and replace it with direct resource access or graph-native dependencies when that preserves behavior.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic, risk: FixRisk.Unspecified);
            Rules.Register(id: "REN005", title: "Renderer code uses MaterialPropertyBlock", category: AssetCategory.Rendering,
                why: "Unity 6 GPU Resident Drawer compatibility requires that the GameObject not use MaterialPropertyBlock, and Unity's draw-call guidance recommends avoiding it in URP/HDRP when GPU-driven rendering is a goal.",
                fix: "Review whether the per-renderer property can be expressed through a compatible material/instance-data path before changing it. Keep MaterialPropertyBlock when it is semantically required.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic, risk: FixRisk.Unspecified);
            Rules.Register(id: "REN006", title: "Per-instance rendering callback can block GPU-driven rendering", category: AssetCategory.Rendering,
                why: "Unity documents per-instance rendering callbacks such as OnRenderObject as incompatible with GPU Resident Drawer participation. This is a compatibility signal, not proof that the project should remove the callback.",
                fix: "Review whether the callback is required. Where GPU-driven rendering is a target, move the rendering work to a compatible system and verify visual and performance behavior.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic, risk: FixRisk.Unspecified);
        }

        public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
        {
            m_Progress = 0f;
            string[] guids;
            try { guids = AssetDatabase.FindAssets("t:Script"); }
            catch { yield break; }
            if (guids == null || guids.Length == 0) { m_Progress = 1f; yield break; }

            var candidates = new List<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                candidates.Add(path);
            }

            int total = candidates.Count;
            for (int i = 0; i < total; i++)
            {
                string path = candidates[i];
                m_Progress = total == 0 ? 1f : i / (float)total;
                string text;
                try { text = File.ReadAllText(Path.GetFullPath(path)); }
                catch { continue; }
                if (text.IndexOf("ScriptableRendererFeature", StringComparison.Ordinal) < 0
                    && text.IndexOf("ScriptableRenderPass", StringComparison.Ordinal) < 0)
                    continue;

                bool legacy = text.IndexOf("AddRenderPasses", StringComparison.Ordinal) >= 0
                    && text.IndexOf("RecordRenderGraph", StringComparison.Ordinal) < 0;
                bool blit = text.IndexOf("CommandBuffer.Blit", StringComparison.Ordinal) >= 0
                    || text.IndexOf(".Blit(", StringComparison.Ordinal) >= 0;
                bool configureTarget = text.IndexOf("ConfigureTarget(", StringComparison.Ordinal) >= 0;
                bool commandCopy = text.IndexOf("CommandBuffer.CopyTexture", StringComparison.Ordinal) >= 0
                    || text.IndexOf("cmd.CopyTexture(", StringComparison.Ordinal) >= 0;
                bool materialPropertyBlock = text.IndexOf("MaterialPropertyBlock", StringComparison.Ordinal) >= 0;
                bool renderingCallback = text.IndexOf("OnRenderObject", StringComparison.Ordinal) >= 0
                    || text.IndexOf("OnWillRenderObject", StringComparison.Ordinal) >= 0;

                if (!(legacy || blit || configureTarget || commandCopy || materialPropertyBlock || renderingCallback)) continue;

                var entry = new AssetEntry
                {
                    Guid = ctx.GetGuid(path) ?? string.Empty,
                    Path = path,
                    Name = Path.GetFileName(path),
                    Directory = AssetPathUtility.Directoryish(path),
                    Extension = ".cs",
                    Category = AssetCategory.Rendering,
                    TypeLabel = "Renderer Feature / Pass",
                    FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(path) : FileUtility.FileSizeOf(path),
                    RuntimeSize = -1
                };

                if (legacy) entry.AddIssue("REN001", "Legacy AddRenderPasses path", "Review Render Graph migration or intentional compatibility mode");
                if (blit) entry.AddIssue("REN002", "Explicit blit operation", "Inspect Render Graph resource flow and Frame Debugger passes");
                if (configureTarget && legacy) entry.AddIssue("REN003", "ConfigureTarget on legacy pass", "Inspect intermediate target lifetime");
                if (commandCopy) entry.AddIssue("REN004", "CommandBuffer copy operation", "Inspect copy cost and graph-native alternatives");
                if (materialPropertyBlock) entry.AddIssue("REN005", "MaterialPropertyBlock usage", "Review GPU-driven rendering compatibility");
                if (renderingCallback) entry.AddIssue("REN006", "Per-instance rendering callback", "Review GPU-driven rendering compatibility");
                entry.Finalise();
                yield return entry;
            }
            m_Progress = 1f;
        }

        static bool IsScriptableRenderPipelineActive()
        {
            try { return UnityEngine.Rendering.GraphicsSettings.isScriptableRenderPipelineEnabled; }
            catch { return false; }
        }
    }
}

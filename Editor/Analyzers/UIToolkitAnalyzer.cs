using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
    [ProjectOptimizerAnalyzer]
    public sealed class UIToolkitAnalyzer : IBackgroundAssetAnalyzer
    {
        const int k_FactsSchema = 2;
        const int k_UxmlElementWarning = 500;
        const int k_UxmlDepthWarning = 20;
        const int k_UssSelectorWarning = 400;
        const long k_LargeFileBytes = 512 * 1024;

        public AssetCategory Category => AssetCategory.UIToolkit;
        public AssetKindMask SupportedKinds => AssetKindMask.Other;
        public void BeginScan(ScanContext ctx) { }
        public bool Handles(string path) => path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase);

        public sealed class UIToolkitFacts
        {
            public int factsSchema = k_FactsSchema;
            public int lines;
            public int elements;
            public int imports;
            public int selectors;
            public int maxDepth;
            public bool importedWithErrors;
        }

        static UIToolkitAnalyzer()
        {
            Rules.Register(id:"UITK001", title:"Large UI Toolkit source asset", category:AssetCategory.UIToolkit, why:"Large UXML/USS files increase import and maintenance complexity and can hide duplicated structure or styling.", fix:"Split the asset by responsibility and remove genuinely unused templates/selectors.", severity:IssueSeverity.Info, fixKind:FixKind.None, confidence:OptimizationConfidence.HighConfidence, risk:FixRisk.Unspecified, backupRequired:false);
            Rules.Register(id:"UITK002", title:"Deep UXML hierarchy", category:AssetCategory.UIToolkit, why:"Very deep visual-tree hierarchies are harder to reason about and can increase layout/style traversal work.", fix:"Flatten unnecessary wrapper elements and keep reusable templates focused.", severity:IssueSeverity.Info, fixKind:FixKind.None, confidence:OptimizationConfidence.Heuristic, risk:FixRisk.Unspecified, backupRequired:false);
            Rules.Register(id:"UITK003", title:"Large USS selector surface", category:AssetCategory.UIToolkit, why:"A large selector surface increases style matching work and makes duplicated or conflicting rules more likely.", fix:"Remove duplicated selectors and keep broad theme rules separate from component-local styling.", severity:IssueSeverity.Info, fixKind:FixKind.None, confidence:OptimizationConfidence.Heuristic, risk:FixRisk.Unspecified, backupRequired:false);
            Rules.Register(id:"UITK004", title:"USS imported with errors", category:AssetCategory.UIToolkit, why:"Unity did not successfully import this stylesheet, so editor UI styling can be missing or incomplete.", fix:"Open the USS, fix the unsupported or malformed selectors/imports, and reimport it.", severity:IssueSeverity.Warning, fixKind:FixKind.None, confidence:OptimizationConfidence.HighConfidence, risk:FixRisk.Unspecified, backupRequired:false);
        }

        public AssetEntry Analyze(string path, ScanContext ctx)
        {
            if (AssetPathUtility.IsInEditorFolder(path)) return null;
            string hash = ctx.GetDependencyHash(path);
            UIToolkitFacts facts = ctx.Cache != null
                ? ctx.GetCached(path, () => Measure(path, null, null))
                : Measure(path, null, null);
            if (facts == null) return null;
            if (facts.factsSchema != k_FactsSchema)
            {
                facts = Measure(path, null, null);
                if (facts == null) return null;
                ctx.Cache?.Set(ScanCache.ScopedKey(path, typeof(UIToolkitFacts).FullName), facts, hash);
            }

            bool errors = facts.importedWithErrors;
            if (path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
            {
                try { var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(path); errors = style == null; }
                catch { }
            }
            facts.importedWithErrors = errors;
            return BuildEntry(path, ctx.GetGuid(path), ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(path) : FileUtility.FileSizeOf(path), facts);
        }

        public void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext)
        {
            if (mainContext.Cache != null)
            {
                string hash = backgroundContext.GetDependencyHash(assetPath);
                if (!string.IsNullOrEmpty(hash) && mainContext.Cache.TryGet(ScanCache.ScopedKey(assetPath, typeof(UIToolkitFacts).FullName), hash, out UIToolkitFacts cached))
                {
                    backgroundContext.SetPreflightFlag(assetPath, cached.importedWithErrors);
                    backgroundContext.Seed(assetPath, cached);
                    return;
                }
            }

            bool importedWithErrors = false;
            if (assetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(assetPath);
                    importedWithErrors = style == null;
                }
                catch { }
            }
            backgroundContext.SetPreflightFlag(assetPath, importedWithErrors);
        }

        public AssetEntry AnalyzeBackground(string path, BackgroundAssetContext context)
        {
            if (AssetPathUtility.IsInEditorFolder(path)) return null;
            var facts = context.GetOrMeasure(path, () => Measure(path, context.GetPreflightFlag(path), context.ProjectRoot));
            if (facts == null) return null;
            if (facts.factsSchema != k_FactsSchema) facts = Measure(path, context.GetPreflightFlag(path), context.ProjectRoot);
            return facts == null ? null : BuildEntry(path, context.GetGuid(path), context.GetFileSize(path), facts);
        }

        static AssetEntry BuildEntry(string path, string guid, long fileSize, UIToolkitFacts facts)
        {
            var entry = new AssetEntry
            {
                Guid = guid ?? string.Empty,
                Path = path,
                Name = Path.GetFileName(path),
                Directory = AssetPathUtility.Directoryish(path),
                Extension = Path.GetExtension(path),
                Category = AssetCategory.UIToolkit,
                TypeLabel = path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) ? "UI Toolkit UXML" : "UI Toolkit USS",
                FileSize = fileSize,
                RuntimeSize = -1
            };
            entry.Set("elements", (long)facts.elements);
            entry.Set("maxDepth", (long)facts.maxDepth);
            entry.Set("templates", (long)facts.imports);
            entry.Set("selectors", (long)facts.selectors);
            entry.Set("imports", (long)facts.imports);
            entry.Set("lines", (long)facts.lines);
            entry.Set("bytes", (long)(fileSize > 0 ? fileSize : 0));
            bool uxml = path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase);
            if (uxml)
            {
                using(RuleTiming.Begin("UITK001",path)) if(facts.lines > 0 && (fileSize > k_LargeFileBytes || facts.elements > k_UxmlElementWarning)) entry.AddIssue("UITK001",facts.elements.ToString("N0")+" elements / "+DisplayUtility.FormatBytes(fileSize),"Split or simplify only when the structure is genuinely redundant");
                using(RuleTiming.Begin("UITK002",path)) if(facts.maxDepth>k_UxmlDepthWarning) entry.AddIssue("UITK002",facts.maxDepth+" levels deep","Flatten unnecessary wrappers");
            }
            else
            {
                using(RuleTiming.Begin("UITK001",path)) if(fileSize>k_LargeFileBytes) entry.AddIssue("UITK001",facts.lines.ToString("N0")+" lines / "+DisplayUtility.FormatBytes(fileSize),"Split or simplify only when styling is genuinely duplicated");
                using(RuleTiming.Begin("UITK003",path)) if(facts.selectors>k_UssSelectorWarning) entry.AddIssue("UITK003",facts.selectors.ToString("N0")+" selector blocks","Review duplicated/broad selectors");
                using(RuleTiming.Begin("UITK004",path)) if(facts.importedWithErrors) entry.AddIssue("UITK004","Unity could not load the stylesheet","Fix the USS import errors and reimport");
            }
            entry.Finalise();
            return entry;
        }

        static UIToolkitFacts Measure(string path, bool? knownImportError, string projectRoot)
        {
            string text;
            try
            {
                string fullPath = string.IsNullOrEmpty(projectRoot) ? path : Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
                text = File.ReadAllText(fullPath);
            }
            catch { return null; }

            var facts = new UIToolkitFacts();
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') facts.lines++;
            if (text.Length > 0) facts.lines++;

            bool uxml = path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase);
            if (uxml)
            {
                int depth = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '<') continue;
                    int end = text.IndexOf('>', i + 1);
                    if (end < 0) break;
                    string tag = text.Substring(i + 1, end - i - 1).Trim();
                    if (tag.StartsWith("!--", StringComparison.Ordinal)) { i = end; continue; }
                    if (tag.StartsWith("?", StringComparison.Ordinal) || tag.StartsWith("!", StringComparison.Ordinal)) { i = end; continue; }
                    if (tag.StartsWith("/", StringComparison.Ordinal)) { if (depth > 0) depth--; i = end; continue; }
                    facts.elements++;
                    if (!tag.EndsWith("/", StringComparison.Ordinal)) { depth++; if (depth > facts.maxDepth) facts.maxDepth = depth; }
                    if (tag.IndexOf("template", StringComparison.OrdinalIgnoreCase) >= 0 || tag.IndexOf("Instance", StringComparison.Ordinal) >= 0) facts.imports++;
                    i = end;
                }
            }
            else
            {
                for (int i = 0; i < text.Length - 1; i++) if (text[i] == '@' && text[i + 1] == '"') facts.imports++;
                for (int i = 0; i < text.Length; i++) if (text[i] == '{') facts.selectors++;
                facts.importedWithErrors = knownImportError ?? false;
            }
            return facts;
        }
    }
}

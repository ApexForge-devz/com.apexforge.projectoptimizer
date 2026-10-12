using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;

namespace ApexForge.ProjectOptimizer
{
    public enum EvidenceState
    {
        Fresh = 0,
        Stale = 1,
        Unconfirmed = 2,
        Mismatched = 3,
        InsufficientSamples = 4
    }

    public enum EvidenceRequirement
    {
        None = 0,
        ProjectProofComplete = 1,
        CrossAssetFresh = 2,
        RuntimeBuildIdentity = 3,
        AddressablesAnalyzeFresh = 4,
        BuildLayoutMetrics = 5
    }

    /// <summary>Versioned execution semantics and deterministic evidence identities for persisted findings.</summary>
    public static class EvidenceContract
    {
        public const int AnalyzerContractVersion = 4;
        public const int FixContractVersion = 4;
        public const int EvidenceSchemaVersion = 3;

        static string s_AnalyzerSignature;
        static string s_FixSignature;

        public static string AnalyzerSignature => s_AnalyzerSignature ?? (s_AnalyzerSignature = ComputeSourceTreeSignature("analyzer-v" + AnalyzerContractVersion, "Editor/Analyzers", "Editor/Core/Analysis", "Editor/Core/Scanning", "Editor/Core/Scanning/AssetDatabaseThreadSafe"));
        public static string FixSignature => s_FixSignature ?? (s_FixSignature = ComputeSourceTreeSignature("fix-v" + FixContractVersion, "Editor/Core/Domain/Model.cs", "Editor/Core/Fixes", "Editor/Core/Persistence"));

        public static string ComputeRuleCatalogSignature(IReadOnlyList<RuleDef> rules, IDictionary<string, string> aliases = null)
        {
            if (rules == null) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder(4096);
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    if (rule == null) continue;
                    text.Append(rule.Id).Append('|')
                        .Append(rule.Title).Append('|')
                        .Append(rule.Why).Append('|')
                        .Append(rule.Fix).Append('|')
                        .Append((int)rule.Category).Append('|')
                        .Append((int)rule.DefaultSeverity).Append('|')
                        .Append((int)rule.FixKind).Append('|')
                        .Append((int)rule.Confidence).Append('|')
                        .Append((int)rule.RecommendationConfidence).Append('|')
                        .Append((int)rule.RequiredEvidence).Append('|')
                        .Append((int)rule.DefaultFixRisk).Append('|')
                        .Append(rule.DefaultBackupRequired ? '1' : '0').Append('|')
                        .Append(rule.SemanticVersion ?? string.Empty).Append('\n');
                }

                // Alias changes alter historical-ID resolution and therefore invalidate snapshots too.
                if (aliases != null && aliases.Count > 0)
                {
                    var aliasIds = new List<string>(aliases.Keys);
                    aliasIds.Sort(StringComparer.Ordinal);
                    for (int i = 0; i < aliasIds.Count; i++)
                    {
                        string aliasId = aliasIds[i];
                        text.Append("alias|").Append(aliasId).Append('|')
                            .Append(aliases[aliasId] ?? string.Empty).Append('\n');
                    }
                }

                byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        public static string ComputeProjectAssetScopeFingerprint()
        {
            try
            {
                var paths = GetProjectContentScopePaths();
                using (var sha = SHA256.Create())
                {
                    var text = new StringBuilder(paths.Count * 32);
                    for (int i = 0; i < paths.Count; i++) text.Append(paths[i]).Append('\n');
                    return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
                }
            }
            catch { return string.Empty; }
        }

        public static string ComputeProjectAssetIdentity()
        {
            try
            {
                return ComputeContentIdentity(GetProjectContentScopePaths());
            }
            catch { return string.Empty; }
        }

        static List<string> GetProjectContentScopePaths()
        {
            var paths = new List<string>();
            var assetPaths = AssetDatabase.GetAllAssetPaths();
            if (assetPaths != null)
            {
                for (int i = 0; i < assetPaths.Length; i++)
                {
                    string path = assetPaths[i];
                    if (!string.IsNullOrEmpty(path)) paths.Add(path.Replace('\\', '/'));
                }
            }

            // Cross-asset conclusions can change without an imported asset changing. Keep the
            // project/package manifests and graphics/build settings in the project identity too.
            AddExistingProjectPath(paths, "Packages/manifest.json");
            AddExistingProjectPath(paths, "Packages/packages-lock.json");
            AddExistingProjectPath(paths, "ProjectSettings/ProjectVersion.txt");
            AddExistingProjectPath(paths, "ProjectSettings/EditorBuildSettings.asset");
            AddExistingProjectPath(paths, "ProjectSettings/GraphicsSettings.asset");
            AddExistingProjectPath(paths, "ProjectSettings/QualitySettings.asset");

            paths.Sort(StringComparer.Ordinal);
            for (int i = paths.Count - 1; i > 0; i--)
                if (string.Equals(paths[i], paths[i - 1], StringComparison.Ordinal)) paths.RemoveAt(i);
            return paths;
        }

        static void AddExistingProjectPath(List<string> paths, string relative)
        {
            if (paths == null || string.IsNullOrEmpty(relative)) return;
            string absolute = Path.Combine(Directory.GetCurrentDirectory(), relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute)) paths.Add(relative.Replace('\\', '/'));
        }

        public static string ComputeAssetEvidenceSignature(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            try
            {
                string relative = assetPath.Replace('\\', '/');
                string dependencyHash = string.Empty;
                try
                {
                    MethodInfo method = typeof(AssetDatabase).GetMethod("GetAssetDependencyHash", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (method != null) dependencyHash = Convert.ToString(method.Invoke(null, new object[] { relative }), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch { }

                string root = Directory.GetCurrentDirectory();
                string absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                string meta = absolute + ".meta";
                if (!File.Exists(absolute) && !File.Exists(meta)) return string.Empty;
                using (var sha = SHA256.Create())
                using (var stream = new MemoryStream())
                {
                    byte[] header = Encoding.UTF8.GetBytes(relative + "|" + (dependencyHash ?? string.Empty) + "\n");
                    stream.Write(header, 0, header.Length);
                    AppendIdentity(stream, relative, absolute);
                    AppendIdentity(stream, relative + ".meta", meta);
                    return ToHex(sha.ComputeHash(stream.ToArray()));
                }
            }
            catch { return string.Empty; }
        }

        public static string ComputeProofEvidenceSignature(ProjectProofIndex proof)
        {
            if (proof == null || !proof.CoverageComplete || string.IsNullOrEmpty(proof.CoverageSignature)) return string.Empty;
            return proof.CoverageSignature;
        }

        public static string ComputeCrossAssetEvidenceSignature(ReferenceIndex references)
        {
            if (references == null || !references.IsCurrentProjectWide || references.IndexedJournalGeneration < 0 || string.IsNullOrEmpty(references.ScopeFingerprint)) return string.Empty;
            return references.IndexedJournalGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + references.ScopeFingerprint + "|" + ComputeProjectAssetIdentity();
        }

        public static bool IsCrossAssetEvidenceCurrent(string evidenceSignature)
        {
            if (string.IsNullOrEmpty(evidenceSignature)) return false;
            string[] parts = evidenceSignature.Split('|');
            if (parts.Length != 3) return false;
            if (!long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var journal)) return false;
            if (journal != AssetDatabaseThreadSafe.AssetDatabaseThreadSafeUtility.JournalGeneration) return false;
            if (!string.Equals(parts[1], ComputeProjectAssetScopeFingerprint(), StringComparison.Ordinal)) return false;
            return string.Equals(parts[2], ComputeProjectAssetIdentity(), StringComparison.Ordinal);
        }

        public static bool IsAssetEvidenceCurrent(string assetPath, string evidenceSignature)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(evidenceSignature)) return false;
            return string.Equals(evidenceSignature, ComputeAssetEvidenceSignature(assetPath), StringComparison.Ordinal);
        }

        public static bool IsProofEvidenceCurrent(string evidenceSignature)
        {
            if (string.IsNullOrEmpty(evidenceSignature)) return false;
            string current = ComputeProofScopeIdentity();
            return !string.IsNullOrEmpty(current) && string.Equals(evidenceSignature, current, StringComparison.Ordinal);
        }

        public static string ComputeProofScopeIdentity()
        {
            try
            {
                string root = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/');
                var files = new List<string>();
                string[] roots =
                {
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Packages"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Library", "PackageCache")
                };
                for (int r = 0; r < roots.Length; r++)
                {
                    if (!Directory.Exists(roots[r])) continue;
                    foreach (string file in Directory.EnumerateFiles(roots[r], "*.*", SearchOption.AllDirectories))
                    {
                        string normalized = file.Replace('\\', '/');
                        if (normalized.IndexOf("/" + ProjectOptimizerConstants.PackageId + "/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        string ext = Path.GetExtension(normalized);
                        if (!IsProofExtension(ext)) continue;
                        files.Add(normalized);
                    }
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                using (var finalSha = SHA256.Create())
                using (var stream = new MemoryStream())
                {
                    for (int i = 0; i < files.Count; i++)
                    {
                        string logical = files[i].StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                            ? files[i].Substring(root.Length + 1) : files[i];
                        byte[] prefix = Encoding.UTF8.GetBytes(logical + "|");
                        stream.Write(prefix, 0, prefix.Length);
                        try
                        {
                            using (var fileStream = File.OpenRead(files[i]))
                            using (var fileSha = SHA256.Create())
                            {
                                byte[] contentHash = fileSha.ComputeHash(fileStream);
                                byte[] hashText = Encoding.UTF8.GetBytes(ToHex(contentHash) + "\n");
                                stream.Write(hashText, 0, hashText.Length);
                            }
                        }
                        catch
                        {
                            byte[] unavailable = Encoding.UTF8.GetBytes("unavailable\n");
                            stream.Write(unavailable, 0, unavailable.Length);
                        }
                    }
                    return ToHex(finalSha.ComputeHash(stream.ToArray()));
                }
            }
            catch { return string.Empty; }
        }

        static bool IsProofExtension(string extension)
        {
            return string.Equals(extension, ".mat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".shader", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".shadergraph", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".hlsl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".compute", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".inputactions", StringComparison.OrdinalIgnoreCase);
        }

        static string ComputeSourceTreeSignature(string prefix, params string[] relativePaths)
        {
            try
            {
                string packageRoot = ResolvePackageRoot();
                if (string.IsNullOrEmpty(packageRoot) || !Directory.Exists(packageRoot)) return prefix + ":unavailable";
                var files = new List<string>();
                for (int i = 0; i < relativePaths.Length; i++)
                {
                    string candidate = Path.Combine(packageRoot, relativePaths[i].Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate)) files.Add(candidate);
                    else if (Directory.Exists(candidate)) files.AddRange(Directory.GetFiles(candidate, "*.cs", SearchOption.AllDirectories));
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                using (var sha = SHA256.Create())
                using (var stream = new MemoryStream())
                {
                    byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix + "\n");
                    stream.Write(prefixBytes, 0, prefixBytes.Length);
                    for (int i = 0; i < files.Count; i++)
                    {
                        string logical = files[i].Substring(packageRoot.Length).Replace('\\', '/');
                        byte[] header = Encoding.UTF8.GetBytes(logical + "\n");
                        stream.Write(header, 0, header.Length);
                        byte[] bytes = File.ReadAllBytes(files[i]);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.WriteByte((byte)'\n');
                    }
                    string digest = ToHex(sha.ComputeHash(stream.ToArray()));
                    return prefix + ":" + digest;
                }
            }
            catch { return prefix + ":unavailable"; }
        }

        internal static string ResolvePackageRootForAudit() => ResolvePackageRoot();

        static string ResolvePackageRoot()
        {
            try
            {
                UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForPackageName(ProjectOptimizerConstants.PackageId);
                string value = package?.resolvedPath;
                if (!string.IsNullOrEmpty(value) && Directory.Exists(value)) return value;
            }
            catch { }
            return ProjectOptimizerConstants.PackageRoot;
        }

        public static string ComputeSourceCommitIdentity()
        {
            try
            {
                string root = Directory.GetCurrentDirectory();
                string headPath = Path.Combine(root, ".git", "HEAD");
                if (!File.Exists(headPath)) return string.Empty;
                string head = File.ReadAllText(headPath).Trim();
                if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                {
                    string reference = head.Substring(5).Trim();
                    string refPath = Path.Combine(root, ".git", reference.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(refPath)) return File.ReadAllText(refPath).Trim();
                    string packedRefs = Path.Combine(root, ".git", "packed-refs");
                    if (File.Exists(packedRefs))
                    {
                        foreach (string line in File.ReadAllLines(packedRefs))
                        {
                            if (line.StartsWith("#", StringComparison.Ordinal) || line.Length < 42) continue;
                            int space = line.IndexOf(' ');
                            if (space <= 0 || !string.Equals(line.Substring(space + 1).Trim(), reference, StringComparison.Ordinal)) continue;
                            return line.Substring(0, space).Trim();
                        }
                    }
                    return string.Empty;
                }
                return head;
            }
            catch { return string.Empty; }
        }

        public static string ComputeContentIdentity(IReadOnlyList<string> assetPaths)
        {
            if (assetPaths == null) return string.Empty;
            try
            {
                string root = Directory.GetCurrentDirectory();
                var sorted = new List<string>(assetPaths.Count);
                for (int i = 0; i < assetPaths.Count; i++)
                {
                    string path = assetPaths[i];
                    if (string.IsNullOrEmpty(path)) continue;
                    sorted.Add(path.Replace('\\', '/'));
                }
                sorted.Sort(StringComparer.Ordinal);
                using (var sha = SHA256.Create())
                using (var stream = new MemoryStream())
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        string assetPath = sorted[i];
                        string absolute = Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar));
                        AppendIdentity(stream, assetPath, absolute);
                        AppendIdentity(stream, assetPath + ".meta", absolute + ".meta");
                    }
                    return ToHex(sha.ComputeHash(stream.ToArray()));
                }
            }
            catch { return string.Empty; }
        }

        static void AppendIdentity(Stream stream, string logicalPath, string path)
        {
            byte[] logical = Encoding.UTF8.GetBytes(logicalPath + "|");
            stream.Write(logical, 0, logical.Length);
            try
            {
                if (!File.Exists(path))
                {
                    byte[] missing = Encoding.UTF8.GetBytes("missing\n");
                    stream.Write(missing, 0, missing.Length);
                    return;
                }

                string dependencyHash = string.Empty;
                if (!logicalPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    try { dependencyHash = AssetDatabase.GetAssetDependencyHash(logicalPath).ToString(); } catch { }
                }

                var info = new FileInfo(path);
                byte[] data = Encoding.UTF8.GetBytes(
                    (dependencyHash ?? string.Empty) + "|"
                    + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                    + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                stream.Write(data, 0, data.Length);
            }
            catch
            {
                byte[] error = Encoding.UTF8.GetBytes("unavailable\n");
                stream.Write(error, 0, error.Length);
            }
        }

        static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }
}

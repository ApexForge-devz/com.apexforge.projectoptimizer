using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Android shipping diagnostics for native-library compatibility and 16 KB page-size readiness.</summary>
    public sealed class AndroidCompatibilityAnalyzer : IProjectAnalyzer
    {
        const long PageAlignment = 16384L;
        public string DisplayName => "Android compatibility";

        static AndroidCompatibilityAnalyzer()
        {
            Rules.Register(id: "AND001", title: "Native Android library is not proven 16 KB page-size compatible", category: AssetCategory.ProjectSetting,
                why: "Android 16 KB page-size support depends on native ELF PT_LOAD alignment and the native toolchain/plugin contents shipped in the application.",
                fix: "Inspect the affected native library/plugin and rebuild or replace it with a 16 KB-compatible artifact. Project Optimizer does not mutate binary native libraries automatically.",
                severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.Proven, risk: FixRisk.High, backupRequired: true);
            Rules.Register(id: "AND002", title: "Android NDK requires 16 KB native-library compatibility review", category: AssetCategory.ProjectSetting,
                why: "The Android toolchain and linker settings participate in the final ELF PT_LOAD alignment. A detected NDK version is evidence about the toolchain, not proof that every shipped native plugin is compatible.",
                fix: "Confirm the native toolchain/plugin set and final packaged ELF alignment for the target Android build.",
                severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence, risk: FixRisk.Unspecified, backupRequired: false);
            Rules.Register(id: "AND003", title: "Custom Android Gradle Plugin is below the 16 KB compatibility baseline", category: AssetCategory.ProjectSetting,
                why: "Android's current 16 KB guidance recommends Android Gradle Plugin 8.5.1 or newer for default uncompressed shared-library packaging alignment.",
                fix: "Review the custom Android Gradle template and upgrade the configured Android Gradle Plugin when compatible with the Unity release and project build pipeline.",
                severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence, risk: FixRisk.Unspecified, backupRequired: false);
        }

        public bool Enabled(OptimizerSettings settings) => true;
        public void BeginScan(ScanContext ctx) { }
        public IEnumerable<AssetEntry> Analyze(ScanContext ctx) => AnalyzeProject(ctx);
        public AssetCategory Category => AssetCategory.ProjectSetting;
        public float Progress => 1f;

        public IEnumerable<AssetEntry> AnalyzeProject(ScanContext ctx)
        {
            var entry = new AssetEntry
            {
                Path = "ProjectSettings/Android",
                Name = "Android compatibility",
                Directory = "ProjectSettings",
                Category = AssetCategory.ProjectSetting,
                TypeLabel = "Android compatibility",
                RuntimeSize = -1,
                FileSize = -1
            };

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                entry.Finalise();
                yield return entry;
                yield break;
            }

            var nativeArtifacts = new List<string>();
            CollectFiles("Assets/Plugins/Android", nativeArtifacts);
            CollectFiles("Packages", nativeArtifacts);
            CollectFiles("Library/PackageCache", nativeArtifacts);
            nativeArtifacts.Sort(StringComparer.OrdinalIgnoreCase);

            int checkedLibraries = 0;
            int incompatible = 0;
            int unknown = 0;
            var issues = new List<string>();
            for (int i = 0; i < nativeArtifacts.Count; i++)
            {
                string path = nativeArtifacts[i];
                if (path.EndsWith(".aar", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using (var archive = ZipFile.OpenRead(Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar))))
                        {
                            foreach (var zip in archive.Entries)
                            {
                                if (!zip.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) continue;
                                checkedLibraries++;
                                using (var input = zip.Open())
                                using (var memory = new MemoryStream())
                                {
                                    input.CopyTo(memory);
                                    memory.Position = 0;
                                    var alignment = ReadElfLoadAlignment(memory);
                                    if (!alignment.HasValue) { unknown++; issues.Add(path + "!" + zip.FullName + " (alignment unavailable)"); }
                                    else if (alignment.Value < PageAlignment) { incompatible++; issues.Add(path + "!" + zip.FullName + " (PT_LOAD align " + alignment.Value + ")"); }
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        unknown++;
                        issues.Add(path + " (AAR inspection failed: " + e.Message + ")");
                    }
                }
                else if (path.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                {
                    checkedLibraries++;
                    try
                    {
                        using (var stream = File.OpenRead(Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar))))
                        {
                            var alignment = ReadElfLoadAlignment(stream);
                            if (!alignment.HasValue) { unknown++; issues.Add(path + " (alignment unavailable)"); }
                            else if (alignment.Value < PageAlignment) { incompatible++; issues.Add(path + " (PT_LOAD align " + alignment.Value + ")"); }
                        }
                    }
                    catch (Exception e)
                    {
                        unknown++;
                        issues.Add(path + " (inspection failed: " + e.Message + ")");
                    }
                }
            }

            entry.Set("nativeLibrariesChecked", (long)checkedLibraries);
            entry.Set("nativeLibrariesIncompatible16Kb", (long)incompatible);
            entry.Set("nativeLibrariesUnknown16Kb", (long)unknown);

            using (RuleTiming.Begin("AND001", entry.Path))
            {
                if (incompatible > 0 || unknown > 0)
                {
                    var issue = entry.AddIssue("AND001", incompatible + " incompatible / " + unknown + " unconfirmed native library(s)",
                        "All shipped native ELF PT_LOAD segments aligned to at least 16384 bytes", 0, null,
                        "Static project-side inspection found native Android libraries that are below the 16 KB PT_LOAD alignment requirement or could not be inspected. Final APK/AAB contents still require build-time verification.");
                    if (issue != null && issues.Count > 0)
                        issue.Items("Affected native artifacts", issues, DetailKind.Bad, arePaths: false);
                }
            }

            string ndkRevision = ReadNdkRevision();
            entry.Set("ndkRevision", ndkRevision);
            using (RuleTiming.Begin("AND002", entry.Path))
            {
                if (checkedLibraries > 0 && !string.IsNullOrEmpty(ndkRevision) && CompareVersionMajor(ndkRevision, 28) < 0)
                    entry.AddIssue("AND002", "NDK " + ndkRevision, "Review final ELF alignment/toolchain compatibility", 0, null,
                        "A native Android library is present and the detected NDK major version is below r28. This is a review signal only; plugin binaries and linker flags can determine final compatibility independently.");
                else if (checkedLibraries > 0 && string.IsNullOrEmpty(ndkRevision))
                    entry.AddIssue("AND002", "NDK version unavailable", "Confirm native toolchain provenance", 0, null,
                        "Native Android libraries are present but the configured NDK revision could not be resolved from the editor's external-tools configuration.");
            }

            string agpVersion = ReadCustomAgpVersion();
            entry.Set("androidGradlePluginVersion", agpVersion);
            using (RuleTiming.Begin("AND003", entry.Path))
            {
                if (!string.IsNullOrEmpty(agpVersion) && CompareVersion(agpVersion, 8, 5, 1) < 0)
                    entry.AddIssue("AND003", "AGP " + agpVersion, "Review AGP 8.5.1+ compatibility", 0, null,
                        "A custom Android Gradle template declares an Android Gradle Plugin version below the 8.5.1 baseline referenced by Android's current 16 KB packaging guidance. This diagnostic does not mutate build templates automatically.");
            }

            entry.Finalise();
            yield return entry;
        }

        static string ReadCustomAgpVersion()
        {
            string[] candidates =
            {
                "Assets/Plugins/Android/mainTemplate.gradle",
                "Assets/Plugins/Android/baseProjectTemplate.gradle"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                string absolute = Path.Combine(Directory.GetCurrentDirectory(), candidates[i].Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolute)) continue;
                try
                {
                    string text = File.ReadAllText(absolute);
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"com\.android\.tools\.build:gradle:([0-9]+(?:\.[0-9]+){1,3})");
                    if (match.Success) return match.Groups[1].Value;
                }
                catch { }
            }
            return string.Empty;
        }

        static void CollectFiles(string rootRelative, List<string> output)
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), rootRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) return;
            try
            {
                output.AddRange(Directory.GetFiles(root, "*.so", SearchOption.AllDirectories).Length == 0
                    ? Array.Empty<string>() : NormalizePaths(Directory.GetFiles(root, "*.so", SearchOption.AllDirectories)));
                output.AddRange(Directory.GetFiles(root, "*.aar", SearchOption.AllDirectories).Length == 0
                    ? Array.Empty<string>() : NormalizePaths(Directory.GetFiles(root, "*.aar", SearchOption.AllDirectories)));
            }
            catch { }
        }

        static IEnumerable<string> NormalizePaths(string[] paths)
        {
            string root = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/');
            for (int i = 0; i < paths.Length; i++)
            {
                string value = paths[i].Replace('\\', '/');
                if (value.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) value = value.Substring(root.Length + 1);
                yield return value;
            }
        }

        static long? ReadElfLoadAlignment(Stream stream)
        {
            if (stream == null || !stream.CanSeek || !stream.CanRead) return null;
            byte[] header = new byte[64];
            if (stream.Read(header, 0, header.Length) < header.Length) return null;
            if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F') return null;
            if (header[5] != 1) return null;
            int elfClass = header[4];
            if (elfClass != 1 && elfClass != 2) return null;
            long programHeaderOffset = elfClass == 2 ? ReadInt64(header, 32) : ReadUInt32(header, 28);
            int entrySize = elfClass == 2 ? ReadUInt16(header, 54) : ReadUInt16(header, 42);
            int entryCount = elfClass == 2 ? ReadUInt16(header, 56) : ReadUInt16(header, 44);
            if (programHeaderOffset < 0 || entrySize <= 0 || entryCount <= 0 || programHeaderOffset + (long)entrySize * entryCount > stream.Length) return null;
            long worst = 0;
            bool foundLoad = false;
            for (int i = 0; i < entryCount; i++)
            {
                stream.Position = programHeaderOffset + (long)i * entrySize;
                byte[] ph = new byte[entrySize];
                if (stream.Read(ph, 0, ph.Length) < ph.Length) return null;
                uint type = ReadUInt32(ph, 0);
                if (type != 1) continue;
                long align = elfClass == 2 ? ReadInt64(ph, 48) : ReadUInt32(ph, 28);
                if (align <= 0) return null;
                foundLoad = true;
                if (worst == 0 || align < worst) worst = align;
            }
            return foundLoad ? worst : (long?)null;
        }

        static ushort ReadUInt16(byte[] data, int offset) => (ushort)(data[offset] | (data[offset + 1] << 8));
        static uint ReadUInt32(byte[] data, int offset) => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        static long ReadInt64(byte[] data, int offset)
        {
            unchecked
            {
                ulong value = 0;
                for (int i = 7; i >= 0; i--) value = (value << 8) | data[offset + i];
                return (long)value;
            }
        }

        static string ReadNdkRevision()
        {
            try
            {
                Type type = FindType("UnityEditor.Android.AndroidExternalToolsSettings");
                if (type == null) return string.Empty;
                var prop = type.GetProperty("ndkRootPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?? type.GetProperty("ndkPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                string path = prop?.GetValue(null, null) as string;
                if (string.IsNullOrEmpty(path)) return string.Empty;
                string properties = Path.Combine(path, "source.properties");
                if (!File.Exists(properties)) return string.Empty;
                foreach (string line in File.ReadAllLines(properties))
                {
                    if (!line.StartsWith("Pkg.Revision", StringComparison.OrdinalIgnoreCase)) continue;
                    int equals = line.IndexOf('=');
                    return equals >= 0 ? line.Substring(equals + 1).Trim() : string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var type = assembly.GetType(fullName, false); if (type != null) return type; } catch { }
            }
            return null;
        }

        static int CompareVersion(string value, int major, int minor, int patch)
        {
            if (string.IsNullOrEmpty(value)) return -1;
            string[] parts = value.Split('.');
            int a = parts.Length > 0 && int.TryParse(parts[0], out var av) ? av : -1;
            int b = parts.Length > 1 && int.TryParse(parts[1], out var bv) ? bv : 0;
            int c = parts.Length > 2 && int.TryParse(parts[2], out var cv) ? cv : 0;
            if (a != major) return a.CompareTo(major);
            if (b != minor) return b.CompareTo(minor);
            return c.CompareTo(patch);
        }

        static int CompareVersionMajor(string value, int required)
        {
            int start = value.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            int end = start;
            while (end < value.Length && char.IsDigit(value[end])) end++;
            if (end == start || !int.TryParse(value.Substring(start, end - start), out int major)) return -1;
            return major.CompareTo(required);
        }
    }
}

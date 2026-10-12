using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Durable, Unity-independent representation of the latest Player BuildReport.</summary>
    public static class BuildReportStore
    {
        [Serializable] public sealed class StoredReport
        {
            public int version = 6;
            public string capturedAtUtc;
            public StoredBuildSummary summary = new StoredBuildSummary();
            public StoredBuildFile[] files = Array.Empty<StoredBuildFile>();
            public StoredPackedAssets[] packedAssets = Array.Empty<StoredPackedAssets>();
            public StoredBuildStep[] steps = Array.Empty<StoredBuildStep>();
        }
        [Serializable] public sealed class StoredBuildSummary
        {
            public string guid;
            public string platform;
            public string platformGroup;
            public string subtarget;
            public string buildType;
            public string result;
            public string outputPath;
            public string buildStartedAt;
            public string buildEndedAt;
            public string buildProfilePath;
            public string buildProfileGuid;
            public string projectFingerprint;
            public string contentIdentity;
            public string commitId;
            public string unityVersion;
            public string evidenceQuality;
            public string evidenceQualityReason;
            public long totalSize;
            public double totalTimeSeconds;
            public int totalErrors;
            public int totalWarnings;
        }
        [Serializable] public sealed class StoredBuildFile { public string path; public long size; public string role; }
        [Serializable] public sealed class StoredPackedAssets { public string shortPath; public long overhead; public StoredPackedAsset[] contents = Array.Empty<StoredPackedAsset>(); }
        [Serializable] public sealed class StoredPackedAsset { public string sourceAssetPath; public string sourceAssetGUID; public string typeName; public long packedSize; public long offset; }
        [Serializable] public sealed class StoredBuildStep { public string name; public int depth; public double durationSeconds; public int messageCount; }
        [Serializable] sealed class History { public List<HistoryItem> items = new List<HistoryItem>(); }
        [Serializable] public sealed class HistoryItem { public string guid; public string platform; public string platformGroup; public string subtarget; public string buildType; public string buildProfilePath; public string buildProfileGuid; public string projectFingerprint; public string unityVersion; public string outputPath; public long size; public double seconds; public int errors; public int warnings; public string capturedAtUtc; }

        const int k_Version = 6;
        static string Folder => ProjectOptimizerConstants.BuildReportsFolder;
        static string LatestPath => ProjectOptimizerConstants.BuildReportLatestPath;
        static string HistoryPath => ProjectOptimizerConstants.BuildReportHistoryPath;
        public static string GetLatestPath() => LatestPath;

        public static StoredReport LoadOrCaptureLatest()
        {
            var stored = LoadLatest();
            try
            {
                var latest = BuildReport.GetLatestReport();
                if (latest == null || latest.summary.buildType != BuildType.Player) return stored;

                string latestGuid = latest.summary.guid.ToString();
                bool sameBuild = stored != null && stored.summary != null
                    && string.Equals(stored.summary.guid, latestGuid, StringComparison.OrdinalIgnoreCase);
                bool needsRefresh = !sameBuild;
                if (!needsRefresh && stored != null && !string.Equals(stored.summary.evidenceQuality, "Complete", StringComparison.OrdinalIgnoreCase))
                    needsRefresh = IsComplete(latest.summary, latest);

                if (needsRefresh) return CaptureLatest(latest);
                return stored ?? CaptureLatest(latest);
            }
            catch (Exception e)
            {
                Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not import the latest BuildReport: " + e.Message);
                return stored;
            }
        }

        public static StoredReport LoadLatest()
        {
            try
            {
                if (!File.Exists(LatestPath)) return null;
                var value = JsonUtility.FromJson<StoredReport>(File.ReadAllText(LatestPath));
                if (value == null || value.version != k_Version || value.summary == null) return null;
                return value;
            }
            catch (Exception e)
            {
                Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read persisted BuildReport: " + e.Message);
                return null;
            }
        }

        static StoredReport CaptureLatest(BuildReport report)
        {
            string profilePath = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ProfilePath : string.Empty;
            string profileGuid = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ProfileGuid : string.Empty;
            string fingerprint = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ProjectFingerprint : string.Empty;
            string contentIdentity = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ContentIdentity : string.Empty;
            return Capture(report, profilePath, profileGuid, fingerprint, contentIdentity);
        }

        public static StoredReport Capture(BuildReport report, string profilePath = null, string profileGuid = null, string projectFingerprint = null, string contentIdentity = null)
        {
            if (report == null || report.summary.buildType != BuildType.Player) return null;
            var s = report.summary;
            string capturedProfilePath = profilePath ?? string.Empty;
            string capturedProfileGuid = profileGuid ?? string.Empty;
            string capturedContentIdentity = contentIdentity ?? string.Empty;
            // BuildSummary does not expose the Build Profile that produced the report. Only the
            // preprocess callback can provide trustworthy profile provenance for this build. When a
            // report is imported independently, leave the profile fields empty rather than guessing
            // from whatever profile happens to be active now.
            var stored = new StoredReport
            {
                version = k_Version,
                capturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                summary = new StoredBuildSummary
                {
                    guid = s.guid.ToString(), platform = s.platform.ToString(), platformGroup = s.platformGroup.ToString(),
                    subtarget = SafeSubtarget(s), buildType = s.buildType.ToString(), result = s.result.ToString(), outputPath = s.outputPath ?? "",
                    buildStartedAt = s.buildStartedAt.ToString("O", CultureInfo.InvariantCulture), buildEndedAt = s.buildEndedAt.ToString("O", CultureInfo.InvariantCulture),
                    buildProfilePath = capturedProfilePath, buildProfileGuid = capturedProfileGuid, projectFingerprint = projectFingerprint ?? string.Empty, contentIdentity = capturedContentIdentity, commitId = EvidenceContract.ComputeSourceCommitIdentity(), unityVersion = Application.unityVersion,
                    totalSize = ToLong(s.totalSize), totalTimeSeconds = s.totalTime.TotalSeconds, totalErrors = s.totalErrors, totalWarnings = s.totalWarnings,
                    evidenceQuality = IsComplete(s, report) ? "Complete" : "Partial",
                    evidenceQualityReason = string.IsNullOrEmpty(capturedProfilePath)
                        ? "Build Profile provenance unavailable at capture time."
                        : "Build Profile provenance captured at the build-start checkpoint."
                },
                files = CaptureFiles(report.GetFiles()), packedAssets = CapturePacked(report.packedAssets), steps = CaptureSteps(report.steps)
            };
            Save(stored);
            RecordHistory(stored);
            return stored;
        }

        static string SafeSubtarget(BuildSummary s)
        {
            try
            {
                switch (s.platform)
                {
                    case BuildTarget.StandaloneWindows:
                    case BuildTarget.StandaloneWindows64:
                    case BuildTarget.StandaloneOSX:
                    case BuildTarget.StandaloneLinux64:
                        return s.GetSubtarget<StandaloneBuildSubtarget>().ToString();
                    case BuildTarget.PS4:
                        return s.GetSubtarget<PS4BuildSubtarget>().ToString();
                    case BuildTarget.XboxOne:
                        return s.GetSubtarget<XboxBuildSubtarget>().ToString();
                    default:
                        return "Unknown";
                }
            }
            catch { return "Unknown"; }
        }
        static bool ProfilesCompatibleWithoutProjectFallback(HistoryItem history, StoredBuildSummary current)
        {
            if (!string.IsNullOrEmpty(history.buildProfileGuid) || !string.IsNullOrEmpty(current.buildProfileGuid))
                return !string.IsNullOrEmpty(history.buildProfileGuid) && !string.IsNullOrEmpty(current.buildProfileGuid)
                    && string.Equals(history.buildProfileGuid, current.buildProfileGuid, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(history.buildProfilePath) || !string.IsNullOrEmpty(current.buildProfilePath))
                return !string.IsNullOrEmpty(history.buildProfilePath) && !string.IsNullOrEmpty(current.buildProfilePath)
                    && string.Equals(history.buildProfilePath, current.buildProfilePath, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        static bool ProfilesCompatible(HistoryItem history, StoredBuildSummary current)
        {
            if (history == null || current == null) return false;
            if (!string.IsNullOrEmpty(history.projectFingerprint) || !string.IsNullOrEmpty(current.projectFingerprint))
            {
                if (string.IsNullOrEmpty(history.projectFingerprint) || string.IsNullOrEmpty(current.projectFingerprint)) return false;
                if (!string.Equals(history.projectFingerprint, current.projectFingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return ProfilesCompatibleWithoutProjectFallback(history, current);
        }

        static bool IsComplete(BuildSummary s, BuildReport r)
        {
            return string.Equals(s.result.ToString(), BuildResult.Succeeded.ToString(), StringComparison.OrdinalIgnoreCase)
                && s.totalSize > 0 && r.GetFiles() != null;
        }
        static StoredBuildFile[] CaptureFiles(BuildFile[] files)
        {
            if (files == null) return Array.Empty<StoredBuildFile>();
            var result = new StoredBuildFile[files.Length];
            for (int i=0;i<files.Length;i++) result[i] = new StoredBuildFile { path=files[i].path, size=ToLong(files[i].size), role=files[i].role ?? "" };
            return result;
        }
        static StoredPackedAssets[] CapturePacked(PackedAssets[] packs)
        {
            if (packs == null) return Array.Empty<StoredPackedAssets>();
            var result = new StoredPackedAssets[packs.Length];
            for (int i=0;i<packs.Length;i++)
            {
                var p = packs[i];
                var contents = p != null ? p.contents : null;
                var arr = contents == null ? Array.Empty<StoredPackedAsset>() : new StoredPackedAsset[contents.Length];
                if (contents != null) for(int j=0;j<contents.Length;j++) arr[j] = new StoredPackedAsset { sourceAssetPath=contents[j].sourceAssetPath, sourceAssetGUID=contents[j].sourceAssetGUID.ToString(), typeName=contents[j].type != null ? contents[j].type.ToString() : "Unknown", packedSize=ToLong(contents[j].packedSize), offset=ToLong(contents[j].offset) };
                result[i] = new StoredPackedAssets { shortPath=p != null ? p.shortPath : "", overhead=p != null ? ToLong(p.overhead) : 0L, contents=arr };
            }
            return result;
        }
        static StoredBuildStep[] CaptureSteps(BuildStep[] steps)
        {
            if (steps == null) return Array.Empty<StoredBuildStep>();
            var result = new StoredBuildStep[steps.Length];
            for(int i=0;i<steps.Length;i++) result[i]=new StoredBuildStep { name=steps[i].name, depth=steps[i].depth, durationSeconds=steps[i].duration.TotalSeconds, messageCount=steps[i].messages != null ? steps[i].messages.Length : 0 };
            return result;
        }
        static long ToLong(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;
        static long ToLong(long v) => Math.Max(0L, v);
        static void Save(StoredReport value)
        {
            try { CacheFileUtility.EnsureCacheFolder(); Directory.CreateDirectory(Folder); var tmp=LatestPath+".tmp"; File.WriteAllText(tmp, JsonUtility.ToJson(value,true)); CacheFileUtility.ReplaceFile(tmp, LatestPath); }
            catch(Exception e){ Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not persist BuildReport: "+e.Message); }
        }
        static void RecordHistory(StoredReport value)
        {
            try
            {
                Directory.CreateDirectory(Folder); var history = File.Exists(HistoryPath) ? JsonUtility.FromJson<History>(File.ReadAllText(HistoryPath)) : new History(); if(history==null) history=new History();
                string guid=value.summary.guid ?? ""; if(!string.IsNullOrEmpty(guid) && history.items.Exists(x=>x!=null && x.guid==guid)) return;
                history.items.Insert(0,new HistoryItem { guid=guid, platform=value.summary.platform, platformGroup=value.summary.platformGroup, subtarget=value.summary.subtarget, buildType=value.summary.buildType, buildProfilePath=value.summary.buildProfilePath, buildProfileGuid=value.summary.buildProfileGuid, projectFingerprint=value.summary.projectFingerprint, unityVersion=value.summary.unityVersion, outputPath=value.summary.outputPath, size=value.summary.totalSize, seconds=value.summary.totalTimeSeconds, errors=value.summary.totalErrors, warnings=value.summary.totalWarnings, capturedAtUtc=value.capturedAtUtc });
                if(history.items.Count>20) history.items.RemoveRange(20,history.items.Count-20); File.WriteAllText(HistoryPath,JsonUtility.ToJson(history,true));
            } catch(Exception e){ Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not persist build history: "+e.Message); }
        }

        public sealed class BuildDelta
        {
            public bool HasPrevious;
            public string CompatibilityNote;
            public long SizeDelta;
            public double DurationDelta;
            public int WarningDelta;
            public int ErrorDelta;
            public StoredBuildFile[] LargestFileGrowth = Array.Empty<StoredBuildFile>();
        }

        public static BuildDelta CompareLatestToPrevious()
        {
            var current = LoadLatest();
            if (current == null || current.summary == null) return new BuildDelta { CompatibilityNote = "No stored build report." };
            var previous = FindPreviousCompatible(current);
            if (previous == null) return new BuildDelta { CompatibilityNote = "No compatible previous build report." };

            // History intentionally stores only summary data; only a previous report with compatible
            // target, Unity/project identity and Build Profile identity is eligible as a baseline.
            var delta = new BuildDelta
            {
                HasPrevious = true,
                SizeDelta = current.summary.totalSize - previous.size,
                DurationDelta = current.summary.totalTimeSeconds - previous.seconds,
                WarningDelta = current.summary.totalWarnings - previous.warnings,
                ErrorDelta = current.summary.totalErrors - previous.errors,
                CompatibilityNote = "Compared with the previous compatible " + previous.platform + " build"
            };
            return delta;
        }

        public static HistoryItem FindPreviousCompatible(StoredReport current)
        {
            if(current==null || current.summary==null) return null;
            try
            {
                if(!File.Exists(HistoryPath)) return null; var history=JsonUtility.FromJson<History>(File.ReadAllText(HistoryPath)); if(history==null) return null;
                for(int i=0;i<history.items.Count;i++){ var x=history.items[i]; if(x==null || string.IsNullOrEmpty(x.guid) || x.guid==current.summary.guid) continue; if(string.Equals(x.platform,current.summary.platform,StringComparison.OrdinalIgnoreCase) && string.Equals(x.platformGroup,current.summary.platformGroup,StringComparison.OrdinalIgnoreCase) && string.Equals(x.subtarget,current.summary.subtarget,StringComparison.OrdinalIgnoreCase) && string.Equals(x.buildType,current.summary.buildType,StringComparison.OrdinalIgnoreCase) && ProfilesCompatible(x,current.summary)) return x; }
            }catch{}
            return null;
        }
    }

    static class BuildProfileBuildProvenance
    {
        public static string BuildGuid;
        public static string ProfilePath;
        public static string ProfileGuid;
        public static string ProjectFingerprint;
        public static string ContentIdentity;

        public static void Capture(BuildReport report)
        {
            try
            {
                if (report == null) return;
                BuildGuid = report.summary.guid.ToString();
                var profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
                ProfilePath = profile == null ? string.Empty : (AssetDatabase.GetAssetPath(profile) ?? string.Empty);
                ProfileGuid = string.IsNullOrEmpty(ProfilePath) ? string.Empty : AssetDatabase.AssetPathToGUID(ProfilePath);
                ProjectFingerprint = EvidenceIdentity.ComputeProjectFingerprint();
                ContentIdentity = EvidenceContract.ComputeProjectAssetIdentity();
            }
            catch
            {
                BuildGuid = report != null ? report.summary.guid.ToString() : string.Empty;
                ProfilePath = string.Empty;
                ProfileGuid = string.Empty;
                ProjectFingerprint = string.Empty;
                ContentIdentity = string.Empty;
            }
        }

        public static bool AppliesTo(BuildReport report)
            => report != null && string.Equals(BuildGuid, report.summary.guid.ToString(), StringComparison.OrdinalIgnoreCase);

        public static void Clear()
        {
            BuildGuid = string.Empty; ProfilePath = string.Empty; ProfileGuid = string.Empty; ProjectFingerprint = string.Empty; ContentIdentity = string.Empty;
        }
    }

    sealed class BuildReportPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;
        public void OnPreprocessBuild(BuildReport report) => BuildProfileBuildProvenance.Capture(report);
    }

    sealed class BuildReportPostProcessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                if (report != null)
                {
                    string profilePath = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ProfilePath : string.Empty;
                    string profileGuid = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ProfileGuid : string.Empty;
                    string projectFingerprint = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ProjectFingerprint : string.Empty;
                    string contentIdentity = BuildProfileBuildProvenance.AppliesTo(report) ? BuildProfileBuildProvenance.ContentIdentity : string.Empty;
                    BuildReportStore.Capture(report, profilePath, profileGuid, projectFingerprint, contentIdentity);
                    BuildProfileBuildProvenance.Clear();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " BuildReport capture failed: " + e.Message);
            }
        }
    }
}

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe
{
    public enum ThreadSafeAssetPathToGUIDOptions
    {
        IncludeRecentlyDeletedAssets = 0,
        OnlyExistingAssets = 1
    }

    public readonly struct AssetMetadataRecord
    {
        public readonly string AssetPath;
        public readonly string Guid;
        public readonly string MetaFilePath;
        public readonly long FileSize;
        public readonly long LastWriteTicks;
        public readonly long MetaLastWriteTicks;

        public AssetMetadataRecord(string assetPath, string guid, string metaFilePath)
            : this(assetPath, guid, metaFilePath, 0L, 0L, 0L) { }

        public AssetMetadataRecord(string assetPath, string guid, string metaFilePath, long fileSize, long lastWriteTicks, long metaLastWriteTicks)
        {
            AssetPath = assetPath;
            Guid = guid;
            MetaFilePath = metaFilePath;
            FileSize = fileSize;
            LastWriteTicks = lastWriteTicks;
            MetaLastWriteTicks = metaLastWriteTicks;
        }
    }

    public sealed class AssetDatabaseThreadSafeBuildProgress
    {
        private long _metaFilesSeen;
        private long _metaFilesParsed;
        private long _invalidMetaFiles;
        private long _directoriesScanned;
        private long _errors;

        public long MetaFilesSeen => Interlocked.Read(ref _metaFilesSeen);
        public long MetaFilesParsed => Interlocked.Read(ref _metaFilesParsed);
        public long InvalidMetaFiles => Interlocked.Read(ref _invalidMetaFiles);
        public long DirectoriesScanned => Interlocked.Read(ref _directoriesScanned);
        public long Errors => Interlocked.Read(ref _errors);
        public string CurrentDirectory { get; internal set; }

        internal void IncrementMetaFilesSeen() => Interlocked.Increment(ref _metaFilesSeen);
        internal void IncrementMetaFilesParsed() => Interlocked.Increment(ref _metaFilesParsed);
        internal void IncrementInvalidMetaFiles() => Interlocked.Increment(ref _invalidMetaFiles);
        internal void IncrementDirectoriesScanned() => Interlocked.Increment(ref _directoriesScanned);
        internal void IncrementErrors() => Interlocked.Increment(ref _errors);
    }

    public readonly struct DuplicateGuidRecord
    {
        public readonly string Guid;
        public readonly string FirstAssetPath;
        public readonly string DuplicateAssetPath;

        public DuplicateGuidRecord(string guid, string firstAssetPath, string duplicateAssetPath)
        {
            Guid = guid;
            FirstAssetPath = firstAssetPath;
            DuplicateAssetPath = duplicateAssetPath;
        }
    }

    public sealed class AssetDatabaseThreadSafeBuildResult
    {
        internal AssetDatabaseThreadSafeBuildResult(
            Dictionary<string, string> pathToGuid,
            Dictionary<string, GUID> pathToGuidValue,
            Dictionary<string, string> guidToPath,
            Dictionary<GUID, string> guidValueToPath,
            List<string> folderPaths,
            HashSet<string> folderPathSet,
            Dictionary<string, string[]> folderToSubFolders,
            Dictionary<string, AssetMetadataRecord> fileMetadata,
            string projectRoot,
            bool includedPackages,
            long metaFilesSeen,
            long metaFilesParsed,
            long invalidMetaFiles,
            long duplicateGuids,
            long directoriesScanned,
            long errors,
            List<DuplicateGuidRecord> duplicateGuidRecords,
            List<string> invalidMetaFilePaths,
            List<string> errorDirectories,
            double elapsedMilliseconds,
            long journalGeneration)
        {
            PathToGuid = pathToGuid;
            PathToGuidValue = pathToGuidValue;
            GuidToPath = guidToPath;
            GuidValueToPath = guidValueToPath;
            FolderPaths = folderPaths;
            FolderPathSet = folderPathSet;
            FolderToSubFolders = folderToSubFolders;
            FileMetadata = fileMetadata;
            ProjectRoot = projectRoot;
            IncludedPackages = includedPackages;
            MetaFilesSeen = metaFilesSeen;
            MetaFilesParsed = metaFilesParsed;
            InvalidMetaFiles = invalidMetaFiles;
            DuplicateGuids = duplicateGuids;
            DirectoriesScanned = directoriesScanned;
            Errors = errors;
            DuplicateGuidRecords = duplicateGuidRecords;
            InvalidMetaFilePaths = invalidMetaFilePaths;
            ErrorDirectories = errorDirectories;
            ElapsedMilliseconds = elapsedMilliseconds;
            JournalGeneration = journalGeneration;
        }

        internal Dictionary<string, string> PathToGuid { get; }
        internal Dictionary<string, GUID> PathToGuidValue { get; }
        internal Dictionary<string, string> GuidToPath { get; }
        internal Dictionary<GUID, string> GuidValueToPath { get; }
        internal List<string> FolderPaths { get; }
        internal HashSet<string> FolderPathSet { get; }
        internal Dictionary<string, string[]> FolderToSubFolders { get; }
        internal Dictionary<string, AssetMetadataRecord> FileMetadata { get; }

        public string ProjectRoot { get; }
        public bool IncludedPackages { get; }
        public long MetaFilesSeen { get; }
        public long MetaFilesParsed { get; }
        public long InvalidMetaFiles { get; }
        public long DuplicateGuids { get; }
        public long DirectoriesScanned { get; }
        public long Errors { get; }
        public double ElapsedMilliseconds { get; }
        public long JournalGeneration { get; }
        public IReadOnlyList<DuplicateGuidRecord> DuplicateGuidRecords { get; }
        public IReadOnlyList<string> InvalidMetaFilePaths { get; }
        public IReadOnlyList<string> ErrorDirectories { get; }
        public int IndexedAssetCount => CountAssetPaths(PathToGuid, FolderPathSet);
        public int IndexedFolderCount => FolderPaths.Count;

        static int CountAssetPaths(Dictionary<string, string> pathToGuid, HashSet<string> folderPathSet)
        {
            if (pathToGuid == null || pathToGuid.Count == 0) return 0;
            int count = 0;
            foreach (var path in pathToGuid.Keys)
                if (folderPathSet == null || !folderPathSet.Contains(path)) count++;
            return count;
        }
        public bool IsComplete => Errors == 0 && InvalidMetaFiles == 0;
    }

    public static class AssetDatabaseThreadSafeUtility
    {
        private const int MetaProbeBytes = 8192;
        private const int MetaBatchSize = 2048;
        private const int IndexBuildWorkerLimit = 8;
        private const int DiagnosticListLimit = 10000;
        private const string MetaExtension = ".meta";
        private const string GuidKey = "guid:";
        private const string LabelsKey = "labels:";
        private const int SnapshotVersion = 7;
        private static readonly string SnapshotPath = ProjectOptimizerConstants.AssetDatabaseSnapshotPath;

        private static readonly object PublishLock = new object();
        private static readonly ConcurrentQueue<PendingAssetChanges> s_PendingChanges = new ConcurrentQueue<PendingAssetChanges>();
        private static int s_Persisting;
        private static int s_PersistenceRequested;
        private static readonly object PersistentSaveLock = new object();
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly StringComparer GuidComparer = StringComparer.OrdinalIgnoreCase;

        private static Dictionary<string, string> s_PathToGuid = new Dictionary<string, string>(PathComparer);
        private static Dictionary<string, GUID> s_PathToGuidValue = new Dictionary<string, GUID>(PathComparer);
        private static Dictionary<string, string> s_GuidToPath = new Dictionary<string, string>(GuidComparer);
        private static Dictionary<GUID, string> s_GuidValueToPath = new Dictionary<GUID, string>();
        private static List<string> s_FolderPaths = new List<string>();
        private static HashSet<string> s_FolderPathSet = new HashSet<string>(PathComparer);
        private static Dictionary<string, string[]> s_FolderToSubFolders = new Dictionary<string, string[]>(PathComparer);
        private static Dictionary<string, AssetMetadataRecord> s_FileMetadata = new Dictionary<string, AssetMetadataRecord>(PathComparer);
        private static ConcurrentDictionary<string, string[]> s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
        private static string s_ProjectRoot;
        private static int s_Initialized;
        private static int s_Stale;
        private static int s_Generation;
        private static long s_LastBuildElapsedMs;
        private static long s_LastBuildIndexedAssetCount;
        private static long s_LastBuildErrors;
        private static long s_LastBuildInvalidMetaFiles;
        private static bool s_IncludePackages;
        private static string s_SnapshotUnityVersion = string.Empty;
        private static uint s_SnapshotArtifactDependencyVersion;
        private static uint s_PersistedSnapshotArtifactDependencyVersion;
        private static bool s_LoadedPersistentSnapshot;
        private static long s_PersistentSnapshotBuildErrors;
        private static long s_PersistentSnapshotInvalidMetaFiles;
        private static long s_SnapshotGeneration;
        private static long s_PersistedSnapshotGeneration;
        private static long s_JournalGeneration;
        private static long s_JournalBaseGeneration;
        private static bool s_JournalCorrupt;
        private static bool s_NeedsAuthoritativeReconcile;
        private static int s_LastJournalRecordsApplied;
        private static bool s_DomainReloadObserved;
        private static string s_LastStateAction = "None";
        private static string s_LastReconcileReason = "None";
        private static bool s_PersistentSnapshotUsedThisInitialization;
        private static int s_LastAuthoritativeAdded;
        private static int s_LastAuthoritativeRemoved;

        public readonly struct AssetChangeRecord
        {
            public readonly long Generation;
            public readonly string[] Imported;
            public readonly string[] Deleted;
            public readonly string[] Moved;
            public readonly string[] MovedFrom;

            internal AssetChangeRecord(AssetDatabaseChangeJournal.Record record)
            {
                Generation = record.Generation;
                Imported = record.Imported ?? Array.Empty<string>();
                Deleted = record.Deleted ?? Array.Empty<string>();
                Moved = record.Moved ?? Array.Empty<string>();
                MovedFrom = record.MovedFrom ?? Array.Empty<string>();
            }
        }

        sealed class PendingAssetChanges
        {
            public readonly long Generation;
            public readonly string[] Imported;
            public readonly string[] Deleted;
            public readonly string[] Moved;
            public readonly string[] MovedFrom;

            public PendingAssetChanges(long generation, string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                Generation = generation;
                Imported = imported ?? Array.Empty<string>();
                Deleted = deleted ?? Array.Empty<string>();
                Moved = moved ?? Array.Empty<string>();
                MovedFrom = movedFrom ?? Array.Empty<string>();
            }

            public bool HasChanges => Imported.Length != 0 || Deleted.Length != 0 || Moved.Length != 0 || MovedFrom.Length != 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (PublishLock)
            {
                s_PathToGuid = new Dictionary<string, string>(PathComparer);
                s_PathToGuidValue = new Dictionary<string, GUID>(PathComparer);
                s_GuidToPath = new Dictionary<string, string>(GuidComparer);
                s_GuidValueToPath = new Dictionary<GUID, string>();
                s_FolderPaths = new List<string>();
                s_FolderPathSet = new HashSet<string>(PathComparer);
                s_FolderToSubFolders = new Dictionary<string, string[]>(PathComparer);
                s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
                s_ProjectRoot = null;
                s_IncludePackages = false;
                s_SnapshotUnityVersion = string.Empty;
                s_SnapshotArtifactDependencyVersion = 0;
                s_PersistedSnapshotArtifactDependencyVersion = 0;
                s_LoadedPersistentSnapshot = false;
                Volatile.Write(ref s_Initialized, 0);
                Volatile.Write(ref s_Stale, 0);
                Volatile.Write(ref s_Generation, 0);
                Interlocked.Exchange(ref s_LastBuildElapsedMs, 0);
                Interlocked.Exchange(ref s_LastBuildIndexedAssetCount, 0);
                Interlocked.Exchange(ref s_LastBuildErrors, 0);
                Interlocked.Exchange(ref s_LastBuildInvalidMetaFiles, 0);
                Interlocked.Exchange(ref s_PersistentSnapshotBuildErrors, 0);
                Interlocked.Exchange(ref s_PersistentSnapshotInvalidMetaFiles, 0);
                Interlocked.Exchange(ref s_SnapshotGeneration, 0);
                Interlocked.Exchange(ref s_PersistedSnapshotGeneration, 0);
                Interlocked.Exchange(ref s_JournalGeneration, 0);
                Interlocked.Exchange(ref s_JournalBaseGeneration, 0);
                s_JournalCorrupt = false;
                s_NeedsAuthoritativeReconcile = false;
                Interlocked.Exchange(ref s_LastJournalRecordsApplied, 0);
                s_DomainReloadObserved = false;
                s_LastStateAction = "None";
                s_LastReconcileReason = "None";
                s_PersistentSnapshotUsedThisInitialization = false;
                s_LastAuthoritativeAdded = 0;
                s_LastAuthoritativeRemoved = 0;
                s_ForceRebuild = false;
                AssetDatabaseChangeJournal.ResetRuntimeState();
                while (s_PendingChanges.TryDequeue(out _)) { }
                Interlocked.Exchange(ref s_Persisting, 0);
                Interlocked.Exchange(ref s_PersistenceRequested, 0);
            }
        }

        public static bool IsInitialized => Volatile.Read(ref s_Initialized) != 0;
        public static bool IsStale => Volatile.Read(ref s_Stale) != 0;
        public static bool IsReady => IsInitialized && Generation > 0 && !IsStale;
        public static int Generation => Volatile.Read(ref s_Generation);
        public static string ProjectRoot => s_ProjectRoot;
        public static int IndexedAssetCount => CountAssetPaths(s_PathToGuid, s_FolderPathSet);
        public static int IndexedFolderCount => s_FolderPaths.Count;
        public static double LastBuildMilliseconds => Interlocked.Read(ref s_LastBuildElapsedMs);
        public static long LastBuildIndexedAssetCount => Interlocked.Read(ref s_LastBuildIndexedAssetCount);
        public static long LastBuildErrors => Interlocked.Read(ref s_LastBuildErrors);
        public static long LastBuildInvalidMetaFiles => Interlocked.Read(ref s_LastBuildInvalidMetaFiles);
        public static bool IsComplete => IsReady && !NeedsAuthoritativeReconcile && LastBuildErrors == 0 && LastBuildInvalidMetaFiles == 0;
        public static int IndexedFileMetadataCount => s_FileMetadata.Count;
        public static uint SnapshotArtifactDependencyVersion => s_SnapshotArtifactDependencyVersion;
        public static uint PersistedSnapshotArtifactDependencyVersion => s_PersistedSnapshotArtifactDependencyVersion;
        public static bool LoadedPersistentSnapshot => s_LoadedPersistentSnapshot;
        public static long SnapshotGeneration => Interlocked.Read(ref s_SnapshotGeneration);
        public static long PersistedSnapshotGeneration => Interlocked.Read(ref s_PersistedSnapshotGeneration);
        public static long JournalGeneration => Interlocked.Read(ref s_JournalGeneration);
        public static long JournalBaseGeneration => Interlocked.Read(ref s_JournalBaseGeneration);
        public static bool JournalHealthy => !s_JournalCorrupt;
        public static bool NeedsAuthoritativeReconcile => s_NeedsAuthoritativeReconcile || s_ForceRebuild;
        public static bool ArtifactDependencyVersionMismatch => IsReady && s_SnapshotArtifactDependencyVersion != AssetDatabase.GlobalArtifactDependencyVersion;
        public static int LastJournalRecordsApplied => Volatile.Read(ref s_LastJournalRecordsApplied);
        public static bool DomainReloadObserved => s_DomainReloadObserved;
        public static string LastStateAction => s_LastStateAction;
        public static string LastReconcileReason => s_LastReconcileReason;
        static bool s_ForceRebuild;

        public static void Initialize(string projectRoot, bool includePackages = false)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Project root cannot be null or empty.", nameof(projectRoot));

            var fullRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool reuseInMemory;

            lock (PublishLock)
            {
                bool sameProject = string.Equals(s_ProjectRoot, fullRoot, StringComparison.OrdinalIgnoreCase) && s_IncludePackages == includePackages;
                reuseInMemory = sameProject && IsReady && !s_ForceRebuild;

                if (!sameProject)
                {
                    s_ProjectRoot = fullRoot;
                    s_PathToGuid = new Dictionary<string, string>(PathComparer);
                    s_PathToGuidValue = new Dictionary<string, GUID>(PathComparer);
                    s_GuidToPath = new Dictionary<string, string>(GuidComparer);
                    s_GuidValueToPath = new Dictionary<GUID, string>();
                    s_FolderPaths = new List<string>();
                    s_FolderPathSet = new HashSet<string>(PathComparer);
                    s_FolderToSubFolders = new Dictionary<string, string[]>(PathComparer);
                    s_FileMetadata = new Dictionary<string, AssetMetadataRecord>(PathComparer);
                    s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
                    s_IncludePackages = includePackages;
                    s_SnapshotGeneration = 0;
                    s_PersistedSnapshotGeneration = 0;
                    s_JournalGeneration = 0;
                    s_JournalBaseGeneration = 0;
                    s_LastJournalRecordsApplied = 0;
                    s_NeedsAuthoritativeReconcile = false;
                    s_ForceRebuild = false;
                    AssetDatabaseChangeJournal.ResetRuntimeState();
                    Interlocked.Exchange(ref s_Stale, 1);
                    reuseInMemory = false;
                }

                s_SnapshotUnityVersion = Application.unityVersion ?? string.Empty;
                Volatile.Write(ref s_Initialized, 1);
            }

            if (reuseInMemory)
            {
                bool hasPendingChanges = !s_PendingChanges.IsEmpty;
                s_LastStateAction = hasPendingChanges ? "InMemoryReuseAndIncrementalApply" : "InMemoryReuse";
                ApplyPendingChanges();
                return;
            }

            s_PersistentSnapshotUsedThisInitialization = false;
            TryLoadPersistentSnapshot(fullRoot, includePackages);
            LoadJournalAndReplay(fullRoot, includePackages);
            ApplyPendingChanges();
        }

        public static void InitializeFromDataPath(string dataPath, bool includePackages = false)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                throw new ArgumentException("Application.dataPath cannot be null or empty.", nameof(dataPath));

            Initialize(Directory.GetParent(Path.GetFullPath(dataPath)).FullName, includePackages);
        }

        public static async Task<AssetDatabaseThreadSafeBuildResult> BuildIndexAsync(
            string projectRoot,
            bool includePackages,
            AssetDatabaseThreadSafeBuildProgress progress = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => BuildIndex(projectRoot, includePackages, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }


        public static Task<AssetDatabaseThreadSafeBuildResult> RebuildIndexAsync(
            bool includePackages,
            AssetDatabaseThreadSafeBuildProgress progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("AssetDatabaseThreadSafeUtility must be initialized before rebuilding.");

            return BuildIndexAsync(ProjectRoot, includePackages, progress, cancellationToken);
        }

        public static AssetDatabaseThreadSafeBuildResult PublishBuild(AssetDatabaseThreadSafeBuildResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            lock (PublishLock)
            {
                s_ProjectRoot = result.ProjectRoot;
                s_PathToGuid = result.PathToGuid;
                s_PathToGuidValue = result.PathToGuidValue;
                s_GuidToPath = result.GuidToPath;
                s_GuidValueToPath = result.GuidValueToPath;
                s_FolderPaths = result.FolderPaths;
                s_FolderPathSet = result.FolderPathSet;
                s_FolderToSubFolders = result.FolderToSubFolders;
                s_FileMetadata = result.FileMetadata;
                s_IncludePackages = result.IncludedPackages;
                s_SnapshotUnityVersion = Application.unityVersion ?? string.Empty;
                s_SnapshotArtifactDependencyVersion = AssetDatabase.GlobalArtifactDependencyVersion;
                s_SnapshotGeneration = result.JournalGeneration;
                s_LoadedPersistentSnapshot = false;
                s_NeedsAuthoritativeReconcile = false;
                s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
                Interlocked.Exchange(ref s_Stale, 0);
                var journalState = AssetDatabaseChangeJournal.Load(result.ProjectRoot, result.IncludedPackages);
                if (journalState.Corrupt)
                {
                    if (AssetDatabaseChangeJournal.Reset(result.ProjectRoot, result.IncludedPackages, result.JournalGeneration))
                        journalState = AssetDatabaseChangeJournal.Load(result.ProjectRoot, result.IncludedPackages);
                }
                Interlocked.Exchange(ref s_JournalGeneration, journalState.LastGeneration);
                Interlocked.Exchange(ref s_JournalBaseGeneration, journalState.BaseGeneration);
                Interlocked.Exchange(ref s_SnapshotGeneration, result.JournalGeneration);
                Interlocked.Exchange(ref s_LastJournalRecordsApplied, 0);
                s_JournalCorrupt = journalState.Corrupt;
                s_NeedsAuthoritativeReconcile = journalState.Corrupt;
                s_ForceRebuild = false;
                s_LastStateAction = "FullRebuildPublished";
                if (result.Errors != 0 || result.InvalidMetaFiles != 0)
                    s_LastReconcileReason = "BuildReportedErrors";
                else if (s_LastReconcileReason == "None" || s_LastReconcileReason == "ExplicitRequest")
                    s_LastReconcileReason = "None";
                Interlocked.Increment(ref s_Generation);
                Interlocked.Exchange(ref s_LastBuildElapsedMs, (long)Math.Round(result.ElapsedMilliseconds));
                Interlocked.Exchange(ref s_LastBuildIndexedAssetCount, result.IndexedAssetCount);
                Interlocked.Exchange(ref s_LastBuildErrors, result.Errors);
                Interlocked.Exchange(ref s_LastBuildInvalidMetaFiles, result.InvalidMetaFiles);
                Interlocked.Exchange(ref s_PersistentSnapshotBuildErrors, result.Errors);
                Interlocked.Exchange(ref s_PersistentSnapshotInvalidMetaFiles, result.InvalidMetaFiles);
                Volatile.Write(ref s_Initialized, 1);
            }

            RequestPersistentSave();
            return result;
        }

        public static void MarkStale()
        {
            if (IsInitialized)
                Volatile.Write(ref s_Stale, 1);
        }

        public static void QueueAssetChanges(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload = false)
        {
            var imported = ClonePaths(importedAssets);
            var deleted = ClonePaths(deletedAssets);
            var moved = ClonePaths(movedAssets);
            var movedFrom = ClonePaths(movedFromAssetPaths);
            if (imported.Length == 0 && deleted.Length == 0 && moved.Length == 0 && movedFrom.Length == 0)
            {
                if (didDomainReload) s_DomainReloadObserved = true;
                return;
            }

            string journalRoot = s_ProjectRoot;
            if (string.IsNullOrWhiteSpace(journalRoot))
                journalRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            bool journalIncludesPackages = IsInitialized ? s_IncludePackages : true;
            long generation;
            bool journaled = AssetDatabaseChangeJournal.Append(journalRoot, journalIncludesPackages, imported, deleted, moved, movedFrom, didDomainReload, out generation);
            if (!journaled)
            {
                s_JournalCorrupt = true;
                s_NeedsAuthoritativeReconcile = true;
            }
            else
            {
                s_JournalGeneration = Math.Max(s_JournalGeneration, generation);
            }
            if (didDomainReload) s_DomainReloadObserved = true;

            // Keep the AssetPostprocessor callback free of Unity or analyzer work. The scanner
            // applies queued deltas later at a safe synchronization point.
            s_PendingChanges.Enqueue(new PendingAssetChanges(generation, imported, deleted, moved, movedFrom));
        }

        public static bool ApplyPendingChanges()
        {
            if (!IsInitialized) return false;
            if (s_PendingChanges.IsEmpty) return IsReady;

            lock (PublishLock)
            {
                if (s_PendingChanges.IsEmpty) return IsReady;
                if (!IsReady) return false;

                var pathToGuid = new Dictionary<string, string>(s_PathToGuid, PathComparer);
                var pathToGuidValue = new Dictionary<string, GUID>(s_PathToGuidValue);
                var guidToPath = new Dictionary<string, string>(s_GuidToPath, GuidComparer);
                var guidValueToPath = new Dictionary<GUID, string>(s_GuidValueToPath);
                var fileMetadata = new Dictionary<string, AssetMetadataRecord>(s_FileMetadata, PathComparer);
                bool rebuildFolders = false;
                bool failed = false;
                long appliedGeneration = s_SnapshotGeneration;

                while (s_PendingChanges.TryDequeue(out var batch))
                {
                    long batchGeneration = batch.Generation;
                    if (batchGeneration <= 0 && batch.HasChanges)
                    {
                        // If the durable journal was unavailable when the AssetPostprocessor fired,
                        // keep the change safe across a crash by re-journaling it after the next
                        // authoritative rebuild has established a healthy journal.
                        if (!AssetDatabaseChangeJournal.Append(
                                s_ProjectRoot,
                                s_IncludePackages,
                                batch.Imported,
                                batch.Deleted,
                                batch.Moved,
                                batch.MovedFrom,
                                false,
                                out batchGeneration) || batchGeneration <= 0)
                        {
                            failed = true;
                            continue;
                        }
                        s_JournalGeneration = Math.Max(s_JournalGeneration, batchGeneration);
                    }

                    // A freshly published full build already includes every journal record up to its
                    // captured generation. Such runtime records must be consumed but not replayed.
                    if (batchGeneration > 0 && batchGeneration <= appliedGeneration) continue;
                    rebuildFolders |= ApplyDeleted(batch.Deleted, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata);
                    rebuildFolders |= ApplyMovedFrom(batch.MovedFrom, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata);
                    rebuildFolders |= ApplyImported(batch.Imported, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata, ref failed);
                    rebuildFolders |= ApplyImported(batch.Moved, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata, ref failed);
                    appliedGeneration = Math.Max(appliedGeneration, batchGeneration);
                }

                if (failed)
                {
                    Volatile.Write(ref s_Stale, 1);
                    s_NeedsAuthoritativeReconcile = true;
                    s_PersistedSnapshotArtifactDependencyVersion = uint.MaxValue;
                    return false;
                }

                List<string> folderPaths;
                HashSet<string> folderSet;
                Dictionary<string, string[]> folderToSubFolders;
                if (rebuildFolders)
                {
                    var rebuilt = BuildFolderIndex(s_ProjectRoot, s_IncludePackages);
                    folderPaths = rebuilt.folderPaths;
                    folderSet = rebuilt.folderSet;
                    folderToSubFolders = rebuilt.folderToSubFolders;
                }
                else
                {
                    folderPaths = new List<string>(s_FolderPaths);
                    folderSet = new HashSet<string>(s_FolderPathSet, PathComparer);
                    folderToSubFolders = CloneFolderIndex(s_FolderToSubFolders);
                }

                s_PathToGuid = pathToGuid;
                s_PathToGuidValue = pathToGuidValue;
                s_GuidToPath = guidToPath;
                s_GuidValueToPath = guidValueToPath;
                s_FileMetadata = fileMetadata;
                s_FolderPaths = folderPaths;
                s_FolderPathSet = folderSet;
                s_FolderToSubFolders = folderToSubFolders;
                s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
                Interlocked.Exchange(ref s_Stale, 0);
                Interlocked.Increment(ref s_Generation);
                Interlocked.Exchange(ref s_LastBuildIndexedAssetCount, CountAssetPaths(s_PathToGuid, s_FolderPathSet));
                Interlocked.Exchange(ref s_LastBuildErrors, 0);
                Interlocked.Exchange(ref s_LastBuildInvalidMetaFiles, 0);
                s_SnapshotArtifactDependencyVersion = AssetDatabase.GlobalArtifactDependencyVersion;
                Interlocked.Exchange(ref s_SnapshotGeneration, appliedGeneration);
                s_LoadedPersistentSnapshot = false;
                s_PersistedSnapshotArtifactDependencyVersion = s_SnapshotArtifactDependencyVersion;
                Interlocked.Exchange(ref s_PersistentSnapshotBuildErrors, 0);
                Interlocked.Exchange(ref s_PersistentSnapshotInvalidMetaFiles, 0);
                if (!s_NeedsAuthoritativeReconcile && s_LastReconcileReason != "ArtifactSafetySignalOnly")
                    s_LastReconcileReason = "None";
                s_LastStateAction = s_LastStateAction == "None" ? "IncrementalApply" : s_LastStateAction;
            }

            RequestPersistentSave();
            return true;
        }

        static string[] ClonePaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return Array.Empty<string>();
            var clone = new string[paths.Length];
            Array.Copy(paths, clone, paths.Length);
            return clone;
        }

        static bool ApplyDeleted(string[] paths, Dictionary<string, string> pathToGuid, Dictionary<string, GUID> pathToGuidValue,
            Dictionary<string, string> guidToPath, Dictionary<GUID, string> guidValueToPath, Dictionary<string, AssetMetadataRecord> fileMetadata)
        {
            bool folders = false;
            if (paths == null) return false;
            for (int i = 0; i < paths.Length; i++)
            {
                var p = NormalizeAssetPath(paths[i]);
                if (string.IsNullOrEmpty(p)) continue;
                if (IsPackagePath(p) && !s_IncludePackages) continue;
                bool wasAsset = pathToGuid.TryGetValue(p, out var guid);
                if (wasAsset)
                {
                    pathToGuid.Remove(p);
                    pathToGuidValue.Remove(p);
                    if (guidToPath.TryGetValue(guid, out var gp) && string.Equals(gp, p, StringComparison.OrdinalIgnoreCase))
                    {
                        guidToPath.Remove(guid);
                        guidValueToPath.Remove(new GUID(guid));
                    }
                    fileMetadata.Remove(p);
                }
                if (!wasAsset && (s_FolderPathSet.Contains(p) || !Path.HasExtension(p))) folders = true;
            }
            return folders;
        }

        static bool ApplyMovedFrom(string[] paths, Dictionary<string, string> pathToGuid, Dictionary<string, GUID> pathToGuidValue,
            Dictionary<string, string> guidToPath, Dictionary<GUID, string> guidValueToPath, Dictionary<string, AssetMetadataRecord> fileMetadata)
            => ApplyDeleted(paths, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata);

        static bool ApplyImported(string[] paths, Dictionary<string, string> pathToGuid, Dictionary<string, GUID> pathToGuidValue,
            Dictionary<string, string> guidToPath, Dictionary<GUID, string> guidValueToPath, Dictionary<string, AssetMetadataRecord> fileMetadata, ref bool failed)
        {
            bool folders = false;
            if (paths == null) return false;
            for (int i = 0; i < paths.Length; i++)
            {
                var p = NormalizeAssetPath(paths[i]);
                if (string.IsNullOrEmpty(p)) continue;
                if (IsPackagePath(p) && !s_IncludePackages) continue;
                string full = Path.Combine(s_ProjectRoot, p.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(full)) { folders = true; continue; }
                string meta = full + MetaExtension;
                if (!File.Exists(meta)) { failed = true; continue; }
                if (!TryReadGuidFromMetaFile(meta, out var guid) || string.IsNullOrEmpty(guid)) { failed = true; continue; }
                var gv = new GUID(guid);
                if (guidToPath.TryGetValue(guid, out var oldPath) && !string.Equals(oldPath, p, StringComparison.OrdinalIgnoreCase))
                {
                    // A duplicate GUID cannot be safely resolved incrementally. Force an authoritative rebuild.
                    failed = true;
                    continue;
                }
                long size = 0, last = 0, metaLast = 0;
                try { var info = new FileInfo(full); if (info.Exists) { size = info.Length; last = info.LastWriteTimeUtc.Ticks; } metaLast = File.GetLastWriteTimeUtc(meta).Ticks; } catch { }
                pathToGuid[p] = guid; pathToGuidValue[p] = gv; guidToPath[guid] = p; guidValueToPath[gv] = p;
                fileMetadata[p] = new AssetMetadataRecord(p, guid, p + MetaExtension, size, last, metaLast);
            }
            return folders;
        }

        static bool IsPackagePath(string path) => !string.IsNullOrEmpty(path) && (path.Equals("Packages", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));

        static (List<string> folderPaths, HashSet<string> folderSet, Dictionary<string, string[]> folderToSubFolders) BuildFolderIndex(string projectRoot, bool includePackages)
        {
            var paths = new List<string>(4096); var set = new HashSet<string>(PathComparer); var children = new Dictionary<string, List<string>>(PathComparer);
            var roots = includePackages ? new[] { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages") } : new[] { Path.Combine(projectRoot, "Assets") };
            foreach (var root in roots) if (Directory.Exists(root))
            {
                var stack = new Stack<string>(); stack.Push(root);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop(); var ap = ToAssetPath(dir, projectRoot);
                    if (!string.Equals(ap, "Packages", StringComparison.OrdinalIgnoreCase)) { paths.Add(ap); set.Add(ap); if (!children.ContainsKey(ap)) children.Add(ap, null); }
                    try { foreach (var child in Directory.EnumerateDirectories(dir)) { var cap = ToAssetPath(child, projectRoot); if (!string.Equals(ap, "Packages", StringComparison.OrdinalIgnoreCase) && !string.Equals(cap, "Packages", StringComparison.OrdinalIgnoreCase)) { if (!children.TryGetValue(ap, out var list) || list == null) children[ap] = list = new List<string>(4); list.Add(cap); } stack.Push(child); } } catch { }
                }
            }
            var result = new Dictionary<string, string[]>(PathComparer); foreach (var kv in children) result[kv.Key] = kv.Value == null ? Array.Empty<string>() : kv.Value.ToArray();
            return (paths, set, result);
        }

        static Dictionary<string, string[]> CloneFolderIndex(Dictionary<string, string[]> source)
        {
            var result = new Dictionary<string, string[]>(source.Count, PathComparer); foreach (var pair in source) result[pair.Key] = pair.Value == null ? Array.Empty<string>() : (string[])pair.Value.Clone(); return result;
        }

        static void LoadJournalAndReplay(string fullRoot, bool includePackages)
        {
            var state = AssetDatabaseChangeJournal.Load(fullRoot, includePackages);
            Interlocked.Exchange(ref s_JournalGeneration, state.LastGeneration);
            Interlocked.Exchange(ref s_JournalBaseGeneration, state.BaseGeneration);
            s_JournalCorrupt = state.Corrupt;
            if (!state.Compatible || state.Corrupt)
            {
                s_LastStateAction = "JournalInvalid";
                s_LastReconcileReason = state.Corrupt ? "JournalCorrupt" : "JournalIncompatible";
                s_NeedsAuthoritativeReconcile = true;
                return;
            }

            if (!IsReady)
            {
                if (state.LastGeneration > state.BaseGeneration || state.BaseGeneration > 0)
                {
                    s_LastStateAction = "JournalAwaitingRebuild";
                    s_LastReconcileReason = "SnapshotUnavailableForJournalReplay";
                    s_NeedsAuthoritativeReconcile = true;
                }
                return;
            }

            long snapshotGeneration = SnapshotGeneration;
            if (snapshotGeneration < state.BaseGeneration || snapshotGeneration > state.LastGeneration)
            {
                s_LastStateAction = "JournalDiscontinuous";
                s_LastReconcileReason = "JournalContinuityBroken";
                s_NeedsAuthoritativeReconcile = true;
                return;
            }

            var records = new List<AssetDatabaseChangeJournal.Record>();
            if (!AssetDatabaseChangeJournal.TryReadPending(fullRoot, includePackages, snapshotGeneration, records))
            {
                s_LastStateAction = "JournalReadFailed";
                s_LastReconcileReason = "JournalReadFailed";
                s_JournalCorrupt = true;
                s_NeedsAuthoritativeReconcile = true;
                return;
            }

            long expected = snapshotGeneration;
            int replayed = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.Generation != ++expected)
                {
                    s_LastStateAction = "JournalDiscontinuous";
                    s_LastReconcileReason = "JournalGenerationGap";
                    s_NeedsAuthoritativeReconcile = true;
                    return;
                }
                if (record.DidDomainReload) s_DomainReloadObserved = true;
                s_PendingChanges.Enqueue(new PendingAssetChanges(record.Generation, record.Imported, record.Deleted, record.Moved, record.MovedFrom));
                replayed++;
            }
            Interlocked.Exchange(ref s_LastJournalRecordsApplied, replayed);
            s_LastStateAction = replayed > 0 ? "PersistentLoadAndJournalReplay" : (s_LoadedPersistentSnapshot ? "PersistentLoad" : "JournalChecked");

            // GlobalArtifactDependencyVersion is deliberately not a snapshot invalidation trigger.
            // Unity documents it as a broad artifact-validity signal, while this snapshot stores only
            // project identity / filesystem metadata. Asset-path changes are causally tracked by the journal;
            // imported-object facts use their own dependency hashes. A version mismatch remains visible in
            // diagnostics but does not turn an unchanged project-state snapshot into an O(N) rebuild.
            if (ArtifactVersionMismatchForLoadedSnapshot() && replayed == 0 && snapshotGeneration == state.LastGeneration)
                s_LastReconcileReason = "ArtifactSafetySignalOnly";
        }

        static bool ArtifactVersionMismatchForLoadedSnapshot()
        {
            return s_LoadedPersistentSnapshot && s_SnapshotArtifactDependencyVersion != AssetDatabase.GlobalArtifactDependencyVersion;
        }

        /// <summary>Copies durable asset-change records after a known journal generation.</summary>
        public static bool TryGetJournalChangesAfter(long afterGeneration, List<AssetChangeRecord> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            string root = s_ProjectRoot;
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            destination.Clear();
            var records = new List<AssetDatabaseChangeJournal.Record>();
            if (!AssetDatabaseChangeJournal.TryReadPending(root, s_IncludePackages, afterGeneration, records))
                return false;

            long expected = afterGeneration;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Generation != ++expected)
                {
                    destination.Clear();
                    return false;
                }
                destination.Add(new AssetChangeRecord(records[i]));
            }
            return true;
        }

        /// <summary>Requests an authoritative filesystem reconciliation on the next scan.</summary>
        public static void RequestAuthoritativeReconcile()
        {
            s_LastStateAction = "ExplicitAuthoritativeReconcile";
            s_LastReconcileReason = "ExplicitRequest";
            s_NeedsAuthoritativeReconcile = true;
            MarkStale();
        }

        /// <summary>Forces a complete project-state rebuild and discards the persistent snapshot.</summary>
        public static void RequestFullRebuild()
        {
            s_LastStateAction = "ExplicitFullRebuild";
            s_LastReconcileReason = "ExplicitRequest";
            s_ForceRebuild = true;
            s_NeedsAuthoritativeReconcile = false;
            MarkStale();
            TryDeletePersistentSnapshot();
        }

        /// <summary>
        /// Reconciles the managed project snapshot against Unity's authoritative asset-path universe.
        /// This is deliberately a one-time/recovery operation: ordinary warm scans never call it.
        /// The snapshot builder is filesystem-based, so this boundary makes the first post-build snapshot
        /// semantically match what Unity itself considers an asset before it becomes the canonical scan universe.
        /// </summary>
        public static bool ReconcileAuthoritativeAssetPaths(IReadOnlyList<string> authoritativePaths)
        {
            if (!IsInitialized || authoritativePaths == null || s_ForceRebuild || s_JournalCorrupt) return false;
            if (LastBuildErrors != 0 || LastBuildInvalidMetaFiles != 0) return false;

            lock (PublishLock)
            {
                // AssetDatabase.GetAllAssetPaths is the authoritative scan universe, but package files can
                // legitimately exist without source .meta files in the project checkout. Canonicalize the
                // editable Assets universe here; package/reference semantics remain covered by the existing
                // package-inclusive snapshot and reference graph.
                var authoritative = new HashSet<string>(PathComparer);
                for (int i = 0; i < authoritativePaths.Count; i++)
                {
                    string path = NormalizeAssetPath(authoritativePaths[i]);
                    if (string.IsNullOrEmpty(path) || path.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase)) continue;
                    bool underAssets = path.Equals("Assets", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                    if (!underAssets || s_FolderPathSet.Contains(path)) continue;
                    authoritative.Add(path);
                }

                var pathToGuid = new Dictionary<string, string>(s_PathToGuid, PathComparer);
                var pathToGuidValue = new Dictionary<string, GUID>(s_PathToGuidValue);
                var guidToPath = new Dictionary<string, string>(s_GuidToPath, GuidComparer);
                var guidValueToPath = new Dictionary<GUID, string>(s_GuidValueToPath);
                var fileMetadata = new Dictionary<string, AssetMetadataRecord>(s_FileMetadata, PathComparer);
                int added = 0;
                int removed = 0;

                var removals = new List<string>();
                foreach (var path in pathToGuid.Keys)
                {
                    bool underAssets = path.Equals("Assets", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                    if (underAssets && !s_FolderPathSet.Contains(path) && !authoritative.Contains(path)) removals.Add(path);
                }

                for (int i = 0; i < removals.Count; i++)
                {
                    string path = removals[i];
                    if (!pathToGuid.TryGetValue(path, out var guid)) continue;
                    pathToGuid.Remove(path);
                    pathToGuidValue.Remove(path);
                    if (guidToPath.TryGetValue(guid, out var mappedPath) && string.Equals(mappedPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        guidToPath.Remove(guid);
                        guidValueToPath.Remove(new GUID(guid));
                    }
                    fileMetadata.Remove(path);
                    removed++;
                }

                foreach (var path in authoritative)
                {
                    if (pathToGuid.ContainsKey(path)) continue;

                    string absolutePath = ToAbsolutePath(path);
                    string metaPath = absolutePath + MetaExtension;
                    if (!File.Exists(metaPath) || !TryReadGuidFromMetaFile(metaPath, out var guid) || string.IsNullOrEmpty(guid))
                    {
                        s_LastStateAction = "AuthoritativeCanonicalizationFailed";
                        s_LastReconcileReason = "AuthoritativePathNotMaterializable";
                        s_NeedsAuthoritativeReconcile = true;
                        Volatile.Write(ref s_Stale, 1);
                        return false;
                    }

                    if (guidToPath.TryGetValue(guid, out var existingPath) && !string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        s_LastStateAction = "AuthoritativeCanonicalizationFailed";
                        s_LastReconcileReason = "DuplicateGuidDuringCanonicalization";
                        s_NeedsAuthoritativeReconcile = true;
                        Volatile.Write(ref s_Stale, 1);
                        return false;
                    }

                    var gv = new GUID(guid);
                    long size = 0, last = 0, metaLast = 0;
                    try
                    {
                        var info = new FileInfo(absolutePath);
                        if (info.Exists) { size = info.Length; last = info.LastWriteTimeUtc.Ticks; }
                        metaLast = File.GetLastWriteTimeUtc(metaPath).Ticks;
                    }
                    catch { }

                    pathToGuid[path] = guid;
                    pathToGuidValue[path] = gv;
                    guidToPath[guid] = path;
                    guidValueToPath[gv] = path;
                    fileMetadata[path] = new AssetMetadataRecord(path, guid, path + MetaExtension, size, last, metaLast);
                    added++;
                }

                s_LastAuthoritativeAdded = added;
                s_LastAuthoritativeRemoved = removed;

                if (added == 0 && removed == 0)
                {
                    s_LastStateAction = "AuthoritativeValidated";
                    if (s_LastReconcileReason == "None" || s_LastReconcileReason == "ArtifactSafetySignalOnly")
                        s_LastReconcileReason = "None";
                    return true;
                }

                s_PathToGuid = pathToGuid;
                s_PathToGuidValue = pathToGuidValue;
                s_GuidToPath = guidToPath;
                s_GuidValueToPath = guidValueToPath;
                s_FileMetadata = fileMetadata;
                s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
                Interlocked.Exchange(ref s_LastBuildIndexedAssetCount, CountAssetPaths(s_PathToGuid, s_FolderPathSet));
                s_SnapshotArtifactDependencyVersion = AssetDatabase.GlobalArtifactDependencyVersion;
                s_PersistedSnapshotArtifactDependencyVersion = s_SnapshotArtifactDependencyVersion;
                Interlocked.Exchange(ref s_LastBuildErrors, 0);
                Interlocked.Exchange(ref s_LastBuildInvalidMetaFiles, 0);
                Interlocked.Increment(ref s_Generation);
                Volatile.Write(ref s_Stale, 0);
                s_NeedsAuthoritativeReconcile = false;
                s_ForceRebuild = false;
                s_LoadedPersistentSnapshot = false;
                s_LastStateAction = "AuthoritativeCanonicalization";
                s_LastReconcileReason = "AuthoritativePathSetMismatch";
            }

            RequestPersistentSave();
            return true;
        }

        /// <summary>Concise diagnostics for benchmark reports and the Project Optimizer troubleshooting UI.</summary>
        public static string ProjectStateDiagnostics()
        {
            var sb = new StringBuilder(512);
            sb.Append("ready=").Append(IsReady)
              .Append(" complete=").Append(IsComplete)
              .Append(" indexedAssets=").Append(IndexedAssetCount)
              .Append(" indexedFolders=").Append(IndexedFolderCount)
              .Append(" snapshotGeneration=").Append(SnapshotGeneration)
              .Append(" persistedGeneration=").Append(PersistedSnapshotGeneration)
              .Append(" journalBase=").Append(JournalBaseGeneration)
              .Append(" journalGeneration=").Append(JournalGeneration)
              .Append(" journalHealthy=").Append(JournalHealthy)
              .Append(" loadedPersistent=").Append(LoadedPersistentSnapshot)
              .Append(" persistentSnapshotUsed=").Append(s_PersistentSnapshotUsedThisInitialization)
              .Append(" artifactMismatch=").Append(ArtifactDependencyVersionMismatch)
              .Append(" needsReconcile=").Append(NeedsAuthoritativeReconcile)
              .Append(" journalRecordsApplied=").Append(LastJournalRecordsApplied)
              .Append(" domainReloadObserved=").Append(DomainReloadObserved)
              .Append(" authoritativeAdded=").Append(s_LastAuthoritativeAdded)
              .Append(" authoritativeRemoved=").Append(s_LastAuthoritativeRemoved)
              .Append(" lastAction=").Append(LastStateAction)
              .Append(" reconcileReason=").Append(LastReconcileReason);
            return sb.ToString();
        }

        static void TryLoadPersistentSnapshot(string fullRoot, bool includePackages)
        {
            lock (PublishLock)
            {
                if (IsReady && string.Equals(s_ProjectRoot, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    if (!File.Exists(SnapshotPath)) return;
                    using var reader = new BinaryReader(File.Open(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read));
                    if (reader.ReadInt32() != SnapshotVersion) return;
                    if (!string.Equals(reader.ReadString(), fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                    var snapshotUnityVersion = reader.ReadString();
                    if (!string.Equals(snapshotUnityVersion, Application.unityVersion, StringComparison.Ordinal)) return;
                    uint snapshotArtifactDependencyVersion = reader.ReadUInt32();
                    bool snapshotIncludesPackages = reader.ReadBoolean();
                    if (snapshotIncludesPackages != includePackages) return;
                    long snapshotGeneration = reader.ReadInt64();
                    if (snapshotGeneration < 0) return;

                    int pathGuidCount = reader.ReadInt32();
                    if (pathGuidCount < 0 || pathGuidCount > 10000000) return;
                    var p2g = new Dictionary<string, string>(pathGuidCount, PathComparer);
                    var p2gv = new Dictionary<string, GUID>(pathGuidCount, PathComparer);
                    var g2p = new Dictionary<string, string>(pathGuidCount, GuidComparer);
                    var g2pv = new Dictionary<GUID, string>();
                    for (int i = 0; i < pathGuidCount; i++)
                    {
                        var path = reader.ReadString();
                        var guid = reader.ReadString();
                        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(guid)) continue;
                        var gv = new GUID(guid);
                        p2g[path] = guid;
                        p2gv[path] = gv;
                        if (!g2p.ContainsKey(guid))
                        {
                            g2p[guid] = path;
                            g2pv[gv] = path;
                        }
                    }

                    int metadataCount = reader.ReadInt32();
                    if (metadataCount < 0 || metadataCount > pathGuidCount || metadataCount > 10000000) return;
                    var meta = new Dictionary<string, AssetMetadataRecord>(metadataCount, PathComparer);
                    for (int i = 0; i < metadataCount; i++)
                    {
                        var path = reader.ReadString();
                        var guid = reader.ReadString();
                        var metaPath = reader.ReadString();
                        var size = reader.ReadInt64();
                        var last = reader.ReadInt64();
                        var metaLast = reader.ReadInt64();
                        if (!string.IsNullOrEmpty(path))
                            meta[path] = new AssetMetadataRecord(path, guid, metaPath, size, last, metaLast);
                    }

                    int folderCount = reader.ReadInt32();
                    if (folderCount < 0 || folderCount > 1000000) return;
                    var folders = new List<string>(folderCount);
                    var fset = new HashSet<string>(PathComparer);
                    for (int i = 0; i < folderCount; i++)
                    {
                        var f = reader.ReadString();
                        folders.Add(f);
                        fset.Add(f);
                    }

                    int childMapCount = reader.ReadInt32();
                    if (childMapCount < 0 || childMapCount > 1000000) return;
                    var folderChildren = new Dictionary<string, string[]>(childMapCount, PathComparer);
                    for (int i = 0; i < childMapCount; i++)
                    {
                        var parent = reader.ReadString();
                        int childCount = reader.ReadInt32();
                        if (childCount < 0 || childCount > 100000) return;
                        var children = new string[childCount];
                        for (int c = 0; c < childCount; c++) children[c] = reader.ReadString();
                        folderChildren[parent] = children;
                    }

                    s_ProjectRoot = fullRoot;
                    s_PathToGuid = p2g;
                    s_PathToGuidValue = p2gv;
                    s_GuidToPath = g2p;
                    s_GuidValueToPath = g2pv;
                    s_FileMetadata = meta;
                    s_FolderPaths = folders;
                    s_FolderPathSet = fset;
                    s_FolderToSubFolders = folderChildren;
                    s_LabelsCache = new ConcurrentDictionary<string, string[]>(GuidComparer);
                    s_IncludePackages = snapshotIncludesPackages;
                    s_SnapshotUnityVersion = snapshotUnityVersion;
                    s_SnapshotArtifactDependencyVersion = snapshotArtifactDependencyVersion;
                    s_PersistedSnapshotArtifactDependencyVersion = snapshotArtifactDependencyVersion;
                    Interlocked.Exchange(ref s_SnapshotGeneration, snapshotGeneration);
                    Interlocked.Exchange(ref s_PersistedSnapshotGeneration, snapshotGeneration);
                    s_LoadedPersistentSnapshot = true;
                    s_PersistentSnapshotUsedThisInitialization = true;
                    Interlocked.Exchange(ref s_Stale, 0);
                    Interlocked.Exchange(ref s_Generation, Math.Max(1, s_Generation + 1));
                    Interlocked.Exchange(ref s_LastBuildIndexedAssetCount, CountAssetPaths(p2g, fset));
                    Interlocked.Exchange(ref s_LastBuildErrors, 0);
                    Interlocked.Exchange(ref s_LastBuildInvalidMetaFiles, 0);
                    Interlocked.Exchange(ref s_PersistentSnapshotBuildErrors, 0);
                    Interlocked.Exchange(ref s_PersistentSnapshotInvalidMetaFiles, 0);
                }
                catch { Volatile.Write(ref s_Stale, 1); }
            }
        }

        static void TryDeletePersistentSnapshot()
        {
            try { if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath); }
            catch { }
        }

        static void RequestPersistentSave()
        {
            Interlocked.Exchange(ref s_PersistenceRequested, 1);
            if (Interlocked.CompareExchange(ref s_Persisting, 1, 0) != 0) return;
            _ = Task.Run(() => PersistLoop());
        }

        public static void FlushPersistentSnapshot()
        {
            if (!IsReady) return;
            Interlocked.Exchange(ref s_PersistenceRequested, 0);
            lock (PersistentSaveLock) PersistCurrentSnapshot();
        }

        static void PersistLoop()
        {
            try
            {
                while (Interlocked.Exchange(ref s_PersistenceRequested, 0) != 0)
                {
                    PersistCurrentSnapshot();
                }
            }
            finally { Volatile.Write(ref s_Persisting, 0); if (Volatile.Read(ref s_PersistenceRequested) != 0) RequestPersistentSave(); }
        }

        static void PersistCurrentSnapshot()
        {
            lock (PersistentSaveLock)
            {
                try
                {
                    string directory = Path.GetDirectoryName(SnapshotPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    Dictionary<string, string> pathToGuid; Dictionary<string, AssetMetadataRecord> metadata; List<string> folders; Dictionary<string,string[]> folderChildren; string root; bool includePackages; string unityVersion; uint artifactDependencyVersion; long snapshotGeneration;
                    lock (PublishLock)
                    {
                        if (s_PersistentSnapshotBuildErrors != 0 || s_PersistentSnapshotInvalidMetaFiles != 0) return;
                        root=s_ProjectRoot;
                        pathToGuid=new Dictionary<string,string>(s_PathToGuid,PathComparer);
                        metadata=new Dictionary<string,AssetMetadataRecord>(s_FileMetadata,PathComparer);
                        folders=new List<string>(s_FolderPaths);
                        folderChildren=CloneFolderIndex(s_FolderToSubFolders);
                        includePackages=s_IncludePackages;
                        unityVersion=s_SnapshotUnityVersion ?? string.Empty;
                        artifactDependencyVersion=s_SnapshotArtifactDependencyVersion;
                        snapshotGeneration=s_SnapshotGeneration;
                    }
                    if (string.IsNullOrEmpty(root) || metadata.Count == 0) return;
                    pathToGuid = new Dictionary<string,string>(pathToGuid, PathComparer);
                    var orderedPaths = pathToGuid.Keys.ToList();
                    orderedPaths.Sort(StringComparer.OrdinalIgnoreCase);
                    var orderedMetadata = metadata.Keys.ToList();
                    orderedMetadata.Sort(StringComparer.OrdinalIgnoreCase);
                    folders.Sort(StringComparer.OrdinalIgnoreCase);
                    var orderedParents = folderChildren.Keys.ToList();
                    orderedParents.Sort(StringComparer.OrdinalIgnoreCase);
                    var temp = SnapshotPath + ".tmp." + Guid.NewGuid().ToString("N");
                    using (var writer = new BinaryWriter(File.Open(temp, FileMode.Create, FileAccess.Write, FileShare.None)))
                    {
                        writer.Write(SnapshotVersion); writer.Write(root); writer.Write(unityVersion); writer.Write(artifactDependencyVersion); writer.Write(includePackages); writer.Write(snapshotGeneration);
                        writer.Write(orderedPaths.Count);
                        for (int i=0;i<orderedPaths.Count;i++) { var path=orderedPaths[i]; writer.Write(path??string.Empty); writer.Write(pathToGuid[path]??string.Empty); }
                        writer.Write(orderedMetadata.Count);
                        for (int i=0;i<orderedMetadata.Count;i++) { var r=metadata[orderedMetadata[i]]; writer.Write(r.AssetPath??string.Empty); writer.Write(r.Guid??string.Empty); writer.Write(r.MetaFilePath??string.Empty); writer.Write(r.FileSize); writer.Write(r.LastWriteTicks); writer.Write(r.MetaLastWriteTicks); }
                        writer.Write(folders.Count); for(int i=0;i<folders.Count;i++) writer.Write(folders[i]??string.Empty);
                        writer.Write(orderedParents.Count); for(int i=0;i<orderedParents.Count;i++){ var parent=orderedParents[i]; writer.Write(parent??string.Empty); var children=folderChildren[parent]??Array.Empty<string>(); var sortedChildren=new List<string>(children); sortedChildren.Sort(StringComparer.OrdinalIgnoreCase); writer.Write(sortedChildren.Count); for(int c=0;c<sortedChildren.Count;c++) writer.Write(sortedChildren[c]??string.Empty); }
                    }
                    ReplaceSnapshotFile(temp, SnapshotPath);
                    Interlocked.Exchange(ref s_PersistedSnapshotGeneration, snapshotGeneration);
                    Volatile.Write(ref s_PersistedSnapshotArtifactDependencyVersion, artifactDependencyVersion);
                    AssetDatabaseChangeJournal.Compact(root, includePackages, snapshotGeneration);
                }
                catch { }
            }
        }

        static void ReplaceSnapshotFile(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                try { File.Replace(temporaryPath, destinationPath, null); return; }
                catch (IOException) { try { File.Delete(destinationPath); } catch { } }
            }
            File.Move(temporaryPath, destinationPath);
        }

        /// <summary>
        /// Verifies the persistent/indexed Assets path set against Unity's authoritative GetAllAssetPaths result.
        /// This is intentionally O(n) managed dictionary work and performs no per-asset AssetDatabase call.
        /// </summary>
        public static bool ValidateAuthoritativeAssetPaths(IReadOnlyList<string> authoritativePaths)
        {
            if (!IsReady || authoritativePaths == null) return false;
            int expectedAssets = 0;
            int indexedAssets = 0;
            lock (PublishLock)
            {
                for (int i = 0; i < authoritativePaths.Count; i++)
                {
                    string path = NormalizeAssetPath(authoritativePaths[i]);
                    if (string.IsNullOrEmpty(path)) continue;
                    bool assetsPath = path.Equals("Assets", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                    bool packagesPath = path.Equals("Packages", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
                    if (!assetsPath && !packagesPath) continue;
                    if (s_FolderPathSet.Contains(path)) continue;
                    if (packagesPath && !s_IncludePackages) continue;
                    expectedAssets++;
                    if (!s_PathToGuid.ContainsKey(path)) return false;
                }

                foreach (var path in s_PathToGuid.Keys)
                {
                    if (s_FolderPathSet.Contains(path)) continue;
                    bool assetsPath = path.Equals("Assets", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
                    bool packagesPath = path.Equals("Packages", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
                    if ((assetsPath || packagesPath) && (!packagesPath || s_IncludePackages)) indexedAssets++;
                }
            }
            return expectedAssets == indexedAssets;
        }

        public static string AssetPathToGUID(
            string path,
            ThreadSafeAssetPathToGUIDOptions options = ThreadSafeAssetPathToGUIDOptions.IncludeRecentlyDeletedAssets)
        {
            var normalized = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(normalized))
                return string.Empty;

            if (!TryGetPathGuid(normalized, out var guid))
                return string.Empty;

            if (options == ThreadSafeAssetPathToGUIDOptions.OnlyExistingAssets && !SourcePathExists(normalized))
                return string.Empty;

            return guid;
        }

        public static string GUIDToAssetPath(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return string.Empty;

            var normalizedGuid = guid.Trim();
            return s_GuidToPath.TryGetValue(normalizedGuid, out var path) ? path : string.Empty;
        }

        public static string GUIDToAssetPath(GUID guid)
        {
            return s_GuidValueToPath.TryGetValue(guid, out var path) ? path : string.Empty;
        }

        public static GUID GUIDFromAssetPath(string path)
        {
            var normalized = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(normalized) || !s_PathToGuidValue.TryGetValue(normalized, out var guid))
                return new GUID();

            return guid;
        }

        public static string GetTextMetaFilePathFromAssetPath(string path)
        {
            var normalized = NormalizeAssetPath(path);
            return string.IsNullOrEmpty(normalized) ? string.Empty : normalized + MetaExtension;
        }

        public static string GetAssetPathFromTextMetaFilePath(string path)
        {
            var normalized = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(normalized) || !normalized.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase))
                return normalized;

            return normalized.Substring(0, normalized.Length - MetaExtension.Length);
        }

        public static bool AssetPathExists(string path)
        {
            var normalized = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(normalized))
                return false;

            return s_PathToGuid.ContainsKey(normalized) || s_FolderPathSet.Contains(normalized);
        }

        public static bool IsValidFolder(string path)
        {
            var normalized = NormalizeAssetPath(path);
            return !string.IsNullOrEmpty(normalized) && s_FolderPathSet.Contains(normalized);
        }

        public static string[] GetSubFolders(string path)
        {
            var normalized = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(normalized) || !s_FolderPathSet.Contains(normalized))
                return Array.Empty<string>();

            return s_FolderToSubFolders.TryGetValue(normalized, out var children) && children.Length > 0
                ? (string[])children.Clone()
                : Array.Empty<string>();
        }

        public static string[] GetLabels(GUID guid)
        {
            return GetLabels(guid.ToString());
        }

        public static string[] GetLabels(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return Array.Empty<string>();

            var normalizedGuid = guid.Trim();
            if (s_LabelsCache.TryGetValue(normalizedGuid, out var cachedLabels))
                return cachedLabels;

            var assetPath = GUIDToAssetPath(normalizedGuid);
            if (string.IsNullOrEmpty(assetPath))
                return Array.Empty<string>();

            var labels = ReadLabelsFromMetaFile(ToAbsolutePath(GetTextMetaFilePathFromAssetPath(assetPath)));
            return s_LabelsCache.GetOrAdd(normalizedGuid, labels);
        }

        public static string[] GetAllAssetPaths()
        {
            if (s_PathToGuid.Count == 0) return Array.Empty<string>();
            var result = new List<string>(Math.Max(0, CountAssetPaths(s_PathToGuid, s_FolderPathSet)));
            foreach (var path in s_PathToGuid.Keys)
                if (!s_FolderPathSet.Contains(path)) result.Add(path);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result.ToArray();
        }

        public static IEnumerable<string> EnumerateIndexedAssetPaths()
        {
            foreach (var path in s_PathToGuid.Keys)
                if (!s_FolderPathSet.Contains(path)) yield return path;
        }

        public static string[] GetIndexedFolderPaths()
        {
            return s_FolderPaths.ToArray();
        }

        public static IEnumerable<string> EnumerateIndexedFolderPaths()
        {
            return s_FolderPaths;
        }

        public static bool TryGetAssetMetadata(string assetPath, out AssetMetadataRecord record)
        {
            var normalized = NormalizeAssetPath(assetPath);
            if (!string.IsNullOrEmpty(normalized) && s_FileMetadata.TryGetValue(normalized, out record))
                return true;

            if (TryGetPathGuid(normalized, out var guid))
            {
                record = new AssetMetadataRecord(normalized, guid, GetTextMetaFilePathFromAssetPath(normalized));
                return true;
            }

            record = default;
            return false;
        }

        private static bool SourcePathExists(string normalizedPath)
        {
            var absolutePath = ToAbsolutePath(normalizedPath);
            return File.Exists(absolutePath) || Directory.Exists(absolutePath);
        }

        private static bool TryGetPathGuid(string normalizedPath, out string guid)
        {
            return s_PathToGuid.TryGetValue(normalizedPath, out guid);
        }

        private static int CountAssetPaths(Dictionary<string, string> pathToGuid, HashSet<string> folderPathSet)
        {
            if (pathToGuid == null || pathToGuid.Count == 0) return 0;
            int count = 0;
            foreach (var path in pathToGuid.Keys)
                if (folderPathSet == null || !folderPathSet.Contains(path)) count++;
            return count;
        }

        private static AssetDatabaseThreadSafeBuildResult BuildIndex(
            string projectRoot,
            bool includePackages,
            AssetDatabaseThreadSafeBuildProgress progress,
            CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            var fullRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            long journalGeneration = AssetDatabaseChangeJournal.Load(fullRoot, includePackages).LastGeneration;
            var pathToGuid = new Dictionary<string, string>(PathComparer);
            var pathToGuidValue = new Dictionary<string, GUID>(PathComparer);
            var guidToPath = new Dictionary<string, string>(GuidComparer);
            var guidValueToPath = new Dictionary<GUID, string>();
            var folderPaths = new List<string>(4096);
            var folderPathSet = new HashSet<string>(PathComparer);
            var folderChildrenBuilders = new Dictionary<string, List<string>>(PathComparer);
            long duplicateGuids = 0;
            long metaFilesSeen = 0;
            long metaFilesParsed = 0;
            long invalidMetaFiles = 0;
            long directoriesScanned = 0;
            long errors = 0;
            var duplicateGuidRecords = new List<DuplicateGuidRecord>();
            var invalidMetaFilePaths = new List<string>();
            var errorDirectories = new List<string>();
            var fileMetadata = new Dictionary<string, AssetMetadataRecord>(PathComparer);

            var roots = new List<string>(2)
            {
                Path.Combine(fullRoot, "Assets")
            };

            if (includePackages)
                roots.Add(Path.Combine(fullRoot, "Packages"));

            var metaBatch = new List<string>(MetaBatchSize);
            var indexBuildWorkerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, IndexBuildWorkerLimit));

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                    continue;

                var directoryStack = new Stack<string>();
                directoryStack.Push(root);

                while (directoryStack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var directory = directoryStack.Pop();
                    progress?.IncrementDirectoriesScanned();
                    directoriesScanned++;
                    if (progress != null)
                        progress.CurrentDirectory = ToAssetPath(directory, fullRoot);

                    var assetDirectoryPath = ToAssetPath(directory, fullRoot);
                    var isPackagesRoot = string.Equals(assetDirectoryPath, "Packages", StringComparison.OrdinalIgnoreCase);
                    if (!isPackagesRoot)
                    {
                        folderPaths.Add(assetDirectoryPath);
                        folderPathSet.Add(assetDirectoryPath);
                        if (!folderChildrenBuilders.ContainsKey(assetDirectoryPath))
                            folderChildrenBuilders.Add(assetDirectoryPath, null);
                    }

                    try
                    {
                        foreach (var metaFile in Directory.EnumerateFiles(directory, "*.meta", SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            metaBatch.Add(metaFile);
                            progress?.IncrementMetaFilesSeen();
                            metaFilesSeen++;
                            if (metaBatch.Count >= MetaBatchSize)
                            {
                                ProcessMetaBatch(metaBatch, fullRoot, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata, ref metaFilesParsed, ref invalidMetaFiles, ref duplicateGuids, duplicateGuidRecords, invalidMetaFilePaths, progress, indexBuildWorkerCount, cancellationToken);
                                metaBatch.Clear();
                            }
                        }
                    }
                    catch (IOException)
                    {
                        errors++;
                        if (errorDirectories.Count < DiagnosticListLimit)
                            errorDirectories.Add(directory);
                        progress?.IncrementErrors();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        errors++;
                        if (errorDirectories.Count < DiagnosticListLimit)
                            errorDirectories.Add(directory);
                        progress?.IncrementErrors();
                    }

                    try
                    {
                        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                        {
                            var childAssetPath = ToAssetPath(child, fullRoot);
                            var childIsPackagesRoot = string.Equals(childAssetPath, "Packages", StringComparison.OrdinalIgnoreCase);
                            if (!isPackagesRoot && !childIsPackagesRoot)
                            {
                                if (!folderChildrenBuilders.TryGetValue(assetDirectoryPath, out var children) || children == null)
                                {
                                    children = new List<string>(4);
                                    folderChildrenBuilders[assetDirectoryPath] = children;
                                }

                                children.Add(childAssetPath);
                            }

                            directoryStack.Push(child);
                        }
                    }
                    catch (IOException)
                    {
                        errors++;
                        if (errorDirectories.Count < DiagnosticListLimit)
                            errorDirectories.Add(directory);
                        progress?.IncrementErrors();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        errors++;
                        if (errorDirectories.Count < DiagnosticListLimit)
                            errorDirectories.Add(directory);
                        progress?.IncrementErrors();
                    }
                }
            }

            if (metaBatch.Count > 0)
                ProcessMetaBatch(metaBatch, fullRoot, pathToGuid, pathToGuidValue, guidToPath, guidValueToPath, fileMetadata, ref metaFilesParsed, ref invalidMetaFiles, ref duplicateGuids, duplicateGuidRecords, invalidMetaFilePaths, progress, indexBuildWorkerCount, cancellationToken);

            var folderToSubFolders = new Dictionary<string, string[]>(PathComparer);
            foreach (var pair in folderChildrenBuilders)
                folderToSubFolders[pair.Key] = pair.Value == null || pair.Value.Count == 0 ? Array.Empty<string>() : pair.Value.ToArray();

            timer.Stop();
            return new AssetDatabaseThreadSafeBuildResult(
                pathToGuid,
                pathToGuidValue,
                guidToPath,
                guidValueToPath,
                folderPaths,
                folderPathSet,
                folderToSubFolders,
                fileMetadata,
                fullRoot,
                includePackages,
                metaFilesSeen,
                metaFilesParsed,
                invalidMetaFiles,
                duplicateGuids,
                directoriesScanned,
                errors,
                duplicateGuidRecords,
                invalidMetaFilePaths,
                errorDirectories,
                timer.Elapsed.TotalMilliseconds,
                journalGeneration);
        }

        private readonly struct ParsedMetaFile
        {
            public readonly string MetaPath;
            public readonly string Guid;
            public readonly bool Valid;

            public ParsedMetaFile(string metaPath, string guid, bool valid)
            {
                MetaPath = metaPath;
                Guid = guid;
                Valid = valid;
            }
        }

        private static void ProcessMetaBatch(
            List<string> metaBatch,
            string projectRoot,
            Dictionary<string, string> pathToGuid,
            Dictionary<string, GUID> pathToGuidValue,
            Dictionary<string, string> guidToPath,
            Dictionary<GUID, string> guidValueToPath,
            Dictionary<string, AssetMetadataRecord> fileMetadata,
            ref long metaFilesParsed,
            ref long invalidMetaFiles,
            ref long duplicateGuids,
            List<DuplicateGuidRecord> duplicateGuidRecords,
            List<string> invalidMetaFilePaths,
            AssetDatabaseThreadSafeBuildProgress progress,
            int workerCount,
            CancellationToken cancellationToken)
        {
            var parsed = new ParsedMetaFile[metaBatch.Count];
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cancellationToken
            };

            Parallel.For(0, metaBatch.Count, options, i =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metaPath = metaBatch[i];
                parsed[i] = TryReadGuidFromMetaFile(metaPath, out var guid)
                    ? new ParsedMetaFile(metaPath, guid, true)
                    : new ParsedMetaFile(metaPath, null, false);
            });

            for (var i = 0; i < parsed.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = parsed[i];
                if (!entry.Valid)
                {
                    invalidMetaFiles++;
                    if (invalidMetaFilePaths.Count < DiagnosticListLimit)
                        invalidMetaFilePaths.Add(entry.MetaPath);
                    progress?.IncrementInvalidMetaFiles();
                    continue;
                }

                var assetPath = ToAssetPath(entry.MetaPath.Substring(0, entry.MetaPath.Length - MetaExtension.Length), projectRoot);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                metaFilesParsed++;
                progress?.IncrementMetaFilesParsed();
                var guidValue = new GUID(entry.Guid);
                pathToGuid[assetPath] = entry.Guid;
                pathToGuidValue[assetPath] = guidValue;

                string fullAssetPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
                bool isFolder = Directory.Exists(fullAssetPath);
                if (!isFolder)
                {
                    long fileSize = 0L;
                    long lastWriteTicks = 0L;
                    long metaLastWriteTicks = 0L;
                    try
                    {
                        var fileInfo = new FileInfo(fullAssetPath);
                        if (fileInfo.Exists)
                        {
                            fileSize = fileInfo.Length;
                            lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
                        }
                        metaLastWriteTicks = File.GetLastWriteTimeUtc(entry.MetaPath).Ticks;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    fileMetadata[assetPath] = new AssetMetadataRecord(
                        assetPath, entry.Guid, GetTextMetaFilePathFromAssetPath(assetPath), fileSize, lastWriteTicks, metaLastWriteTicks);
                }

                if (guidToPath.TryGetValue(entry.Guid, out var firstAssetPath))
                {
                    duplicateGuids++;
                    if (duplicateGuidRecords.Count < DiagnosticListLimit)
                        duplicateGuidRecords.Add(new DuplicateGuidRecord(entry.Guid, firstAssetPath, assetPath));
                    continue;
                }

                guidToPath.Add(entry.Guid, assetPath);
                guidValueToPath.Add(guidValue, assetPath);
            }
        }

        private static bool TryReadGuidFromMetaFile(string path, out string guid)
        {
            guid = null;
            var buffer = ArrayPool<byte>.Shared.Rent(MetaProbeBytes);

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, MetaProbeBytes, FileOptions.SequentialScan);
                var read = stream.Read(buffer, 0, MetaProbeBytes);
                if (read <= 0)
                    return false;

                for (var i = 0; i < read - GuidKey.Length; i++)
                {
                    if (i > 0 && buffer[i - 1] != (byte)'\n' && buffer[i - 1] != (byte)'\r')
                        continue;

                    if (buffer[i] != (byte)'g' ||
                        buffer[i + 1] != (byte)'u' ||
                        buffer[i + 2] != (byte)'i' ||
                        buffer[i + 3] != (byte)'d' ||
                        buffer[i + 4] != (byte)':')
                        continue;

                    var valueStart = i + GuidKey.Length;
                    while (valueStart < read && (buffer[valueStart] == (byte)' ' || buffer[valueStart] == (byte)'\t'))
                        valueStart++;

                    if (valueStart + 32 > read)
                        return false;

                    for (var j = 0; j < 32; j++)
                    {
                        if (!IsHex(buffer[valueStart + j]))
                            return false;
                    }

                    guid = Encoding.ASCII.GetString(buffer, valueStart, 32);
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return false;
        }

        private static string[] ReadLabelsFromMetaFile(string absoluteMetaPath)
        {
            if (!File.Exists(absoluteMetaPath))
                return Array.Empty<string>();

            var labels = new List<string>(4);
            try
            {
                using var reader = new StreamReader(absoluteMetaPath, Encoding.UTF8, true, 4096);
                var inLabels = false;
                var labelsIndent = -1;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.TrimStart();
                    var indent = line.Length - trimmed.Length;

                    if (!inLabels)
                    {
                        if (!trimmed.StartsWith(LabelsKey, StringComparison.Ordinal))
                            continue;

                        var remainder = trimmed.Substring(LabelsKey.Length).Trim();
                        if (remainder.Length == 0)
                        {
                            inLabels = true;
                            labelsIndent = indent;
                            continue;
                        }

                        if (remainder == "[]")
                            return Array.Empty<string>();

                        if (remainder[0] == '[')
                            return ParseInlineLabels(remainder);

                        inLabels = true;
                        labelsIndent = indent;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    if (indent <= labelsIndent && !trimmed.StartsWith("-", StringComparison.Ordinal))
                        break;

                    if (!trimmed.StartsWith("-", StringComparison.Ordinal))
                        continue;

                    var labelValue = trimmed.Substring(1).Trim();
                    if (labelValue.Length == 0)
                        continue;

                    labels.Add(ParseYamlScalar(labelValue));
                }
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }

            return labels.Count == 0 ? Array.Empty<string>() : labels.ToArray();
        }

        private static string[] ParseInlineLabels(string value)
        {
            var inner = value.Trim();
            if (inner.Length < 2 || inner[0] != '[' || inner[inner.Length - 1] != ']')
                return Array.Empty<string>();

            inner = inner.Substring(1, inner.Length - 2);
            var values = new List<string>();
            var start = 0;
            var quote = '\0';
            for (var i = 0; i <= inner.Length; i++)
            {
                var end = i == inner.Length;
                if (!end)
                {
                    var c = inner[i];
                    if ((c == '\'' || c == '"') && (i == 0 || inner[i - 1] != '\\'))
                        quote = quote == '\0' ? c : (quote == c ? '\0' : quote);
                    if (c != ',' || quote != '\0')
                        continue;
                }

                var token = inner.Substring(start, i - start).Trim();
                if (token.Length > 0)
                    values.Add(ParseYamlScalar(token));
                start = i + 1;
            }

            return values.Count == 0 ? Array.Empty<string>() : values.ToArray();
        }

        private static string ParseYamlScalar(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                var inner = value.Substring(1, value.Length - 2);
                return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            return value;
        }

        private static bool IsHex(byte value)
        {
            return (value >= (byte)'0' && value <= (byte)'9') ||
                   (value >= (byte)'a' && value <= (byte)'f') ||
                   (value >= (byte)'A' && value <= (byte)'F');
        }

        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            path = path.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(path) && !string.IsNullOrEmpty(s_ProjectRoot))
                path = ToAssetPath(path);

            while (path.Contains("//"))
                path = path.Replace("//", "/");

            path = path.TrimStart('/');
            if (path == ".")
                return string.Empty;

            return path;
        }

        private static string ToAbsolutePath(string assetPath)
        {
            if (string.IsNullOrEmpty(s_ProjectRoot))
                throw new InvalidOperationException("AssetDatabaseThreadSafeUtility is not initialized. Call Initialize(projectRoot) first.");

            var normalized = NormalizeAssetPath(assetPath);
            return Path.GetFullPath(Path.Combine(s_ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string ToAssetPath(string absolutePath)
        {
            return ToAssetPath(absolutePath, s_ProjectRoot);
        }

        private static string ToAssetPath(string absolutePath, string projectRoot)
        {
            var fullPath = Path.GetFullPath(absolutePath).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            var normalizedRoot = Path.GetFullPath(projectRoot).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimEnd('/');

            if (string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var prefix = normalizedRoot + "/";
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(prefix.Length);

            return fullPath;
        }
    }
}

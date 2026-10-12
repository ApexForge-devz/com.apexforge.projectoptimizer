using System;
using System.Collections.Generic;
using System.IO;
using ApexForge.ProjectOptimizer;

namespace ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe
{
    /// <summary>
    /// Durable, append-only record of AssetPostprocessor changes between persistent project-state snapshots.
    /// The journal is intentionally path-oriented and compact: no Unity objects or analyzer data are retained.
    /// </summary>
    internal static class AssetDatabaseChangeJournal
    {
        const int Magic = 0x504F4A52; // POJR
        const int Version = 1;
        const int MaxRecords = 5_000_000;
        const int MaxPathsPerRecord = 1_000_000;
        static readonly object s_Lock = new object();
        static readonly string s_Path = ProjectOptimizerConstants.AssetDatabaseChangesPath;

        static string s_ProjectRoot;
        static bool s_IncludePackages;
        static bool s_Loaded;
        static bool s_Corrupt;
        static long s_BaseGeneration;
        static long s_LastGeneration;

        internal sealed class Record
        {
            public long Generation;
            public string[] Imported = Array.Empty<string>();
            public string[] Deleted = Array.Empty<string>();
            public string[] Moved = Array.Empty<string>();
            public string[] MovedFrom = Array.Empty<string>();
            public bool DidDomainReload;
        }

        internal readonly struct State
        {
            public readonly bool Compatible;
            public readonly bool Corrupt;
            public readonly long BaseGeneration;
            public readonly long LastGeneration;
            public State(bool compatible, bool corrupt, long baseGeneration, long lastGeneration)
            {
                Compatible = compatible;
                Corrupt = corrupt;
                BaseGeneration = baseGeneration;
                LastGeneration = lastGeneration;
            }
        }

        internal static string FilePath => s_Path;

        internal static State Load(string projectRoot, bool includePackages)
        {
            lock (s_Lock)
            {
                EnsureLoadedLocked(projectRoot, includePackages);
                return new State(s_Loaded && !s_Corrupt, s_Corrupt, s_BaseGeneration, s_LastGeneration);
            }
        }

        internal static bool TryReadPending(string projectRoot, bool includePackages, long afterGeneration, List<Record> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            lock (s_Lock)
            {
                EnsureLoadedLocked(projectRoot, includePackages);
                if (!s_Loaded || s_Corrupt) return false;
                if (afterGeneration < s_BaseGeneration) return false;
                if (afterGeneration >= s_LastGeneration) return true;
                try
                {
                    using var reader = new BinaryReader(File.Open(s_Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    if (!ReadHeader(reader, projectRoot, includePackages, out var baseGeneration)) return false;
                    int recordCount = 0;
                    while (reader.BaseStream.Position < reader.BaseStream.Length)
                    {
                        if (++recordCount > MaxRecords) return false;
                        var record = ReadRecord(reader);
                        if (record == null) return false;
                        if (record.Generation <= afterGeneration) continue;
                        destination.Add(record);
                    }
                    return true;
                }
                catch
                {
                    s_Corrupt = true;
                    return false;
                }
            }
        }

        internal static bool Append(string projectRoot, bool includePackages,
            string[] imported, string[] deleted, string[] moved, string[] movedFrom, bool didDomainReload, out long generation)
        {
            generation = 0;
            if ((imported == null || imported.Length == 0)
                && (deleted == null || deleted.Length == 0)
                && (moved == null || moved.Length == 0)
                && (movedFrom == null || movedFrom.Length == 0))
                return true;

            lock (s_Lock)
            {
                EnsureLoadedLocked(projectRoot, includePackages);
                if (!CacheFileUtility.EnsureCacheFolder()) return false;
                // A corrupt durable journal cannot safely be repaired implicitly. The caller must
                // perform an authoritative rebuild before the journal is reset; otherwise changes that
                // preceded the corruption could be silently forgotten.
                if (!s_Loaded || s_Corrupt) return false;

                generation = ++s_LastGeneration;
                var record = new Record
                {
                    Generation = generation,
                    Imported = NormalizePaths(imported),
                    Deleted = NormalizePaths(deleted),
                    Moved = NormalizePaths(moved),
                    MovedFrom = NormalizePaths(movedFrom),
                    DidDomainReload = didDomainReload
                };

                try
                {
                    if (!File.Exists(s_Path))
                    {
                        ResetLocked(projectRoot, includePackages, s_BaseGeneration);
                    }
                    using var stream = new FileStream(s_Path, FileMode.Append, FileAccess.Write, FileShare.Read, 32 * 1024);
                    using var writer = new BinaryWriter(stream);
                    writer.Write(record.Generation);
                    writer.Write(record.DidDomainReload);
                    WritePaths(writer, record.Imported);
                    WritePaths(writer, record.Deleted);
                    WritePaths(writer, record.Moved);
                    WritePaths(writer, record.MovedFrom);
                    writer.Flush();
                    stream.Flush(true);
                    return true;
                }
                catch
                {
                    s_Corrupt = true;
                    return false;
                }
            }
        }

        internal static bool Reset(string projectRoot, bool includePackages, long generation)
        {
            lock (s_Lock)
            {
                if (!CacheFileUtility.EnsureCacheFolder()) return false;
                ResetLocked(projectRoot, includePackages, Math.Max(0L, generation));
                return !s_Corrupt;
            }
        }

        internal static bool Compact(string projectRoot, bool includePackages, long generation)
        {
            lock (s_Lock)
            {
                EnsureLoadedLocked(projectRoot, includePackages);
                if (!CacheFileUtility.EnsureCacheFolder()) return false;
                if (!s_Loaded || s_Corrupt) return false;
                if (generation < s_BaseGeneration || generation > s_LastGeneration) return false;
                if (generation == s_LastGeneration)
                {
                    ResetLocked(projectRoot, includePackages, generation);
                    return !s_Corrupt;
                }

                string temp = s_Path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using var input = new BinaryReader(File.Open(s_Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    if (!ReadHeader(input, projectRoot, includePackages, out _)) return false;
                    using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024);
                    using var output = new BinaryWriter(stream);
                    WriteHeader(output, projectRoot, includePackages, generation);
                    int recordCount = 0;
                    long last = generation;
                    while (input.BaseStream.Position < input.BaseStream.Length)
                    {
                        if (++recordCount > MaxRecords) throw new InvalidDataException();
                        var record = ReadRecord(input);
                        if (record == null) throw new InvalidDataException();
                        if (record.Generation <= generation) continue;
                        if (record.Generation <= last) throw new InvalidDataException();
                        WriteRecord(output, record);
                        last = record.Generation;
                    }
                    output.Flush();
                    stream.Flush(true);
                    CacheFileUtility.ReplaceFile(temp, s_Path);
                    s_ProjectRoot = Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    s_IncludePackages = includePackages;
                    s_Loaded = true;
                    s_Corrupt = false;
                    s_BaseGeneration = generation;
                    s_LastGeneration = last;
                    return true;
                }
                catch
                {
                    s_Corrupt = true;
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    return false;
                }
            }
        }

        internal static void ResetRuntimeState()
        {
            lock (s_Lock)
            {
                s_ProjectRoot = null;
                s_IncludePackages = false;
                s_Loaded = false;
                s_Corrupt = false;
                s_BaseGeneration = 0;
                s_LastGeneration = 0;
            }
        }

        static void EnsureLoadedLocked(string projectRoot, bool includePackages)
        {
            string normalizedRoot = Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (s_Loaded && string.Equals(s_ProjectRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase) && s_IncludePackages == includePackages)
                return;

            s_ProjectRoot = normalizedRoot;
            s_IncludePackages = includePackages;
            s_Loaded = false;
            s_Corrupt = false;
            s_BaseGeneration = 0;
            s_LastGeneration = 0;

            if (!File.Exists(s_Path))
            {
                s_Loaded = true;
                return;
            }

            try
            {
                using var reader = new BinaryReader(File.Open(s_Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                if (!ReadHeader(reader, normalizedRoot, includePackages, out var baseGeneration))
                {
                    s_Corrupt = true;
                    return;
                }
                int recordCount = 0;
                long last = baseGeneration;
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    if (++recordCount > MaxRecords)
                    {
                        s_Corrupt = true;
                        return;
                    }
                    var record = ReadRecord(reader);
                    if (record == null || record.Generation <= last)
                    {
                        s_Corrupt = true;
                        return;
                    }
                    last = record.Generation;
                }
                s_BaseGeneration = baseGeneration;
                s_LastGeneration = last;
                s_Loaded = true;
            }
            catch
            {
                s_Corrupt = true;
            }
        }

        static void ResetLocked(string projectRoot, bool includePackages, long baseGeneration)
        {
            try
            {
                string directory = Path.GetDirectoryName(s_Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temp = s_Path + ".tmp." + Guid.NewGuid().ToString("N");
                using (var writer = new BinaryWriter(File.Open(temp, FileMode.Create, FileAccess.Write, FileShare.None)))
                {
                    WriteHeader(writer, projectRoot, includePackages, baseGeneration);
                    writer.Flush();
                }
                CacheFileUtility.ReplaceFile(temp, s_Path);
                s_ProjectRoot = Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                s_IncludePackages = includePackages;
                s_Loaded = true;
                s_Corrupt = false;
                s_BaseGeneration = baseGeneration;
                s_LastGeneration = baseGeneration;
            }
            catch
            {
                s_Corrupt = true;
            }
        }

        static bool ReadHeader(BinaryReader reader, string projectRoot, bool includePackages, out long baseGeneration)
        {
            baseGeneration = 0;
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return false;
            string root = reader.ReadString();
            bool packages = reader.ReadBoolean();
            if (!string.Equals(root, Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
            if (packages != includePackages) return false;
            baseGeneration = reader.ReadInt64();
            return baseGeneration >= 0;
        }

        static void WriteHeader(BinaryWriter writer, string projectRoot, bool includePackages, long baseGeneration)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            writer.Write(includePackages);
            writer.Write(baseGeneration);
        }

        static Record ReadRecord(BinaryReader reader)
        {
            try
            {
                var record = new Record { Generation = reader.ReadInt64(), DidDomainReload = reader.ReadBoolean() };
                if (record.Generation < 0) return null;
                record.Imported = ReadPaths(reader);
                record.Deleted = ReadPaths(reader);
                record.Moved = ReadPaths(reader);
                record.MovedFrom = ReadPaths(reader);
                return record;
            }
            catch
            {
                return null;
            }
        }

        static string[] ReadPaths(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxPathsPerRecord) throw new InvalidDataException();
            var result = count == 0 ? Array.Empty<string>() : new string[count];
            for (int i = 0; i < count; i++) result[i] = reader.ReadString();
            return result;
        }

        static void WritePaths(BinaryWriter writer, string[] paths)
        {
            writer.Write(paths != null ? paths.Length : 0);
            if (paths == null) return;
            for (int i = 0; i < paths.Length; i++) writer.Write(paths[i] ?? string.Empty);
        }

        static void WriteRecord(BinaryWriter writer, Record record)
        {
            writer.Write(record.Generation);
            writer.Write(record.DidDomainReload);
            WritePaths(writer, record.Imported);
            WritePaths(writer, record.Deleted);
            WritePaths(writer, record.Moved);
            WritePaths(writer, record.MovedFrom);
        }

        static string[] NormalizePaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return Array.Empty<string>();
            var result = new List<string>(paths.Length);
            for (int i = 0; i < paths.Length; i++)
            {
                var path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(paths[i]);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
            return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
        }
    }
}

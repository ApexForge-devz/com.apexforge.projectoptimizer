using System;
using System.Collections.Generic;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Dense project identity table shared by scanning, source-data analysis and graph indexing.</summary>
    public sealed class AssetCatalog
    {
        public readonly struct Record
        {
            public readonly AssetId Id;
            public readonly string Path;
            public readonly string Guid;
            public readonly AssetKind Kind;
            public readonly long FileSize;
            public readonly long LastWriteTicks;

            public Record(AssetId id, string path, string guid, AssetKind kind, long fileSize, long lastWriteTicks)
            {
                Id = id;
                Path = path ?? string.Empty;
                Guid = guid ?? string.Empty;
                Kind = kind;
                FileSize = fileSize;
                LastWriteTicks = lastWriteTicks;
            }
        }

        readonly List<Record> m_Records = new List<Record>();
        readonly Dictionary<string, AssetId> m_ByPath = new Dictionary<string, AssetId>(StringComparer.Ordinal);
        readonly Dictionary<string, AssetId> m_ByGuid = new Dictionary<string, AssetId>(StringComparer.OrdinalIgnoreCase);

        public int Count => m_Records.Count;
        public IReadOnlyList<Record> Records => m_Records;

        public AssetId AddOrUpdate(string path, string guid, AssetKind kind, long fileSize, long lastWriteTicks)
        {
            if (string.IsNullOrEmpty(path)) return default(AssetId);
            if (m_ByPath.TryGetValue(path, out AssetId existing))
            {
                Record old = m_Records[existing.Value];
                if (!string.IsNullOrEmpty(old.Guid) && !string.Equals(old.Guid, guid, StringComparison.OrdinalIgnoreCase)) m_ByGuid.Remove(old.Guid);
                m_Records[existing.Value] = new Record(existing, path, guid, kind, fileSize, lastWriteTicks);
                if (!string.IsNullOrEmpty(guid)) m_ByGuid[guid] = existing;
                return existing;
            }

            AssetId id = new AssetId(m_Records.Count);
            m_ByPath[path] = id;
            if (!string.IsNullOrEmpty(guid)) m_ByGuid[guid] = id;
            m_Records.Add(new Record(id, path, guid, kind, fileSize, lastWriteTicks));
            return id;
        }

        public bool TryGet(string path, out Record record)
        {
            if (!string.IsNullOrEmpty(path) && m_ByPath.TryGetValue(path, out AssetId id))
            {
                record = m_Records[id.Value];
                return true;
            }
            record = default(Record);
            return false;
        }

        public bool TryGet(AssetId id, out Record record)
        {
            if (id.Value >= 0 && id.Value < m_Records.Count)
            {
                record = m_Records[id.Value];
                return true;
            }
            record = default(Record);
            return false;
        }

        public bool TryGetByGuid(string guid, out Record record)
        {
            if (!string.IsNullOrEmpty(guid) && m_ByGuid.TryGetValue(guid, out AssetId id))
            {
                record = m_Records[id.Value];
                return true;
            }
            record = default(Record);
            return false;
        }

        public string GuidFor(string path) => TryGet(path, out Record record) ? record.Guid : string.Empty;
        public string PathForGuid(string guid) => TryGetByGuid(guid, out Record record) ? record.Path : string.Empty;
        public AssetId IdFor(string path) => TryGet(path, out Record record) ? record.Id : default(AssetId);
    }
}

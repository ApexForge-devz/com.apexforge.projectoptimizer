using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Search;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Starts a small number of non-blocking main-asset loads ahead of the scanner cursor.</summary>
	public sealed class AssetLoadPrefetcher : IDisposable
	{
		sealed class Pending
		{
			public string Path;
			public AssetDatabaseLoadOperation Operation;
			public double StartedAt;
		}

		readonly Dictionary<string, Pending> m_Pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
		readonly int m_MaxConcurrent;
		bool m_Disposed;
		public int Started { get; private set; }
		public int Completed { get; private set; }
		public int Active => m_Pending.Count;

		public AssetLoadPrefetcher(OptimizerSettings settings)
		{
			m_MaxConcurrent = settings != null && settings.scanPacingMode == ScanPacingMode.Throughput ? 2 : 1;
		}

		public void Pump()
		{
			if (m_Disposed) return;
			var completed = new List<string>();
			foreach (var pair in m_Pending)
			{
				var pending = pair.Value;
				if (pending?.Operation == null || !pending.Operation.isDone) continue;
				completed.Add(pair.Key);
				Completed++;
				ScanTiming.Active?.AddDiagnosticTiming("AssetDatabase/LoadObjectAsync/complete", Math.Max(0.0, ScanTiming.Now - pending.StartedAt));
			}
			for (int i = 0; i < completed.Count; i++) m_Pending.Remove(completed[i]);
		}

		public void Prefetch(IReadOnlyList<string> paths, int currentIndex, int lookAhead, Func<string, bool> shouldPrefetch = null)
		{
			if (m_Disposed || paths == null || lookAhead <= 0) return;
			Pump();
			int end = Math.Min(paths.Count, currentIndex + 1 + lookAhead);
			for (int i = currentIndex + 1; i < end && m_Pending.Count < m_MaxConcurrent; i++)
			{
				string path = paths[i];
				if (string.IsNullOrEmpty(path) || m_Pending.ContainsKey(path)) continue;
				if (shouldPrefetch != null && !shouldPrefetch(path)) continue;
				if (AssetLoadUtility.IsMainAssetAtPathLoaded(path)) continue;
				int instanceId;
				try { instanceId = SearchUtils.GetMainAssetInstanceID(path); } catch { instanceId = 0; }
				if (!AssetLoadUtility.TryGetLocalFileIdentifier(instanceId, out long localId)) continue;
				var operation = AssetLoadUtility.LoadObjectAsync(path, localId);
				if (operation == null) continue;
				m_Pending[path] = new Pending { Path = path, Operation = operation, StartedAt = ScanTiming.Now };
				Started++;
			}
		}

		public void Dispose()
		{
			if (m_Disposed) return;
			m_Disposed = true;
			m_Pending.Clear();
		}
	}
}

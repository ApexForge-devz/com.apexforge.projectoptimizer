using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	[ProjectOptimizerAnalyzer]
	public sealed class DependencyHotspotAnalyzer : IReferenceAwareBackgroundProjectAnalyzer
	{
		const int k_Count = 25;

		static DependencyHotspotAnalyzer()
		{
			Rules.Register(id:"DEP001", title:"High dependency fan-in", category:AssetCategory.Build,
				why:"Many assets depend directly on this asset, so a change or duplication of it can propagate broadly through the project and build graph.",
				fix:"Treat highly shared assets as dependency hubs; optimize or split them only with build evidence.",
				severity:IssueSeverity.Info, fixKind:FixKind.None, confidence:OptimizationConfidence.HighConfidence,
				risk:FixRisk.Unspecified, backupRequired:false);
			Rules.Register(id:"DEP002", title:"Large dependency fan-out", category:AssetCategory.Build,
				why:"This asset directly references many other assets, increasing its dependency surface and the chance of dragging extra content into builds.",
				fix:"Review the referenced content and separate optional content when the runtime loading model permits it.",
				severity:IssueSeverity.Info, fixKind:FixKind.None, confidence:OptimizationConfidence.HighConfidence,
				risk:FixRisk.Unspecified, backupRequired:false);
		}

		public string DisplayName => "Dependency hotspot analysis";
		public bool Enabled(OptimizerSettings s) => s == null || s.scanProjectSettings;
		public float Progress { get; private set; }

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			var refs = ctx.References;
			if (refs == null || !refs.IsComplete) { Progress = 1f; yield break; }

			CollectTopRows(refs, ctx.ReferenceScopePaths, k_Count, out var fanIn, out var fanOut);
			for (int i = 0; i < fanIn.Count; i++)
			{
				var row = ResolveRow(fanIn[i], ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(fanIn[i].Path) : FileUtility.FileSizeOf(fanIn[i].Path), ctx.GetGuid(fanIn[i].Path));
				var e = NewEntry(row.Path, "FanIn", row.Guid, row.FileSize);
				using (RuleTiming.Begin("DEP001", e.Path))
					if (row.FanIn >= 32) e.AddIssue("DEP001", row.FanIn.ToString("N0") + " direct referrers", "Review this asset as a dependency hub; avoid duplicate copies");
				e.Finalise();
				yield return e;
			}

			for (int i = 0; i < fanOut.Count; i++)
			{
				var row = ResolveRow(fanOut[i], ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(fanOut[i].Path) : FileUtility.FileSizeOf(fanOut[i].Path), ctx.GetGuid(fanOut[i].Path));
				var e = NewEntry(row.Path, "FanOut", row.Guid, row.FileSize);
				using (RuleTiming.Begin("DEP002", e.Path))
					if (row.FanOut >= 32) e.AddIssue("DEP002", row.FanOut.ToString("N0") + " direct dependencies", "Review whether optional content can be split");
				e.Finalise();
				yield return e;
			}
			Progress = 1f;
		}

		public IReadOnlyList<AssetEntry> AnalyzeBackground(BackgroundProjectContext context)
		{
			var refs = context.References;
			if (refs == null || !refs.IsComplete) { Progress = 1f; return Array.Empty<AssetEntry>(); }

			CollectTopRows(refs, refs.NodePaths, k_Count, out var fanIn, out var fanOut);
			var result = new List<AssetEntry>(k_Count * 2);

			for (int i = 0; i < fanIn.Count; i++)
			{
				var row = ResolveRow(fanIn[i], context.GetFileSize(fanIn[i].Path), context.GetGuidFromMeta(fanIn[i].Path));
				var e = NewEntry(row.Path, "FanIn", row.Guid, row.FileSize);
				using (RuleTiming.Begin("DEP001", e.Path))
					if (row.FanIn >= 32) e.AddIssue("DEP001", row.FanIn.ToString("N0") + " direct referrers", "Review this asset as a dependency hub; avoid duplicate copies");
				e.Finalise();
				result.Add(e);
			}

			for (int i = 0; i < fanOut.Count; i++)
			{
				var row = ResolveRow(fanOut[i], context.GetFileSize(fanOut[i].Path), context.GetGuidFromMeta(fanOut[i].Path));
				var e = NewEntry(row.Path, "FanOut", row.Guid, row.FileSize);
				using (RuleTiming.Begin("DEP002", e.Path))
					if (row.FanOut >= 32) e.AddIssue("DEP002", row.FanOut.ToString("N0") + " direct dependencies", "Review whether optional content can be split");
				e.Finalise();
				result.Add(e);
			}

			Progress = 1f;
			return result;
		}

		static void CollectTopRows(ReferenceIndex refs, IEnumerable<string> paths, int count, out IReadOnlyList<Row> fanIn, out IReadOnlyList<Row> fanOut)
		{
			int safeCount = Math.Max(1, count);
			var fanInHeap = new TopKHeap(safeCount, true);
			var fanOutHeap = new TopKHeap(safeCount, false);
			foreach (var path in paths)
			{
				if (string.IsNullOrEmpty(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

				int inCount = refs.ReferrersOf(path).Count;
				int outCount = refs.DependenciesOf(path).Count;
				if (inCount <= 0 && outCount <= 0) continue;

				if (inCount > 0) fanInHeap.Add(new Row { Path = path, FanIn = inCount, FanOut = outCount });
				if (outCount > 0) fanOutHeap.Add(new Row { Path = path, FanIn = inCount, FanOut = outCount });
			}

			// Return order is deterministic and only the final K rows reach metadata/GUID resolution.
			fanIn = fanInHeap.ToSortedList(true);
			fanOut = fanOutHeap.ToSortedList(false);
		}

		static Row ResolveRow(Row row, long fileSize, string guid)
		{
			row.FileSize = fileSize;
			row.Guid = guid ?? string.Empty;
			return row;
		}

		static AssetEntry NewEntry(string path, string kind, string guid, long fileSize) => new AssetEntry
		{
			Guid = guid ?? string.Empty,
			Path = "DependencyHotspot/" + kind + "/" + path,
			Name = System.IO.Path.GetFileName(path),
			Directory = AssetPathUtility.Directoryish(path),
			Extension = System.IO.Path.GetExtension(path),
			Category = AssetCategory.Build,
			TypeLabel = "Dependency hotspot",
			FileSize = fileSize,
			RuntimeSize = -1
		};

		struct Row
		{
			public string Path;
			public string Guid;
			public long FileSize;
			public int FanIn;
			public int FanOut;
		}

		/// <summary>Keeps only the requested best rows. The heap root is the worst retained candidate.</summary>
		sealed class TopKHeap
		{
			readonly Row[] m_Items;
			readonly bool m_UseFanIn;
			int m_Count;

			public TopKHeap(int capacity, bool useFanIn)
			{
				m_Items = new Row[Math.Max(1, capacity)];
				m_UseFanIn = useFanIn;
			}

			public void Add(Row candidate)
			{
				if (m_Count < m_Items.Length)
				{
					m_Items[m_Count] = candidate;
					SiftUp(m_Count++);
					return;
				}

				if (!IsBetter(candidate, m_Items[0])) return;
				m_Items[0] = candidate;
				SiftDown(0);
			}

			public List<Row> ToSortedList(bool useFanIn)
			{
				var result = new List<Row>(m_Count);
				for (int i = 0; i < m_Count; i++) result.Add(m_Items[i]);
				result.Sort((a, b) => CompareBetter(a, b, useFanIn));
				return result;
			}

			bool IsBetter(Row a, Row b) => CompareBetter(a, b, m_UseFanIn) < 0;

			void SiftUp(int index)
			{
				while (index > 0)
				{
					int parent = (index - 1) >> 1;
					if (!IsWorse(m_Items[index], m_Items[parent], m_UseFanIn)) break;
					Swap(index, parent);
					index = parent;
				}
			}

			void SiftDown(int index)
			{
				while (true)
				{
					int left = index * 2 + 1;
					if (left >= m_Count) return;
					int right = left + 1;
					int worst = right < m_Count && IsWorse(m_Items[right], m_Items[left], m_UseFanIn) ? right : left;
					if (!IsWorse(m_Items[worst], m_Items[index], m_UseFanIn)) return;
					Swap(index, worst);
					index = worst;
				}
			}

			static int CompareBetter(Row a, Row b, bool useFanIn)
			{
				int av = useFanIn ? a.FanIn : a.FanOut;
				int bv = useFanIn ? b.FanIn : b.FanOut;
				int cmp = bv.CompareTo(av);
				return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.Ordinal);
			}

			static bool IsWorse(Row a, Row b, bool useFanIn)
			{
				int metricA = useFanIn ? a.FanIn : a.FanOut;
				int metricB = useFanIn ? b.FanIn : b.FanOut;
				if (metricA != metricB) return metricA < metricB;
				// For equal counts, lexicographically later paths are worse because final order is path-ascending.
				return string.Compare(a.Path, b.Path, StringComparison.Ordinal) > 0;
			}

			void Swap(int a, int b) { var tmp = m_Items[a]; m_Items[a] = m_Items[b]; m_Items[b] = tmp; }
		}
	}
}

using System;
using System.Collections.Generic;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// How much each material is actually used: by how many renderers, in how many prefabs, in how
	/// many scenes.
	/// <para>
	/// Per-asset rules can say a material has GPU instancing off, or that two materials are
	/// identical. They cannot say whether that matters, because importance lives in the content
	/// rather than in the asset - a material on four hundred renderers and a material on one produce
	/// exactly the same finding and are completely different pieces of work. This index is the
	/// missing half.
	/// </para>
	/// <para>
	/// The counts come from the prefab and scene walk, which is already opening every hierarchy in
	/// the project and can record the material each renderer uses for very little extra. They are
	/// therefore only as complete as that walk: with prefabs or scenes switched off, or a scan
	/// restricted to one folder, this counts what was scanned and no more. <see cref="Partial"/>
	/// records that, so a count of zero is never read as "used nowhere" when it means "not looked
	/// for everywhere".
	/// </para>
	/// </summary>
	public sealed class MaterialUsageIndex
	{
		/// <summary>
		/// What one material is used by, kept as two separate counts.
		/// <para>
		/// A renderer slot inside a prefab asset and a renderer slot inside a scene are not the same
		/// unit and adding them produces a number that means nothing. A prefab with twenty renderers,
		/// instantiated in ten scenes, contributes twenty from the prefab walk and two hundred from
		/// the scene walk; a combined 220 is neither "places this material is authored" nor "renderer
		/// instances at runtime" - and it double-counts the prefab's own renderers on top. So the two
		/// are reported side by side and never summed.
		/// </para>
		/// </summary>
		public struct Counts
		{
			/// <summary>Renderer material slots inside prefab assets - where the material is authored.</summary>
			public int PrefabSlots;

			/// <summary>
			/// Renderer material slots found in scenes, including those belonging to prefab
			/// instances. Closer to a runtime count than the prefab figure, and still not one:
			/// objects spawned at runtime are invisible to a static walk.
			/// </summary>
			public int SceneSlots;

			/// <summary>Distinct prefab assets that use it.</summary>
			public int Prefabs;

			/// <summary>Distinct scenes that use it.</summary>
			public int Scenes;

			public bool Any => PrefabSlots > 0 || SceneSlots > 0;

			/// <summary>
			/// A phrase that says what was counted and where, without implying a runtime instance
			/// count that nothing here measured.
			/// </summary>
			public string Describe()
			{
				var parts = new List<string>();

				if (PrefabSlots > 0)
				{
					parts.Add(PrefabSlots + " renderer slot" + (PrefabSlots == 1 ? "" : "s")
							  + " in " + Prefabs + " prefab" + (Prefabs == 1 ? "" : "s"));
				}

				if (SceneSlots > 0)
				{
					parts.Add(SceneSlots + " renderer slot" + (SceneSlots == 1 ? "" : "s")
							  + " in " + Scenes + " scene" + (Scenes == 1 ? "" : "s"));
				}

				return parts.Count == 0 ? "nothing scanned" : string.Join(" and ", parts.ToArray());
			}
		}

		sealed class Entry
		{
			public int PrefabSlots;
			public int SceneSlots;
			public readonly HashSet<string> Prefabs = new HashSet<string>(StringComparer.Ordinal);
			public readonly HashSet<string> Scenes = new HashSet<string>(StringComparer.Ordinal);
		}

		readonly Dictionary<string, Entry> m_ByMaterial = new Dictionary<string, Entry>(StringComparer.Ordinal);

		/// <summary>
		/// True when the walk that fed this index did not cover the whole project, so a low count
		/// means "as far as this scan looked" rather than "in this project".
		/// </summary>
		public bool Partial { get; set; }

		public bool IsEmpty => m_ByMaterial.Count == 0;

		public int MaterialCount => m_ByMaterial.Count;

		/// <summary>Records that one hierarchy used these materials, one entry per renderer slot.</summary>
		public void Record(string containerPath, bool isScene, IReadOnlyList<string> materialPaths,
			IReadOnlyList<int> rendererCounts)
		{
			if (materialPaths == null || rendererCounts == null) return;

			int count = Math.Min(materialPaths.Count, rendererCounts.Count);
			for (int i = 0; i < count; i++)
			{
				var path = materialPaths[i];
				if (string.IsNullOrEmpty(path)) continue;

				if (!m_ByMaterial.TryGetValue(path, out var entry))
				{
					entry = new Entry();
					m_ByMaterial[path] = entry;
				}

				if (isScene)
				{
					entry.SceneSlots += Math.Max(0, rendererCounts[i]);
					entry.Scenes.Add(containerPath);
				}
				else
				{
					entry.PrefabSlots += Math.Max(0, rendererCounts[i]);
					entry.Prefabs.Add(containerPath);
				}
			}
		}

		public Counts For(string materialPath)
		{
			if (materialPath == null || !m_ByMaterial.TryGetValue(materialPath, out var entry))
				return default;

			return new Counts
			{
				PrefabSlots = entry.PrefabSlots,
				SceneSlots = entry.SceneSlots,
				Prefabs = entry.Prefabs.Count,
				Scenes = entry.Scenes.Count
			};
		}
	}
}

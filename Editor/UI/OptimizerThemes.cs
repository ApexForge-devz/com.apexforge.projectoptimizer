using System;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One selectable colour theme: a display name plus the USS asset that supplies its
	/// --po-* custom properties. See <see cref="OptimizerThemes"/> for the full list.</summary>
	public sealed class OptimizerTheme
	{
		public string Id;
		public string DisplayName;
		public string Description;

		/// <summary>
		/// Name searched for via <see cref="UnityEditor.AssetDatabase.FindAssets"/>, using the same
		/// by-name convention as the base stylesheet. Each theme is a standalone USS file.
		/// </summary>
		public string AssetName;
		public bool IsDark;
	}

	/// <summary>
	/// The tool's colour themes. There are exactly 50 selectable palettes: 25 Dark and 25 Light.
	/// Each theme owns a separate USS file containing only ProjectOptimizer's --po-* colour tokens,
	/// so changing themes never changes layout, spacing, sizing, or component structure.
	/// </summary>
	public static class OptimizerThemes
	{
		public const string DefaultIdDark = "obsidian";
		public const string DefaultIdLight = "daylight";

		public static readonly OptimizerTheme[] All =
		{
			new OptimizerTheme
			{
				Id = "obsidian",
				DisplayName = "Obsidian (Dark)",
				Description = "Near-black graphite panels with a refined violet-blue accent.",
				AssetName = "ProjectOptimizerTheme_Obsidian",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "midnight",
				DisplayName = "Midnight (Dark)",
				Description = "Deep navy surfaces with an electric cyan accent and a cold, technical edge.",
				AssetName = "ProjectOptimizerTheme_Midnight",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "nebula",
				DisplayName = "Nebula (Dark)",
				Description = "Deep violet space tones with a vivid magenta accent and a luminous secondary cyan.",
				AssetName = "ProjectOptimizerTheme_Nebula",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "forest",
				DisplayName = "Forest (Dark)",
				Description = "Muted pine-charcoal surfaces with a fresh mint accent, designed for long editor sessions.",
				AssetName = "ProjectOptimizerTheme_Forest",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "crimson",
				DisplayName = "Crimson (Dark)",
				Description = "Blackened wine surfaces with a decisive crimson accent and restrained rose highlights.",
				AssetName = "ProjectOptimizerTheme_Crimson",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "amethyst",
				DisplayName = "Amethyst (Dark)",
				Description = "Smoky plum-grey surfaces with a soft lavender accent for a more subdued violet look.",
				AssetName = "ProjectOptimizerTheme_Amethyst",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "ember",
				DisplayName = "Ember (Dark)",
				Description = "Warm charcoal-brown surfaces with an incandescent orange accent.",
				AssetName = "ProjectOptimizerTheme_Ember",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "slate",
				DisplayName = "Slate (Dark)",
				Description = "Cool steel-grey surfaces with a crisp blue accent and minimal saturation.",
				AssetName = "ProjectOptimizerTheme_Slate",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "terminal",
				DisplayName = "Terminal (Dark)",
				Description = "Near-black green-black surfaces with phosphor green and restrained amber indicators.",
				AssetName = "ProjectOptimizerTheme_Terminal",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "volt",
				DisplayName = "Volt (Dark)",
				Description = "Deep mineral-black surfaces with a neon lime accent, built for maximum visual energy.",
				AssetName = "ProjectOptimizerTheme_Volt",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "aurora",
				DisplayName = "Aurora (Dark)",
				Description = "Charcoal teal surfaces with a shifting green-violet character anchored by an aurora mint.",
				AssetName = "ProjectOptimizerTheme_Aurora",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "cobalt",
				DisplayName = "Cobalt (Dark)",
				Description = "Ink-blue panels with a saturated cobalt accent and cool periwinkle support.",
				AssetName = "ProjectOptimizerTheme_Cobalt",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "rosewood",
				DisplayName = "Rosewood (Dark)",
				Description = "Black cherry-brown surfaces with a wine-red accent and dusty blush secondary.",
				AssetName = "ProjectOptimizerTheme_Rosewood",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "royal",
				DisplayName = "Royal (Dark)",
				Description = "Rich blue-plum surfaces with a regal sapphire accent and soft gold highlights.",
				AssetName = "ProjectOptimizerTheme_Royal",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "moss",
				DisplayName = "Moss (Dark)",
				Description = "Earthy green-black surfaces with a muted moss accent and warm sandstone secondary.",
				AssetName = "ProjectOptimizerTheme_Moss",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "oceanic",
				DisplayName = "Oceanic (Dark)",
				Description = "Deep petrol surfaces with saturated turquoise accents and a clean sea-blue secondary.",
				AssetName = "ProjectOptimizerTheme_Oceanic",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "inferno",
				DisplayName = "Inferno (Dark)",
				Description = "Charred red-black surfaces with a hot vermilion accent and molten gold secondary.",
				AssetName = "ProjectOptimizerTheme_Inferno",
				IsDark = true
			},
			new OptimizerTheme
			{
				Id = "daylight",
				DisplayName = "Daylight (Light)",
				Description = "Clean cool-neutral surfaces with a confident violet-blue accent.",
				AssetName = "ProjectOptimizerTheme_Daylight",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "sandstone",
				DisplayName = "Sandstone (Light)",
				Description = "Warm mineral-paper surfaces with an amber accent and earthy secondary tones.",
				AssetName = "ProjectOptimizerTheme_Sandstone",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "glacier",
				DisplayName = "Glacier (Light)",
				Description = "Icy blue-white surfaces with a clear alpine blue accent and frosted teal support.",
				AssetName = "ProjectOptimizerTheme_Glacier",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "rose",
				DisplayName = "Rose (Light)",
				Description = "Soft blush-white surfaces with an elegant rose accent and muted plum support.",
				AssetName = "ProjectOptimizerTheme_Rose",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "arctic",
				DisplayName = "Arctic (Light)",
				Description = "Pale blue-grey surfaces with a restrained teal accent for the quietest light appearance.",
				AssetName = "ProjectOptimizerTheme_Arctic",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "porcelain",
				DisplayName = "Porcelain (Light)",
				Description = "Cool porcelain surfaces with deep sapphire accents and a clean ceramic-blue secondary.",
				AssetName = "ProjectOptimizerTheme_Porcelain",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "lavender",
				DisplayName = "Lavender (Light)",
				Description = "Pale lavender-grey surfaces with a sophisticated purple accent and blue-violet support.",
				AssetName = "ProjectOptimizerTheme_Lavender",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "peach",
				DisplayName = "Peach (Light)",
				Description = "Soft apricot surfaces with a coral accent balanced by a muted teal secondary.",
				AssetName = "ProjectOptimizerTheme_Peach",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "sky",
				DisplayName = "Sky (Light)",
				Description = "Open airy blue surfaces with a vivid azure accent and cool indigo secondary.",
				AssetName = "ProjectOptimizerTheme_Sky",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "sage",
				DisplayName = "Sage (Light)",
				Description = "Quiet grey-green surfaces with a grounded sage accent and eucalyptus support.",
				AssetName = "ProjectOptimizerTheme_Sage",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "ivory",
				DisplayName = "Ivory (Light)",
				Description = "Classic warm ivory surfaces with dark olive accents and a restrained copper secondary.",
				AssetName = "ProjectOptimizerTheme_Ivory",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "aqua",
				DisplayName = "Aqua (Light)",
				Description = "Bright sea-glass surfaces with a saturated turquoise accent and marine-blue secondary.",
				AssetName = "ProjectOptimizerTheme_Aqua",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "butter",
				DisplayName = "Butter (Light)",
				Description = "Creamy yellow-white surfaces with a golden accent and cool slate secondary.",
				AssetName = "ProjectOptimizerTheme_Butter",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "parchment",
				DisplayName = "Parchment (Light)",
				Description = "Old-paper cream surfaces with a sepia accent and restrained forest-green support.",
				AssetName = "ProjectOptimizerTheme_Parchment",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "blossom",
				DisplayName = "Blossom (Light)",
				Description = "Powdered pink surfaces with a berry accent and cool periwinkle support.",
				AssetName = "ProjectOptimizerTheme_Blossom",
				IsDark = false
			},
			new OptimizerTheme
			{
				Id = "silver",
				DisplayName = "Silver (Light)",
				Description = "Neutral silver surfaces with a crisp steel accent and cool violet support.",
				AssetName = "ProjectOptimizerTheme_Silver",
				IsDark = false
			},
		};

		/// <summary>Raised after a selection is persisted, so every open window can re-apply it.</summary>
		public static event Action<string> Changed;

		/// <summary>Looks up a theme by id, falling back to <see cref="All"/>[0] for an unknown or empty one.</summary>
		public static OptimizerTheme Get(string id)
		{
			if (!string.IsNullOrEmpty(id))
			{
				for (int i = 0; i < All.Length; i++)
					if (All[i].Id == id) return All[i];
			}
			return All[0];
		}

		/// <summary>Persists the choice to the window state and notifies listeners. A no-op if unchanged.</summary>
		public static void Select(string id)
		{
			var theme = Get(id);
			var state = OptimizerWindowState.instance;
			if (state.ThemeId == theme.Id) return;

			state.ThemeId = theme.Id;
			state.Persist();
			Changed?.Invoke(theme.Id);
		}

		/// <summary>
		/// The id one step away from <paramref name="currentId"/> in <see cref="All"/>, wrapping
		/// around at either end. Direction is normally -1 or 1, but any step size wraps correctly.
		/// </summary>
		public static string Cycle(string currentId, int direction)
		{
			int index = Array.IndexOf(All, Get(currentId));
			if (index < 0) index = 0;

			int count = All.Length;
			int next = ((index + direction) % count + count) % count;
			return All[next].Id;
		}
	}
}

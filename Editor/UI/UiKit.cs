using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Small factory helpers so the pages stay readable.</summary>
	public static class UiKit
	{
		public static VisualElement Row(params string[] classes)
		{
			var element = new VisualElement();
			element.style.flexDirection = FlexDirection.Row;
			AddClasses(element, classes);
			return element;
		}

		public static VisualElement Column(params string[] classes)
		{
			var element = new VisualElement();
			AddClasses(element, classes);
			return element;
		}

		public static Label Text(string text, params string[] classes)
		{
			var label = new Label(text);
			AddClasses(label, classes);
			return label;
		}

		public static Button Button(string text, Action onClick, params string[] classes)
		{
			// No child elements here, ever: a Button/TextElement only gets Unity's automatic
			// text-based width from Yoga while it is a leaf node. The moment it has any child - even
			// one that is position: absolute and otherwise invisible - Yoga stops calling its text
			// measure function and it collapses to just its padding and border. A decorative hover-shine
			// child did exactly this to every button in the tool once already - do not add one back.
			var button = new Button(onClick) { text = text };
			button.AddToClassList("po-button");
			AddClasses(button, classes);
			return button;
		}

		public static VisualElement Spacer()
		{
			var element = new VisualElement();
			element.AddToClassList("po-spacer");
			return element;
		}

		public static Label Pill(string text, string severityClass)
		{
			var label = new Label(text);
			label.AddToClassList("po-pill");
			if (!string.IsNullOrEmpty(severityClass))
				label.AddToClassList(severityClass);
			return label;
		}

		public static VisualElement Stat(string label, string value, string dotClass)
		{
			var row = Row("po-stat");
			var dot = new VisualElement();
			dot.AddToClassList("po-stat-dot");
			if (!string.IsNullOrEmpty(dotClass))
				dot.AddToClassList(dotClass);
			row.Add(dot);
			row.Add(Text(value, "po-stat-value"));
			row.Add(Text(label, "po-stat-label"));
			return row;
		}

		/// <summary>
		/// Counts a label's text from 0 up to <paramref name="to"/> over <paramref name="durationMs"/>,
		/// eased out so it settles rather than stopping dead. <paramref name="format"/> turns the
		/// in-flight number into the same text the final value would show - a byte count and a plain
		/// integer both count up the same way, they just render differently.
		/// <para>
		/// Ticks on the label's own scheduler, so it stops cleanly if the label is torn down mid-count
		/// (the scheduler belongs to the panel the label is attached to, and a detached element simply
		/// never gets another callback).
		/// </para>
		/// </summary>
		public static void AnimateCountUp(Label label, long to, Func<long, string> format, int durationMs = 550)
		{
			if (label == null || format == null) return;

			if (to == 0 || durationMs <= 0)
			{
				label.text = format(to);
				return;
			}

			double start = EditorApplication.timeSinceStartup;

			void Tick()
			{
				double elapsedMs = (EditorApplication.timeSinceStartup - start) * 1000.0;
				double t = elapsedMs / durationMs;
				if (t >= 1.0)
				{
					label.text = format(to);
					return;
				}

				// Ease-out cubic: fast start, settles gently rather than arriving at full speed.
				double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
				label.text = format((long)Math.Round(to * eased));
				label.schedule.Execute(Tick).StartingIn(16);
			}
			Tick();
		}

		public static VisualElement Meta(string label, string value, string tooltip = null)
		{
			var column = Column("po-meta");
			column.Add(Text(label.ToUpperInvariant(), "po-meta-label"));
			column.Add(Text(value, "po-meta-value"));
			if (!string.IsNullOrEmpty(tooltip)) column.tooltip = tooltip;
			return column;
		}

		public static VisualElement Tile(string label, string value, string sub, string valueClass)
		{
			var tile = Column("po-tile");
			tile.Add(Text(label.ToUpperInvariant(), "po-tile-label"));
			var valueLabel = Text(value, "po-tile-value");
			if (!string.IsNullOrEmpty(valueClass))
				valueLabel.AddToClassList(valueClass);
			tile.Add(valueLabel);
			if (!string.IsNullOrEmpty(sub))
				tile.Add(Text(sub, "po-tile-sub"));
			return tile;
		}

		/// <summary>A toggle-styled button that reports its on/off state.</summary>
		public sealed class Chip : Button
		{
			bool m_On;
			public event Action<bool> Changed;

			public Chip(string text, bool on, string severityClass = "po-chip-neutral")
			{
				this.text = text;
				AddToClassList("po-chip");
				if (!string.IsNullOrEmpty(severityClass))
					AddToClassList(severityClass);
				clicked += () =>
				{
					On = !On;
					Changed?.Invoke(m_On);
				};
				On = on;
			}

			public bool On
			{
				get => m_On;
				set
				{
					m_On = value;
					EnableInClassList("po-chip-on", value);
				}
			}
		}

		public static VisualElement Bar(float fill01, string severityClass)
		{
			var track = new VisualElement();
			track.AddToClassList("po-bar-track");
			var fill = new VisualElement();
			fill.AddToClassList("po-bar-fill");
			if (!string.IsNullOrEmpty(severityClass))
				fill.AddToClassList(severityClass);
			fill.style.width = Length.Percent(Mathf.Clamp01(fill01) * 100f);
			track.Add(fill);
			return track;
		}

		public static VisualElement Empty(string title, string subtitle)
		{
			var column = Column("po-empty");
			column.Add(Text(title, "po-empty-title"));
			column.Add(Text(subtitle, "po-empty-sub"));
			return column;
		}

		public static Image Icon(Texture texture, float size, params string[] classes)
		{
			var image = new Image { image = texture };
			image.style.width = size;
			image.style.height = size;
			image.style.flexShrink = 0;
			AddClasses(image, classes);
			return image;
		}

		public static PopupField<string> Dropdown(
			string label,
			List<string> choices,
			string value,
			Action<string> onChange
		)
		{
			var field = new PopupField<string>(
				label,
				choices,
				Mathf.Max(0, choices.IndexOf(value))
			);
			field.AddToClassList("po-dropdown");
			field.RegisterValueChangedCallback(evt => onChange(evt.newValue));
			return field;
		}

		/// <summary>
		/// The target-profile selector, shared by the toolbar and the Rules tab. Uses the custom
		/// dropdown because the profile names contain "/", which Unity's PopupField would turn
		/// into submenus.
		/// </summary>
		public static FilterDropdown PlatformSelector(Action onChanged)
		{
			var settings = OptimizerSettings.Instance;
			var dropdown = new FilterDropdown(
				"Target platform",
				"Hardware class the recommendations target."
			)
			{
				ShowAllEntry = false,
			};

			var root = new FilterDropdown.Node();
			foreach (var profile in PlatformProfile.All)
			{
				root.Add(
					new FilterDropdown.Node
					{
						Id = ((int)profile.Platform).ToString(),
						Label = profile.DisplayName,
						ButtonLabel = profile.DisplayName,
						Tooltip = profile.Description,
					}
				);
			}
			dropdown.SetTree(root);
			dropdown.SetValue(((int)settings.targetPlatform).ToString(), false);

			dropdown.ValueChanged += value =>
			{
				if (!int.TryParse(value, out int index))
					return;
				settings.targetPlatform = (OptimizerPlatform)index;
				settings.Save();
				OfferRecommendedBudgets();
				onChanged?.Invoke();
			};
			return dropdown;
		}

		/// <summary>
		/// Budgets are the user's to tune, so switching profile asks before overwriting them
		/// instead of silently resetting whatever they had set.
		/// </summary>
		public static void OfferRecommendedBudgets()
		{
			var settings = OptimizerSettings.Instance;
			if (settings.MatchesRecommendedBudgets())
				return;

			var profile = settings.Profile;
			bool apply = EditorUtility.DisplayDialog(
				"Apply recommended budgets to rules list?",
				profile.DisplayName
					+ "\n\n"
					+ profile.Description
					+ "\n\n"
					+ "Recommended budgets:\n"
					+ "  Textures "
					+ profile.MaxTextureSize
					+ ", UI "
					+ profile.MaxUiTextureSize
					+ "\n"
					+ "  Meshes warn "
					+ profile.MeshVertexWarn
					+ ", critical "
					+ profile.MeshVertexCritical
					+ "\n"
					+ "  Stream audio over "
					+ profile.AudioStreamingSeconds
					+ " s, effects at "
					+ profile.AudioSfxSampleRate
					+ " Hz\n"
					+ "  At most "
					+ profile.RealtimeLightWarn
					+ " realtime shadow-casting lights\n\n"
					+ "Apply these, or keep your current thresholds?",
				"Apply recommended",
				"Keep mine"
			);
			if (apply)
				settings.ApplyRecommendedBudgets();
		}

		static void AddClasses(VisualElement element, string[] classes)
		{
			if (classes == null)
				return;
			for (int i = 0; i < classes.Length; i++)
				if (!string.IsNullOrEmpty(classes[i]))
					element.AddToClassList(classes[i]);
		}
	}
}

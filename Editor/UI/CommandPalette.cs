using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>One entry in the command palette: what it says, what happens when it runs.</summary>
	public sealed class PaletteCommand
	{
		public string Title;
		public string Subtitle;
		public string Category;
		public Action Execute;
	}

	/// <summary>
	/// A fuzzy-searchable command launcher (Ctrl+K / Cmd+K) that overlays the whole window - jump to
	/// any tab, run a scan, export, revert, switch theme, all from one place instead of hunting
	/// through the nav rail and two settings pages.
	/// <para>
	/// Built once per <see cref="ProjectOptimizerWindow"/> and toggled open/closed rather than
	/// recreated each time, so it keeps no state worth losing between uses. The command list itself is
	/// supplied fresh on every open (see <c>ProjectOptimizerWindow.BuildPaletteCommands</c>), because
	/// which commands make sense - a scan running or not, a revert available or not - changes
	/// constantly and a stale list would offer actions that no longer apply.
	/// </para>
	/// </summary>
	public sealed class CommandPalette : VisualElement
	{
		readonly TextField m_Search;
		readonly ScrollView m_List;

		List<PaletteCommand> m_Commands = new List<PaletteCommand>();
		readonly List<PaletteCommand> m_Filtered = new List<PaletteCommand>();
		int m_SelectedIndex;

		public bool IsOpen => style.display == DisplayStyle.Flex;

		public CommandPalette()
		{
			AddToClassList("po-palette-backdrop");
			style.display = DisplayStyle.None;

			// Clicking the dimmed backdrop closes it, same as the cascading dropdown's own backdrop;
			// clicking the card itself must not, so that click is stopped before it reaches here.
			RegisterCallback<ClickEvent>(_ => Close());

			var card = UiKit.Column("po-palette-card");
			card.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
			Add(card);

			m_Search = new TextField();
			m_Search.AddToClassList("po-palette-search");
			m_Search.textEdition.placeholder = "Type a command or search...";
			m_Search.RegisterValueChangedCallback(_ => Refilter());
			card.Add(m_Search);

			m_List = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			m_List.AddToClassList("po-palette-list");
			card.Add(m_List);

			// Trickle-down so arrow keys/Enter/Escape are caught here first, before the focused search
			// field would otherwise treat them as ordinary text-editing keys.
			RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
		}

		public void Open(List<PaletteCommand> commands)
		{
			m_Commands = commands ?? new List<PaletteCommand>();
			m_Search.SetValueWithoutNotify(string.Empty);
			style.display = DisplayStyle.Flex;
			style.opacity = 0f;
			style.translate = new Translate(0f, -8f);
			Refilter();

			schedule.Execute(() =>
			{
				style.opacity = 1f;
				style.translate = new Translate(0f, 0f);
				m_Search.Focus();
			}).StartingIn(0);
		}

		public void Close()
		{
			Blur();
			style.opacity = 0f;
			style.translate = new Translate(0f, -8f);
			schedule.Execute(() => style.display = DisplayStyle.None).StartingIn(130);
		}

		void Refilter()
		{
			var query = m_Search.value.Trim();
			bool searching = !string.IsNullOrEmpty(query);

			m_Filtered.Clear();
			if (!searching)
			{
				m_Filtered.AddRange(m_Commands);
			}
			else
			{
				var scored = new List<KeyValuePair<int, PaletteCommand>>();
				foreach (var command in m_Commands)
				{
					var candidates = new List<string> { command.Title };
					if (!string.IsNullOrEmpty(command.Category)) candidates.Add(command.Category);
					if (!string.IsNullOrEmpty(command.Subtitle)) candidates.Add(command.Subtitle);

					var best = FuzzyMatch.BestScore(query, candidates);
					if (best == null) continue;
					scored.Add(new KeyValuePair<int, PaletteCommand>(best.Value, command));
				}
				scored.Sort((a, b) => b.Key.CompareTo(a.Key));
				foreach (var pair in scored) m_Filtered.Add(pair.Value);
			}

			m_SelectedIndex = 0;
			Rebuild();
		}

		void Rebuild()
		{
			m_List.Clear();
			if (m_Filtered.Count == 0)
			{
				m_List.Add(UiKit.Empty("No matching commands", "Try a different search term."));
				return;
			}

			for (int i = 0; i < m_Filtered.Count; i++)
			{
				var command = m_Filtered[i];
				var row = UiKit.Row("po-palette-item");
				row.EnableInClassList("po-palette-item-selected", i == m_SelectedIndex);

				var text = UiKit.Column();
				text.style.flexGrow = 1f;
				text.style.flexShrink = 1f;
				text.style.minWidth = 0f;
				text.Add(UiKit.Text(command.Title, "po-palette-item-title"));
				if (!string.IsNullOrEmpty(command.Subtitle))
					text.Add(UiKit.Text(command.Subtitle, "po-palette-item-subtitle"));
				row.Add(text);

				if (!string.IsNullOrEmpty(command.Category))
					row.Add(UiKit.Text(command.Category, "po-palette-item-category"));

				var index = i;
				row.RegisterCallback<ClickEvent>(_ => RunAt(index));
				m_List.Add(row);
			}
		}

		void OnKeyDown(KeyDownEvent evt)
		{
			switch (evt.keyCode)
			{
				case KeyCode.Escape:
					Close();
					evt.StopPropagation();
					break;

				case KeyCode.DownArrow:
					if (m_Filtered.Count > 0)
					{
						m_SelectedIndex = Mathf.Min(m_SelectedIndex + 1, m_Filtered.Count - 1);
						Rebuild();
					}
					evt.StopPropagation();
					break;

				case KeyCode.UpArrow:
					if (m_Filtered.Count > 0)
					{
						m_SelectedIndex = Mathf.Max(m_SelectedIndex - 1, 0);
						Rebuild();
					}
					evt.StopPropagation();
					break;

				case KeyCode.Return:
				case KeyCode.KeypadEnter:
					RunAt(m_SelectedIndex);
					evt.StopPropagation();
					break;
			}
		}

		void RunAt(int index)
		{
			if (index < 0 || index >= m_Filtered.Count) return;
			var command = m_Filtered[index];

			// Closed before running rather than after: a command that opens a dialog (export, revert)
			// must not have the palette still sitting on top of it, and one that throws must not leave
			// the palette stuck open over a window that is otherwise fine.
			Close();
			command.Execute?.Invoke();
		}
	}
}

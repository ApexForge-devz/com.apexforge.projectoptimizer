using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// A cascading dropdown built out of UI Toolkit elements.
	/// <para>
	/// Unity's own PopupField splits its labels on "/" to build submenus, which makes a parent
	/// folder unselectable: clicking "Assets/Art" only opens its children. Here every node is
	/// clickable on its own, and hovering one opens its children in a column to the right.
	/// </para>
	/// </summary>
	public sealed class FilterDropdown : VisualElement
	{
		public sealed class Node
		{
			public string Id;
			public string Label;
			/// <summary>Shown on the closed button when this node is selected. Defaults to the id.</summary>
			public string ButtonLabel;
			public string Count;
			public string PillClass;
			public string Tooltip;
			public readonly List<Node> Children = new List<Node>();

			public Node Add(Node child)
			{
				Children.Add(child);
				return child;
			}
		}

		const float k_MinColumnWidth = 190f;
		const float k_MaxColumnWidth = 560f;
		const float k_ItemHeight = 24f;
		const float k_MaxColumnHeight = 380f;
		const float k_ScreenMargin = 6f;
		/// <summary>How far a child column overlaps its parent, so the two read as one menu.</summary>
		const float k_CascadeOverlap = 4f;

		readonly Label m_ButtonLabel;
		readonly VisualElement m_Button;
		readonly string m_AllLabel;

		Node m_Root = new Node();
		readonly Dictionary<string, string> m_Labels = new Dictionary<string, string>(StringComparer.Ordinal);

		VisualElement m_Backdrop;
		readonly List<VisualElement> m_Columns = new List<VisualElement>();
		Label m_Measurer;

		public string Value { get; private set; }

		/// <summary>Whether the first column offers a "clear the filter" row. Off for plain selectors.</summary>
		public bool ShowAllEntry { get; set; } = true;

		public event Action<string> ValueChanged;

		public FilterDropdown(string allLabel, string tooltipText = null)
		{
			m_AllLabel = allLabel;
			Value = null;

			m_Button = UiKit.Row("po-dd-button");
			m_ButtonLabel = UiKit.Text(allLabel, "po-dd-button-label");
			m_Button.Add(m_ButtonLabel);
			m_Button.Add(UiKit.Text("\u25be", "po-dd-button-caret"));
			m_Button.RegisterCallback<PointerDownEvent>(evt =>
			{
				evt.StopPropagation();
				Toggle();
			});
			if (!string.IsNullOrEmpty(tooltipText)) m_Button.tooltip = tooltipText;

			style.flexDirection = FlexDirection.Row;
			Add(m_Button);

			// Sits in the panel purely so text can be measured against the real font.
			m_Measurer = new Label { pickingMode = PickingMode.Ignore };
			m_Measurer.style.position = Position.Absolute;
			m_Measurer.style.visibility = Visibility.Hidden;
			Add(m_Measurer);
		}

		/// <summary>Width of a string at a given font size, falling back to a character estimate.</summary>
		float TextWidth(string text, float fontSize)
		{
			if (string.IsNullOrEmpty(text)) return 0f;
			if (m_Measurer != null && m_Measurer.panel != null)
			{
				m_Measurer.style.fontSize = fontSize;
				var size = m_Measurer.MeasureTextSize(text, 0f, MeasureMode.Undefined, 0f, MeasureMode.Undefined);
				if (size.x > 1f) return size.x;
			}
			return text.Length * fontSize * 0.56f;
		}

		/// <summary>
		/// Widest row in this column, so the menu fits its content instead of truncating every
		/// label to a fixed width.
		/// </summary>
		float MeasureColumnWidth(Node parentNode, bool includeAllEntry)
		{
			float widest = includeAllEntry ? TextWidth(m_AllLabel, 12f) : 0f;
			bool anyChildren = false;
			bool anyPill = false;

			foreach (var child in parentNode.Children)
			{
				float row = TextWidth(child.Label, 12f);
				if (!string.IsNullOrEmpty(child.Count)) row += TextWidth(child.Count, 10f) + 8f;
				widest = Mathf.Max(widest, row);
				if (child.Children.Count > 0) anyChildren = true;
				if (!string.IsNullOrEmpty(child.PillClass)) anyPill = true;
			}

			// tick column + optional severity dot + row padding and margins, then the submenu arrow.
			float chrome = 14f + 20f + 8f + (anyPill ? 16f : 0f) + (anyChildren ? 18f : 0f);

			// Leave room for the scrollbar when the column will actually scroll.
			int rows = parentNode.Children.Count + (includeAllEntry ? 1 : 0);
			if (rows * k_ItemHeight + 16f > k_MaxColumnHeight) chrome += 14f;

			return Mathf.Clamp(Mathf.Ceil(widest + chrome), k_MinColumnWidth, k_MaxColumnWidth);
		}

		// --- data -------------------------------------------------------------------

		/// <summary>Replaces the tree. <paramref name="root"/>'s children become the first column.</summary>
		public void SetTree(Node root)
		{
			Close();
			m_Root = root ?? new Node();
			m_Labels.Clear();
			IndexLabels(m_Root);

			// Drop a selection that no longer exists in the new data.
			if (Value != null && !m_Labels.ContainsKey(Value)) Value = null;
			RefreshButton();
		}

		void IndexLabels(Node node)
		{
			foreach (var child in node.Children)
			{
				if (!string.IsNullOrEmpty(child.Id)) m_Labels[child.Id] = child.ButtonLabel ?? child.Id;
				IndexLabels(child);
			}
		}

		public void SetValue(string id, bool notify)
		{
			Value = string.IsNullOrEmpty(id) ? null : id;
			RefreshButton();
			if (notify) ValueChanged?.Invoke(Value);
		}

		void RefreshButton()
		{
			string label = m_AllLabel;
			if (Value != null && m_Labels.TryGetValue(Value, out var friendly)) label = friendly;
			else if (Value != null) label = Value;

			m_ButtonLabel.text = label;
			m_Button.tooltip = label;
		}

		// --- menu -------------------------------------------------------------------

		void Toggle()
		{
			if (m_Backdrop != null) Close();
			else Open();
		}

		VisualElement OverlayRoot()
		{
			var current = parent;
			while (current != null)
			{
				if (current.ClassListContains("po-root")) return current;
				current = current.parent;
			}
			return panel?.visualTree ?? this;
		}

		void Open()
		{
			var overlay = OverlayRoot();
			if (overlay == null) return;

			m_Backdrop = new VisualElement();
			m_Backdrop.AddToClassList("po-menu-backdrop");
			m_Backdrop.RegisterCallback<PointerDownEvent>(_ => Close());
			overlay.Add(m_Backdrop);
			m_Button.AddToClassList("po-dd-open");

			var anchor = overlay.WorldToLocal(m_Button.worldBound);
			OpenColumn(0, m_Root, anchor, anchor.yMax + 2f, true, ShowAllEntry);
		}

		public void Close()
		{
			if (m_Backdrop != null)
			{
				m_Backdrop.RemoveFromHierarchy();
				m_Backdrop = null;
			}
			m_Columns.Clear();
			m_Button.RemoveFromClassList("po-dd-open");
		}

		void CloseColumnsFrom(int level)
		{
			for (int i = m_Columns.Count - 1; i >= level; i--)
			{
				m_Columns[i].RemoveFromHierarchy();
				m_Columns.RemoveAt(i);
			}
		}

		/// <summary>
		/// Places a column. <paramref name="anchor"/> is the button for the first column and the
		/// parent column for the rest.
		/// </summary>
		void OpenColumn(int level, Node parentNode, Rect anchor, float top, bool isRoot, bool includeAllEntry)
		{
			CloseColumnsFrom(level);
			if (parentNode.Children.Count == 0) return;

			var overlay = OverlayRoot();
			var column = new VisualElement();
			column.AddToClassList("po-menu-column");
			// Clicks inside the menu must not reach the backdrop, which closes it.
			column.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

			var scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.Auto };
			scroll.AddToClassList("po-menu-scroll");
			column.Add(scroll);

			int rows = parentNode.Children.Count + (includeAllEntry ? 1 : 0);
			if (includeAllEntry)
			{
				scroll.Add(BuildItem(new Node { Id = null, Label = m_AllLabel }, level, false));
				var separator = new VisualElement();
				separator.AddToClassList("po-menu-separator");
				scroll.Add(separator);
			}

			foreach (var child in parentNode.Children)
				scroll.Add(BuildItem(child, level, true));

			float width = MeasureColumnWidth(parentNode, includeAllEntry);
			float available = overlay.layout.width;
			float left;

			if (isRoot)
			{
				left = anchor.xMin;
			}
			else
			{
				// Cascade to the right while there is room; otherwise mirror to the left of the
				// parent. Without the flip, every column past the screen edge would be clamped to
				// the same x and stack on top of its parent.
				float toTheRight = anchor.xMax - k_CascadeOverlap;
				float toTheLeft = anchor.xMin - width + k_CascadeOverlap;

				if (toTheRight + width <= available - k_ScreenMargin) left = toTheRight;
				else if (toTheLeft >= k_ScreenMargin) left = toTheLeft;
				else left = Mathf.Max(k_ScreenMargin, available - width - k_ScreenMargin);
			}

			float estimatedHeight = Mathf.Min(k_MaxColumnHeight, rows * k_ItemHeight + 16f);
			float maxLeft = Mathf.Max(k_ScreenMargin, available - width - k_ScreenMargin);
			float maxTop = Mathf.Max(4f, overlay.layout.height - estimatedHeight - k_ScreenMargin);

			column.style.width = width;
			column.style.left = Mathf.Clamp(left, k_ScreenMargin, maxLeft);
			column.style.top = Mathf.Clamp(top, 4f, maxTop);

			m_Backdrop.Add(column);
			m_Columns.Add(column);
		}

		VisualElement BuildItem(Node node, int level, bool realNode)
		{
			var item = UiKit.Row("po-menu-item");

			bool selected = realNode ? node.Id == Value : Value == null;
			item.Add(UiKit.Text(selected ? "\u2713" : string.Empty, "po-menu-tick"));
			if (selected) item.AddToClassList("po-menu-item-current");

			if (!string.IsNullOrEmpty(node.PillClass))
			{
				var pill = UiKit.Pill(string.Empty, node.PillClass);
				pill.style.width = 10f;
				pill.style.height = 10f;
				pill.style.marginRight = 6f;
				item.Add(pill);
			}

			var label = UiKit.Text(node.Label, "po-menu-label");
			if (!string.IsNullOrEmpty(node.Tooltip)) item.tooltip = node.Tooltip;
			item.Add(label);
			if (!string.IsNullOrEmpty(node.Count)) item.Add(UiKit.Text(node.Count, "po-menu-count"));

			bool hasChildren = node.Children.Count > 0;
			item.Add(UiKit.Text(hasChildren ? "\u25b8" : string.Empty, "po-menu-arrow"));

			// Hovering opens the children; clicking picks this node itself.
			item.RegisterCallback<PointerEnterEvent>(_ =>
			{
				foreach (var sibling in item.parent.Children())
					sibling.RemoveFromClassList("po-menu-item-open");

				if (!hasChildren)
				{
					CloseColumnsFrom(level + 1);
					return;
				}

				item.AddToClassList("po-menu-item-open");
				var overlay = OverlayRoot();
				var itemRect = overlay.WorldToLocal(item.worldBound);
				var columnRect = overlay.WorldToLocal(m_Columns[level].worldBound);
				OpenColumn(level + 1, node, columnRect, itemRect.yMin - 5f, false, false);
			});

			item.RegisterCallback<PointerUpEvent>(evt =>
			{
				evt.StopPropagation();
				SetValue(node.Id, true);
				Close();
			});

			return item;
		}
	}
}

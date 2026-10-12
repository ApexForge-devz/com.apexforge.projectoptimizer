using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// "Review selected..." - the first step of a Review Batch.
	///
	/// This window deliberately owns its visual layout instead of depending on the shared
	/// option-card styling used by the other dialogs. The important rule here is:
	/// no horizontal competition between the action title and its explanation.
	/// Everything except the badges/metadata is allowed to wrap naturally.
	///
	/// Nothing is changed by this window. The user chooses one action and the existing
	/// ReviewFixDialog performs the actual confirmation/transaction.
	/// </summary>
	public sealed class ReviewActionPickerDialog : EditorWindow
	{
		List<AssetEntry> m_Selection;
		Action<IReadOnlyList<string>> m_AfterFix;
		List<ReviewActionDescriptor> m_Actions;

		const float k_WindowWidth = 680f;
		const float k_WindowHeight = 620f;
		const float k_MinWindowWidth = 500f;
		const float k_MinWindowHeight = 360f;

		public static void Open(List<AssetEntry> selection, Action<IReadOnlyList<string>> afterFix)
		{
			if (selection == null || selection.Count == 0)
				return;

			var actions = ReviewActionCatalog.DetectCommonActions(selection);

			if (actions.Count == 0)
			{
				Feedback.Warn(
					"None of the "
						+ selection.Count
						+ " selected assets currently offer a Review Fix."
				);
				return;
			}

			var window = CreateInstance<ReviewActionPickerDialog>();

			window.m_Selection = selection;
			window.m_AfterFix = afterFix;
			window.m_Actions = actions;

			window.titleContent = new GUIContent("Review selected (" + selection.Count + ")");

			window.minSize = new Vector2(k_MinWindowWidth, k_MinWindowHeight);
			window.maxSize = new Vector2(1100f, 1100f);
			window.position = new Rect(
				window.position.x,
				window.position.y,
				k_WindowWidth,
				k_WindowHeight
			);

			window.Build();
			window.ShowUtility();
		}

		void Build()
		{
			var root = rootVisualElement;

			// This dialog intentionally does not load the shared stylesheet.
			// Its layout is entirely explicit so changes elsewhere in the project
			// cannot make these cards overflow horizontally.
			root.Clear();

			root.style.flexDirection = FlexDirection.Column;
			root.style.flexGrow = 1f;
			root.style.paddingLeft = 14f;
			root.style.paddingRight = 14f;
			root.style.paddingTop = 12f;
			root.style.paddingBottom = 12f;
			root.style.backgroundColor = new Color(0.115f, 0.12f, 0.135f);

			var header = new VisualElement();
			header.style.flexShrink = 0f;
			header.style.marginBottom = 10f;
			root.Add(header);

			var titleRow = new VisualElement();
			titleRow.style.flexDirection = FlexDirection.Row;
			titleRow.style.alignItems = Align.Center;
			header.Add(titleRow);

			var title = new Label(m_Selection.Count + " assets selected");
			title.style.flexGrow = 1f;
			title.style.flexShrink = 1f;
			title.style.minWidth = 0f;
			title.style.unityFontStyleAndWeight = FontStyle.Bold;
			title.style.fontSize = 18f;
			title.style.color = new Color(0.93f, 0.94f, 0.96f);
			titleRow.Add(title);

			var countBadge = MakeBadge(
				m_Actions.Count + " actions",
				new Color(0.12f, 0.42f, 0.50f),
				new Color(0.75f, 0.94f, 0.97f)
			);

			countBadge.style.marginLeft = 10f;
			titleRow.Add(countBadge);

			var hint = new Label(
				"Choose one Review Fix action. Nothing changes yet. "
					+ "The next screen shows exactly how many selected assets are eligible, "
					+ "lets you inspect them, and then uses the same backup / verify / rollback "
					+ "transaction as every other fix."
			);

			hint.style.marginTop = 5f;
			hint.style.fontSize = 11f;
			hint.style.color = new Color(0.62f, 0.65f, 0.69f);
			hint.style.whiteSpace = WhiteSpace.Normal;
			hint.style.flexShrink = 1f;
			hint.style.minWidth = 0f;
			header.Add(hint);

			var divider = new VisualElement();
			divider.style.height = 1f;
			divider.style.backgroundColor = new Color(0.22f, 0.235f, 0.255f);
			divider.style.marginTop = 11f;
			header.Add(divider);

			var scroll = new ScrollView();

			scroll.style.flexGrow = 1f;
			scroll.style.flexShrink = 1f;
			scroll.style.minWidth = 0f;
			scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
			scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;

			root.Add(scroll);

			var content = new VisualElement();
			content.style.flexDirection = FlexDirection.Column;
			content.style.flexGrow = 1f;
			content.style.flexShrink = 1f;
			content.style.minWidth = 0f;
			content.style.width = Length.Percent(100f);
			content.style.paddingRight = 3f;
			scroll.Add(content);

			for (int i = 0; i < m_Actions.Count; i++)
				content.Add(BuildActionCard(m_Actions[i], i));
		}

		VisualElement BuildActionCard(ReviewActionDescriptor action, int index)
		{
			var card = new VisualElement();

			card.style.flexDirection = FlexDirection.Column;
			card.style.flexGrow = 0f;
			card.style.flexShrink = 1f;
			card.style.minWidth = 0f;
			card.style.width = Length.Percent(100f);

			card.style.backgroundColor = new Color(0.16f, 0.17f, 0.19f);

			card.style.borderTopWidth = 1f;
			card.style.borderBottomWidth = 1f;
			card.style.borderLeftWidth = 1f;
			card.style.borderRightWidth = 1f;

			card.style.borderTopColor = new Color(0.235f, 0.25f, 0.275f);
			card.style.borderBottomColor = new Color(0.235f, 0.25f, 0.275f);
			card.style.borderLeftColor = new Color(0.235f, 0.25f, 0.275f);
			card.style.borderRightColor = new Color(0.235f, 0.25f, 0.275f);

			card.style.borderTopLeftRadius = 8f;
			card.style.borderTopRightRadius = 8f;
			card.style.borderBottomLeftRadius = 8f;
			card.style.borderBottomRightRadius = 8f;

			card.style.paddingLeft = 12f;
			card.style.paddingRight = 12f;
			card.style.paddingTop = 10f;
			card.style.paddingBottom = 10f;

			card.style.marginBottom = 8f;

			// Accent line.
			var accent = new VisualElement();
			accent.style.position = Position.Absolute;
			accent.style.left = 0f;
			accent.style.top = 0f;
			accent.style.bottom = 0f;
			accent.style.width = 3f;
			accent.style.backgroundColor = action.Destructive
				? new Color(0.85f, 0.31f, 0.35f)
				: new Color(0.16f, 0.64f, 0.76f);

			accent.style.borderTopLeftRadius = 8f;
			accent.style.borderBottomLeftRadius = 8f;
			card.Add(accent);

			// Top metadata row.
			var metaRow = new VisualElement();
			metaRow.style.flexDirection = FlexDirection.Row;
			metaRow.style.alignItems = Align.Center;
			metaRow.style.minWidth = 0f;
			metaRow.style.width = Length.Percent(100f);
			metaRow.style.marginBottom = 6f;
			card.Add(metaRow);

			var rule = new Label(action.RuleId);
			rule.style.flexGrow = 1f;
			rule.style.flexShrink = 1f;
			rule.style.minWidth = 0f;
			rule.style.fontSize = 10f;
			rule.style.unityFontStyleAndWeight = FontStyle.Bold;
			rule.style.color = new Color(0.47f, 0.50f, 0.55f);
			metaRow.Add(rule);

			var matching = MakeBadge(
				action.MatchingCount + " matching",
				new Color(0.07f, 0.28f, 0.235f),
				new Color(0.22f, 0.92f, 0.62f)
			);

			matching.style.flexShrink = 0f;
			matching.style.marginLeft = 8f;
			metaRow.Add(matching);

			if (action.Destructive)
			{
				var destructive = MakeBadge(
					"DESTRUCTIVE",
					new Color(0.34f, 0.105f, 0.12f),
					new Color(1.0f, 0.55f, 0.58f)
				);

				destructive.style.flexShrink = 0f;
				destructive.style.marginLeft = 6f;
				metaRow.Add(destructive);
			}

			if (!action.HasVerifier)
			{
				var unverified = MakeBadge(
					"UNVERIFIED",
					new Color(0.34f, 0.25f, 0.075f),
					new Color(1.0f, 0.77f, 0.32f)
				);

				unverified.style.flexShrink = 0f;
				unverified.style.marginLeft = 6f;
				metaRow.Add(unverified);
			}

			// Title is deliberately on its own line.
			// This is the key difference from the overflowing implementation.
			var title = new Label(action.DisplayName);
			title.style.flexGrow = 0f;
			title.style.flexShrink = 1f;
			title.style.minWidth = 0f;
			title.style.width = Length.Percent(100f);
			title.style.fontSize = 13f;
			title.style.unityFontStyleAndWeight = FontStyle.Bold;
			title.style.color = new Color(0.90f, 0.91f, 0.94f);
			title.style.whiteSpace = WhiteSpace.Normal;
			title.style.overflow = Overflow.Hidden;
			title.style.marginBottom = 5f;
			card.Add(title);

			if (!string.IsNullOrEmpty(action.Explanation))
			{
				var description = new Label(action.Explanation);

				description.style.flexGrow = 0f;
				description.style.flexShrink = 1f;
				description.style.minWidth = 0f;
				description.style.width = Length.Percent(100f);

				description.style.fontSize = 10.5f;
				description.style.color = new Color(0.62f, 0.65f, 0.69f);

				description.style.whiteSpace = WhiteSpace.Normal;
				description.style.overflow = Overflow.Hidden;

				card.Add(description);
			}

			var contextText = action.HasSingleContext
				? (action.TargetPlatform ?? "Default")
					+ (action.QualityLevelIndex >= 0 ? ", quality " + action.QualityLevelIndex : "")
				: null;

			if (contextText != null)
			{
				var context = new Label("Context  •  " + contextText);

				context.style.flexShrink = 1f;
				context.style.minWidth = 0f;
				context.style.width = Length.Percent(100f);

				context.style.fontSize = 10f;
				context.style.color = new Color(0.48f, 0.51f, 0.56f);
				context.style.marginTop = 5f;
				context.style.whiteSpace = WhiteSpace.Normal;

				card.Add(context);
			}

			// Bottom information/action row.
			var footer = new VisualElement();
			footer.style.flexDirection = FlexDirection.Row;
			footer.style.alignItems = Align.Center;
			footer.style.minWidth = 0f;
			footer.style.width = Length.Percent(100f);
			card.Add(footer);

			// Compact metadata cluster.
			var info = new VisualElement();
			info.style.flexDirection = FlexDirection.Row;
			info.style.alignItems = Align.Center;
			info.style.flexGrow = 1f;
			info.style.flexShrink = 1f;
			info.style.minWidth = 0f;
			footer.Add(info);

			if (action.EstimatedSavings > 0)
			{
				info.Add(
					MakeFooterInfo(
						"SAVE",
						DisplayUtility.FormatBytes(action.EstimatedSavings),
						new Color(0.25f, 0.82f, 0.58f)
					)
				);
			}

			if (action.BackupRequired)
			{
				info.Add(MakeFooterInfo("BACKUP", "required", new Color(0.62f, 0.73f, 0.85f)));
			}

			// The whole card is selectable, but we also provide an explicit affordance.
			var chooseButton = new Button();
			chooseButton.text = "Choose action";
			chooseButton.style.flexShrink = 0f;
			chooseButton.style.marginLeft = 10f;

			chooseButton.style.height = 26f;
			chooseButton.style.paddingLeft = 12f;
			chooseButton.style.paddingRight = 12f;

			chooseButton.style.backgroundColor = new Color(0.12f, 0.47f, 0.58f);

			chooseButton.style.color = new Color(0.88f, 0.97f, 1.0f);

			chooseButton.style.borderTopLeftRadius = 5f;
			chooseButton.style.borderTopRightRadius = 5f;
			chooseButton.style.borderBottomLeftRadius = 5f;
			chooseButton.style.borderBottomRightRadius = 5f;

			chooseButton.tooltip = "Choose this Review Fix action.";

			chooseButton.clicked += () => SelectAction(action);

			footer.Add(chooseButton);

			// Clicking anywhere on the card has the same effect, except on the button.
			card.RegisterCallback<ClickEvent>(evt =>
			{
				if (evt.target == chooseButton)
					return;

				SelectAction(action);
				evt.StopPropagation();
			});

			card.tooltip = BuildTooltip(action);

			// Slightly differentiate alternating cards without relying on a stylesheet.
			var normalBackground =
				(index & 1) != 0
					? new Color(0.145f, 0.155f, 0.175f)
					: new Color(0.16f, 0.17f, 0.19f);

			var hoverBackground = new Color(0.19f, 0.205f, 0.23f);
			card.style.backgroundColor = normalBackground;

			card.RegisterCallback<MouseEnterEvent>(_ =>
			{
				card.style.backgroundColor = hoverBackground;
				card.style.borderTopColor = new Color(0.28f, 0.48f, 0.53f);
				card.style.borderBottomColor = new Color(0.28f, 0.48f, 0.53f);
				card.style.borderLeftColor = new Color(0.28f, 0.48f, 0.53f);
				card.style.borderRightColor = new Color(0.28f, 0.48f, 0.53f);
			});

			card.RegisterCallback<MouseLeaveEvent>(_ =>
			{
				card.style.backgroundColor = normalBackground;
				card.style.borderTopColor = new Color(0.235f, 0.25f, 0.275f);
				card.style.borderBottomColor = new Color(0.235f, 0.25f, 0.275f);
				card.style.borderLeftColor = new Color(0.235f, 0.25f, 0.275f);
				card.style.borderRightColor = new Color(0.235f, 0.25f, 0.275f);
			});

			return card;
		}

		Label MakeBadge(string text, Color background, Color foreground)
		{
			var badge = new Label(text);

			badge.style.flexShrink = 0f;
			badge.style.fontSize = 9.5f;
			badge.style.unityFontStyleAndWeight = FontStyle.Bold;
			badge.style.color = foreground;

			badge.style.backgroundColor = background;

			badge.style.paddingLeft = 7f;
			badge.style.paddingRight = 7f;
			badge.style.paddingTop = 3f;
			badge.style.paddingBottom = 3f;

			badge.style.borderTopLeftRadius = 9f;
			badge.style.borderTopRightRadius = 9f;
			badge.style.borderBottomLeftRadius = 9f;
			badge.style.borderBottomRightRadius = 9f;

			badge.style.whiteSpace = WhiteSpace.NoWrap;
			badge.style.unityTextAlign = TextAnchor.MiddleCenter;

			return badge;
		}

		VisualElement MakeFooterInfo(string label, string value, Color valueColor)
		{
			var item = new VisualElement();
			item.style.flexDirection = FlexDirection.Row;
			item.style.alignItems = Align.Center;
			item.style.flexShrink = 0f;
			item.style.marginRight = 12f;

			var labelElement = new Label(label);
			labelElement.style.fontSize = 8.5f;
			labelElement.style.unityFontStyleAndWeight = FontStyle.Bold;
			labelElement.style.color = new Color(0.43f, 0.46f, 0.50f);
			item.Add(labelElement);

			var valueElement = new Label(value);
			valueElement.style.marginLeft = 5f;
			valueElement.style.fontSize = 10f;
			valueElement.style.unityFontStyleAndWeight = FontStyle.Bold;
			valueElement.style.color = valueColor;
			item.Add(valueElement);

			return item;
		}

		string BuildTooltip(ReviewActionDescriptor action)
		{
			var text = action.RuleId + "\n\n" + action.DisplayName;

			if (!string.IsNullOrEmpty(action.Explanation))
				text += "\n\n" + action.Explanation;

			text += "\n\n" + action.MatchingCount + " selected asset(s) match this action.";

			if (action.EstimatedSavings > 0)
				text += "\nEstimated saving: " + DisplayUtility.FormatBytes(action.EstimatedSavings);

			if (action.BackupRequired)
				text += "\nA backup is required before applying it.";

			return text;
		}

		void SelectAction(ReviewActionDescriptor action)
		{
			var selection = m_Selection;
			var afterFix = m_AfterFix;

			Close();

			ReviewFixDialog.OpenBatch(selection, action, afterFix);
		}
	}
}

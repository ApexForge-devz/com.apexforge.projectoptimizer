using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ApexForge.ProjectOptimizer
{
	public enum FeedbackKind
	{
		Info = 0,
		Success = 1,
		Warning = 2,
		Error = 3
	}

	/// <summary>
	/// One channel for "that worked" messages, so a page can confirm an action without knowing where
	/// the confirmation ends up.
	/// <para>
	/// Most actions in this tool change data that is not on screen - a preset rewrites settings shown
	/// three sections further down, applying a fix rewrites a .meta file. Without a reply the button
	/// looks broken even when it did exactly what it was asked. A dialog for each would be worse than
	/// silence, so these surface as a toast that fades on its own.
	/// </para>
	/// </summary>
	public static class Feedback
	{
		public sealed class Message
		{
			public string Text;
			public FeedbackKind Kind;
			/// <summary>Optional button on the toast, for the obvious next step.</summary>
			public string ActionLabel;
			public Action Action;
		}

		/// <summary>Raised on the main thread when something wants to be shown.</summary>
		public static event Action<Message> Posted;

		public static void Show(string text, FeedbackKind kind = FeedbackKind.Info,
			string actionLabel = null, Action action = null)
		{
			if (string.IsNullOrEmpty(text)) return;
			Posted?.Invoke(new Message
			{
				Text = text,
				Kind = kind,
				ActionLabel = actionLabel,
				Action = action
			});
		}

		public static void Success(string text, string actionLabel = null, Action action = null)
			=> Show(text, FeedbackKind.Success, actionLabel, action);

		public static void Warn(string text, string actionLabel = null, Action action = null)
			=> Show(text, FeedbackKind.Warning, actionLabel, action);

		public static void Error(string text, string actionLabel = null, Action action = null)
			=> Show(text, FeedbackKind.Error, actionLabel, action);

		public static string ClassFor(FeedbackKind kind)
		{
			switch (kind)
			{
				case FeedbackKind.Success: return "po-toast-success";
				case FeedbackKind.Warning: return "po-toast-warning";
				case FeedbackKind.Error: return "po-toast-error";
				default: return "po-toast-info";
			}
		}
	}

	/// <summary>
	/// Shows <see cref="Feedback"/> messages as a stack of toasts in the corner of the window.
	/// <para>
	/// Toasts are for confirmations, not decisions: they never block, they never take focus, and the
	/// window works identically if you ignore every one of them. Anything that genuinely needs an
	/// answer still uses a dialog.
	/// </para>
	/// </summary>
	public sealed class ToastHost : VisualElement
	{
		/// <summary>Warnings and errors linger; a plain confirmation does not need to be read.</summary>
		const long k_ShortMs = 2600;
		const long k_LongMs = 5200;

		/// <summary>Older toasts are dropped rather than stacked past this, so they cannot cover the window.</summary>
		const int k_MaxVisible = 3;

		readonly List<VisualElement> m_Live = new List<VisualElement>();

		public ToastHost()
		{
			AddToClassList("po-toast-host");
			// Purely decorative until something is posted: the host must never eat clicks meant for
			// the rows underneath it.
			pickingMode = PickingMode.Ignore;
		}

		public void Post(Feedback.Message message)
		{
			if (message == null) return;

			while (m_Live.Count >= k_MaxVisible) Dismiss(m_Live[0]);

			var toast = new VisualElement();
			toast.AddToClassList("po-toast");
			toast.AddToClassList(Feedback.ClassFor(message.Kind));

			var label = new Label(message.Text);
			label.AddToClassList("po-toast-text");
			toast.Add(label);

			if (!string.IsNullOrEmpty(message.ActionLabel) && message.Action != null)
			{
				var action = message.Action;
				var button = UiKit.Button(message.ActionLabel, () =>
				{
					Dismiss(toast);
					action();
				}, "po-button-small", "po-toast-action");
				toast.Add(button);
			}

			// A thin bar along the bottom edge that empties over the toast's own lifetime, so "this is
			// about to go away" is something you can see coming rather than a surprise when it vanishes.
			var countdown = new VisualElement();
			countdown.AddToClassList("po-toast-countdown");
			toast.Add(countdown);

			// Clicking anywhere on a toast gets rid of it, so one that is in the way is one click
			// from gone rather than something to wait out.
			toast.RegisterCallback<ClickEvent>(_ => Dismiss(toast));

			Add(toast);
			m_Live.Add(toast);

			long life = message.Kind == FeedbackKind.Info || message.Kind == FeedbackKind.Success
				? k_ShortMs
				: k_LongMs;

			// The countdown's own transition duration is set here rather than in the stylesheet: it has
			// to match this particular toast's lifetime, which differs by message kind, and USS cannot
			// express "whatever this instance's life field is".
			countdown.style.transitionDuration = new StyleList<TimeValue>(
				new List<TimeValue> { new TimeValue(life, TimeUnit.Millisecond) });
			countdown.style.width = Length.Percent(100f);

			// Added transparent, shifted right and slightly shrunk, then eased back to normal a frame
			// later, so the USS transition has two states to animate between - a plain fade felt flat
			// next to everything else that now moves on entry. The countdown starts shrinking on the
			// same tick, so its transition-duration above is already in effect before it moves.
			toast.style.opacity = 0f;
			toast.style.translate = new Translate(28, 0);
			toast.style.scale = new Scale(new Vector3(0.96f, 0.96f, 1f));
			toast.schedule.Execute(() =>
			{
				toast.style.opacity = 1f;
				toast.style.translate = new Translate(0, 0);
				toast.style.scale = new Scale(Vector3.one);
				countdown.style.width = Length.Percent(0f);
			}).StartingIn(0);

			toast.schedule.Execute(() => Dismiss(toast)).StartingIn(life);
		}

		void Dismiss(VisualElement toast)
		{
			if (toast == null || !m_Live.Remove(toast)) return;

			toast.style.opacity = 0f;
			toast.style.translate = new Translate(28, 0);
			// Removed after the fade rather than with it, so the toast is not yanked mid-animation.
			toast.schedule.Execute(() => toast.RemoveFromHierarchy()).StartingIn(180);
		}
	}
}

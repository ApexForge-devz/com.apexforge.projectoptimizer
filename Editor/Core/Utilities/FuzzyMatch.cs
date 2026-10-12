using System;
using System.Collections.Generic;
using System.Text;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// A small, deterministic, local fuzzy-ranking matcher for the search boxes on the category and
	/// rules pages, replacing plain <c>string.Contains</c>.
	/// <para>
	/// Deliberately not a general string-similarity library: it is tuned for the one shape of query
	/// this tool ever sees - a short, often-abbreviated fragment of an asset name, a rule id or a
	/// class name - typed against a small, fixed set of candidate strings per row. No allocation-heavy
	/// dependency is warranted for that, and pulling one in would be the "heavyweight external
	/// dependency" the brief asks not to add.
	/// </para>
	/// </summary>
	public static class FuzzyMatch
	{
		const string k_Separators = " _-./\\";

		/// <summary>
		/// Scores <paramref name="target"/> against <paramref name="query"/>, or returns false when it
		/// does not match at all. Higher is better; scores are only meaningful relative to one another
		/// for the same query, never as an absolute quality measure.
		/// <para>Tiers, strongest first: exact, prefix, word-boundary, subsequence (with consecutive-run,
		/// camelCase/acronym and separator bonuses), then bounded edit distance against one token for
		/// short queries. A path-ish target (one containing '/') pays a small depth penalty, so two
		/// equally good matches favour the shallower one.</para>
		/// </summary>
		public static bool TryScore(string query, string target, out int score)
		{
			score = 0;
			if (string.IsNullOrEmpty(target)) return false;
			if (string.IsNullOrEmpty(query)) return true;

			string q = query.Trim();
			if (q.Length == 0) return true;

			string qLower = q.ToLowerInvariant();
			string tLower = target.ToLowerInvariant();
			int depthPenalty = PathDepthPenalty(target);

			if (tLower == qLower) { score = 100000 - depthPenalty; return true; }

			if (tLower.StartsWith(qLower, StringComparison.Ordinal))
			{
				// Shorter targets are a tighter prefix match of the same query, so they rank first.
				score = 90000 - Math.Min(target.Length, 500) - depthPenalty;
				return true;
			}

			int boundary = FindWordBoundaryMatch(target, tLower, qLower);
			if (boundary >= 0)
			{
				score = 80000 - boundary - Math.Min(target.Length, 500) / 10 - depthPenalty;
				return true;
			}

			int? subsequence = ScoreSubsequence(target, tLower, qLower);
			if (subsequence.HasValue)
			{
				score = 40000 + subsequence.Value - Math.Min(target.Length, 500) / 4 - depthPenalty;
				return true;
			}

			// Typo tolerance, short queries only: a long query that merely fails to subsequence-match
			// is not a typo, it is a different string, and forgiving it would just return everything.
			if (qLower.Length <= 6 && HasCloseToken(target, qLower))
			{
				score = 10000 - Math.Min(target.Length, 500) - depthPenalty;
				return true;
			}

			return false;
		}

		/// <summary>The best score <paramref name="query"/> gets against any of <paramref name="candidates"/>, or null.</summary>
		public static int? BestScore(string query, IEnumerable<string> candidates)
		{
			int? best = null;
			foreach (var candidate in candidates)
			{
				if (!TryScore(query, candidate, out int score)) continue;
				if (best == null || score > best.Value) best = score;
			}
			return best;
		}

		static int PathDepthPenalty(string target)
		{
			int depth = 0;
			for (int i = 0; i < target.Length; i++) if (target[i] == '/') depth++;
			return depth * 6;
		}

		static bool IsSeparator(char c) => k_Separators.IndexOf(c) >= 0;

		/// <summary>
		/// The index of a place in <paramref name="target"/> where a "word" starts - after a
		/// separator, or where lowercase gives way to uppercase - and the whole query matches from
		/// there, case-insensitively. Handles "anim" finding "Animator" inside "AnimatorController"
		/// and "pref" finding "Prefab" inside "PrefabSceneAnalyzer".
		/// </summary>
		static int FindWordBoundaryMatch(string target, string tLower, string qLower)
		{
			for (int i = 0; i < target.Length; i++)
			{
				bool boundary = i == 0
								|| IsSeparator(target[i - 1])
								|| (char.IsUpper(target[i]) && char.IsLower(target[i - 1]));
				if (!boundary) continue;
				if (i + qLower.Length > tLower.Length) continue;
				if (string.CompareOrdinal(tLower, i, qLower, 0, qLower.Length) == 0) return i;
			}
			return -1;
		}

		/// <summary>
		/// Walks the query through the target in order, allowing gaps, and scores the result:
		/// consecutive runs, a hit right after a separator, and a hit that lands on an
		/// uppercase/acronym letter (mid-word) each add a bonus, and any skipped character between two
		/// matches costs a small penalty so a tighter cluster of matches always outranks a looser one
		/// of the same length. Returns null when the characters do not all appear in order at all.
		/// </summary>
		static int? ScoreSubsequence(string target, string tLower, string qLower)
		{
			int searchFrom = 0;
			int lastMatch = -1;
			int score = 0;

			for (int qi = 0; qi < qLower.Length; qi++)
			{
				int found = tLower.IndexOf(qLower[qi], searchFrom);
				if (found < 0) return null;

				if (lastMatch < 0)
				{
					if (found == 0) score += 15;
				}
				else
				{
					int gap = found - lastMatch - 1;
					score -= gap;
					if (gap == 0) score += 15;
				}

				bool isUpperMidWord = char.IsUpper(target[found]) && found > 0 && char.IsLower(target[found - 1]);
				if (isUpperMidWord) score += 12;

				if (found > 0 && IsSeparator(target[found - 1])) score += 10;

				score += 3;
				lastMatch = found;
				searchFrom = found + 1;
			}
			return score;
		}

		/// <summary>
		/// True when the query is within a small edit distance of one whole token of the target -
		/// tokens split on separators and camelCase boundaries, so "MskUtilities" offers up "Msk" and
		/// "Utilities" as candidates rather than the string as a whole.
		/// </summary>
		static bool HasCloseToken(string target, string qLower)
		{
			int budget = qLower.Length <= 3 ? 1 : 2;
			foreach (var token in Tokenize(target))
			{
				if (Math.Abs(token.Length - qLower.Length) > budget) continue;
				if (LevenshteinAtMost(qLower, token, budget)) return true;
			}
			return false;
		}

		static List<string> Tokenize(string target)
		{
			var tokens = new List<string>();
			var current = new StringBuilder();
			for (int i = 0; i < target.Length; i++)
			{
				char c = target[i];
				bool separator = IsSeparator(c);
				bool camelBoundary = !separator && i > 0 && char.IsUpper(c) && char.IsLower(target[i - 1]);

				if (separator || camelBoundary)
				{
					if (current.Length > 0) { tokens.Add(current.ToString().ToLowerInvariant()); current.Clear(); }
				}
				if (!separator) current.Append(c);
			}
			if (current.Length > 0) tokens.Add(current.ToString().ToLowerInvariant());
			return tokens;
		}

		static bool LevenshteinAtMost(string a, string b, int max)
		{
			if (Math.Abs(a.Length - b.Length) > max) return false;

			var previous = new int[b.Length + 1];
			var current = new int[b.Length + 1];
			for (int j = 0; j <= b.Length; j++) previous[j] = j;

			for (int i = 1; i <= a.Length; i++)
			{
				current[0] = i;
				int rowBest = current[0];
				for (int j = 1; j <= b.Length; j++)
				{
					int cost = a[i - 1] == b[j - 1] ? 0 : 1;
					current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
					if (current[j] < rowBest) rowBest = current[j];
				}
				// Every remaining cell in the row can only grow, so once even the best of this row
				// has already blown the budget the full distance cannot come back under it.
				if (rowBest > max) return false;
				var swap = previous; previous = current; current = swap;
			}
			return previous[b.Length] <= max;
		}
	}
}

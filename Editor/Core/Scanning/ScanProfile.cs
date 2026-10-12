using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// The shape of a scan: how far through the wall clock the scan was at each point on the
	/// progress bar.
	/// <para>
	/// Progress and time are not proportional, and assuming they are is what makes a remaining-time
	/// readout useless. The per-asset sweep covers most of the bar at a fairly even rate; the
	/// project-wide passes cover the last fifth and can take a third of the run. Extrapolating from
	/// "we are 98% along" therefore predicts a couple of seconds while a couple of minutes are left,
	/// and keeps predicting it, because the same wrong assumption produces the same wrong answer
	/// every time it is asked.
	/// </para>
	/// <para>
	/// So the previous scan of this project is recorded and used as the model for the next one. Same
	/// project, same settings, same machine: the shape barely moves between runs, which makes it a
	/// far better predictor than any formula.
	/// </para>
	/// </summary>
	public sealed class ScanProfile
	{
		/// <summary>Progress is bucketed at this resolution: 51 samples from 0% to 100%.</summary>
		public const int Buckets = 51;

		static readonly string k_Path = ProjectOptimizerConstants.ScanProfilePath;
		const int k_Version = 2;

		/// <summary>
		/// What this recorded shape describes. A scan is only a model for another scan of roughly
		/// the same shape: a folder-scoped run over 300 assets and a full run over 300,000 have
		/// nothing in common but the units on the axes, and using one to predict the other produces
		/// a countdown that is wrong in the same direction every time.
		/// <para>
		/// Asset count is bucketed by order of magnitude rather than compared exactly, so ordinary
		/// growth does not throw the history away.
		/// </para>
		/// </summary>
		public static string KeyFor(OptimizerSettings settings, int assetCount)
		{
			int magnitude = 0;
			for (int n = Mathf.Max(1, assetCount); n >= 10; n /= 10) magnitude++;

			// The project's own identity is part of the key. Timing history lives in Library, and a
			// Library folder can be copied, shared or restored alongside a different project - at
			// which point a stranger's scan shape would be driving this project's countdown.
			return "tool=" + OptimizerVersion.Schema
				   + "|project=" + ProjectIdentity()
				   + "|" + (settings != null ? settings.scopeMode.ToString() : "?")
				   + "|" + (settings != null ? settings.targetPlatform.ToString() : "?")
				   + "|prefab=" + (settings != null && settings.scanPrefabs ? "1" : "0")
				   + "|scene=" + (settings != null && settings.scanScenes ? "1" : "0")
				   + "|dup=" + (settings != null && settings.scanDuplicates ? "1" : "0")
				   + "|unused=" + (settings != null && settings.scanUnused ? "1" : "0")
				   + "|deepTex=" + (settings != null && settings.deepTextureScan ? "1" : "0")
				   + "|proof=" + (settings != null && settings.projectProofPass ? "1" : "0")
				   + "|assetLimit=" + (settings != null ? settings.maxAssetSizeMb : 0)
					+ "|1e" + magnitude;
		}

		/// <summary>
		/// A stable, cheap identifier for this project: the data path, folded to a number. Not a
		/// security measure - just enough that two different projects cannot be mistaken for each
		/// other by a file that outlived one of them.
		/// </summary>
		static string ProjectIdentity()
		{
			unchecked
			{
				var path = Application.dataPath ?? string.Empty;
				ulong acc = 1469598103934665603UL;
				for (int i = 0; i < path.Length; i++)
				{
					acc ^= char.ToLowerInvariant(path[i]);
					acc *= 1099511628211UL;
				}
				return acc.ToString("x16");
			}
		}

		/// <summary>The key the loaded shape was recorded under, or null when nothing was loaded.</summary>
		public string Key { get; private set; }

		/// <summary>Fraction of the total run that had elapsed on reaching each progress bucket.</summary>
		readonly float[] m_TimeAt = new float[Buckets];

		/// <summary>How long the recorded scan took, end to end.</summary>
		public double TotalSeconds { get; private set; }

		public bool IsUsable => TotalSeconds > 1.0;

		public static int BucketOf(float progress)
		{
			int bucket = Mathf.RoundToInt(Mathf.Clamp01(progress) * (Buckets - 1));
			return Mathf.Clamp(bucket, 0, Buckets - 1);
		}

		/// <summary>
		/// Predicts total run time from where the scan is now. Reads the share of the run that had
		/// been spent by this point last time, and scales the measured elapsed time by it.
		/// </summary>
		public double PredictTotalSeconds(float progress, double elapsed)
		{
			if (!IsUsable) return -1.0;

			float share = m_TimeAt[BucketOf(progress)];

			// Too early to divide by: the first buckets are collection and cache loading, where a
			// tiny share would turn a second of noise into a wild total.
			if (share < 0.02f) return -1.0;

			return elapsed / share;
		}

		// --- recording ------------------------------------------------------------------

		/// <summary>Accumulates the shape of a scan as it runs.</summary>
		public sealed class Recorder
		{
			readonly double[] m_ElapsedAt = new double[Buckets];
			int m_Highest = -1;

			public void Sample(float progress, double elapsed)
			{
				int bucket = BucketOf(progress);
				if (bucket <= m_Highest) return;

				// Fill any buckets jumped over, so a fast stretch does not leave holes that read as
				// zero later.
				for (int i = Mathf.Max(0, m_Highest + 1); i <= bucket; i++) m_ElapsedAt[i] = elapsed;
				m_Highest = bucket;
			}

			/// <summary>Normalises what was recorded into a profile and returns it.</summary>
			public ScanProfile Build(double totalSeconds, string key)
			{
				var profile = new ScanProfile { TotalSeconds = totalSeconds, Key = key };
				if (totalSeconds <= 0) return profile;

				// Anything never reached is treated as having taken until the end, so a scan that
				// was cancelled early cannot claim the tail is free.
				double last = 0;
				for (int i = 0; i < Buckets; i++)
				{
					if (i > m_Highest) m_ElapsedAt[i] = totalSeconds;
					if (m_ElapsedAt[i] < last) m_ElapsedAt[i] = last;
					last = m_ElapsedAt[i];
					profile.m_TimeAt[i] = (float)Mathf.Clamp01((float)(m_ElapsedAt[i] / totalSeconds));
				}
				return profile;
			}
		}

		// --- persistence -----------------------------------------------------------------

		/// <summary>
		/// Loads the shape of the last scan of this shape, or an unusable profile when the stored
		/// one describes a different one.
		/// <para>
		/// Previously keyed on nothing at all, on the reasoning that any history beats assuming
		/// progress is linear. It does not: a shape recorded from a different scope, platform or
		/// project size is confidently wrong rather than merely uninformed, and a countdown that is
		/// wrong the same way every run reads as broken. Where the key does not match, the honest
		/// answer is no prediction until this run has enough of its own history.
		/// </para>
		/// </summary>
		public static ScanProfile Load(string key)
		{
			var profile = new ScanProfile();
			try
			{
				if (!File.Exists(k_Path)) return profile;

				var lines = File.ReadAllLines(k_Path);
				if (lines.Length < 4 || lines[0] != k_Version.ToString()) return profile;
				if (!string.Equals(lines[1], key, StringComparison.Ordinal)) return profile;
				if (!double.TryParse(lines[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double total))
					return profile;

				var parts = lines[3].Split(' ');
				if (parts.Length < Buckets) return profile;

				for (int i = 0; i < Buckets; i++)
				{
					float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value);
					profile.m_TimeAt[i] = Mathf.Clamp01(value);
				}
				profile.TotalSeconds = total;
				profile.Key = key;
			}
			catch
			{
				return new ScanProfile();
			}
			return profile;
		}

		public void Save()
		{
			try
			{
				if (!IsUsable || string.IsNullOrEmpty(Key) || !CacheFileUtility.EnsureCacheFolder()) return;

				var values = new string[Buckets];
				for (int i = 0; i < Buckets; i++)
					values[i] = m_TimeAt[i].ToString("0.####", CultureInfo.InvariantCulture);

				File.WriteAllText(k_Path,
					k_Version + "\n"
					+ Key + "\n"
					+ TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "\n"
					+ string.Join(" ", values) + "\n");
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not record the scan shape: " + e.Message);
			}
		}

		public static void Delete()
		{
			try { if (File.Exists(k_Path)) File.Delete(k_Path); }
			catch { /* best effort */ }
		}
	}
}

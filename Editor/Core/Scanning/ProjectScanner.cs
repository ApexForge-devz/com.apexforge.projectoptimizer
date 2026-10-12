using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Debug = UnityEngine.Debug;
using System.Linq;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Where a scan spent its time. Wall time includes the frames the scanner gives back to the
	/// editor, so the per-stage figures measure actual work, which is what is worth optimising.
	/// </summary>
	public sealed class ScanTiming
	{
		public readonly Dictionary<string, double> Stages = new Dictionary<string, double>();
		public readonly Dictionary<string, double> Analyzers = new Dictionary<string, double>();
		public readonly Dictionary<string, int> AnalyzerCalls = new Dictionary<string, int>();
		public readonly Dictionary<string, double> DiagnosticTimings = new Dictionary<string, double>(StringComparer.Ordinal);
		public int CacheHits;
		public int CacheMisses;
		public int BackgroundAssetWorkItems;
		public int BackgroundAssetCompleted;
		public double BackgroundAssetWallMilliseconds;
		public int BackgroundAssetQueueCapacity;
		public int BackgroundAssetQueueHighWater;
		public int BackgroundAssetHeavyQueueHighWater;
		public int BackgroundAssetResultQueueHighWater;
		public long BackgroundAssetHeavyWorkItems;
		public double BackgroundAssetWorkerUtilizationPercent;
		public long BackgroundCacheWritesCommitted;
		public double BackgroundAssetExtractionMilliseconds;
		public int BackgroundAssetOutstandingHighWater;
		public int BackgroundAssetOrderedPendingHighWater;
		public int BackgroundAssetWorkerTargetHighWater;
		public long BackgroundAssetBackpressureEvents;
		public int BackgroundCacheWriteHighWater;
		public int BackgroundWorkerTarget;
		public int BackgroundProjectPasses;
		public int BackgroundProjectCompleted;
		public double BackgroundProjectWallMilliseconds;
		public ScanProfilingMode ProfilingMode { get; private set; } = ScanProfilingMode.Production;
		public string BenchmarkIdentifier { get; set; } = string.Empty;

		public readonly Dictionary<string, RuleTimingStats> Rules = new Dictionary<string, RuleTimingStats>(StringComparer.Ordinal);
		[ThreadStatic] static ScanTiming s_Active;
		[ThreadStatic] static Stack<RuleTimingScope> s_RuleStack;
		[ThreadStatic] static string s_ActiveAnalyzer;
		[ThreadStatic] static int s_ActiveAnalyzerRuleCount;
		[ThreadStatic] static double s_ActiveAnalyzerRuleMs;
		[ThreadStatic] static Dictionary<string, List<RuleTimingSample>> s_ActiveAnalyzerSamplesByRule;
		[ThreadStatic] static Stack<RuleTimingScope> s_RuleScopePool;
		public static ScanTiming Active => s_Active;
		public void ConfigureProfiling(ScanProfilingMode mode) => ProfilingMode = mode;
		public void Activate() => s_Active = this;
		public static void Deactivate(ScanTiming timing)
		{
			if (ReferenceEquals(s_Active, timing))
				s_Active = null;
			if (s_RuleStack != null)
				s_RuleStack.Clear();
			s_ActiveAnalyzer = null;
			s_ActiveAnalyzerRuleCount = 0;
			s_ActiveAnalyzerRuleMs = 0.0;
			if (s_ActiveAnalyzerSamplesByRule != null) s_ActiveAnalyzerSamplesByRule.Clear();
		}
		public void BeginAnalyzerTracking(string analyzerName)
		{
			if (ProfilingMode < ScanProfilingMode.AggregateProfiling) return;
			s_ActiveAnalyzer = analyzerName;
			s_ActiveAnalyzerRuleCount = 0;
			s_ActiveAnalyzerRuleMs = 0.0;
			if (ProfilingMode != ScanProfilingMode.DeepProfiling && ProfilingMode != ScanProfilingMode.Validation)
			{
				if (s_ActiveAnalyzerSamplesByRule != null) s_ActiveAnalyzerSamplesByRule.Clear();
				return;
			}
			if (s_ActiveAnalyzerSamplesByRule == null)
				s_ActiveAnalyzerSamplesByRule = new Dictionary<string, List<RuleTimingSample>>(StringComparer.Ordinal);
			else
				s_ActiveAnalyzerSamplesByRule.Clear();
		}
		public void EndAnalyzerTracking(string analyzerName, double analyzerMilliseconds)
		{
			if (!string.Equals(s_ActiveAnalyzer, analyzerName, StringComparison.Ordinal)) return;
			var key = analyzerName ?? "<unknown>";
			if (!AnalyzerRuleAccounting.TryGetValue(key, out var info))
				AnalyzerRuleAccounting[key] = info = new AnalyzerRuleTimingInfo(key);

			// Analyzer time is the authoritative inclusive measurement for this invocation. Rule
			// scopes are independent measurements nested inside it. Any remainder is genuinely shared
			// work (asset/fact loading, common indexing, cache reads, setup, etc.) and cannot be measured
			// as belonging exclusively to one rule. We therefore keep the exact rule stopwatch time
			// separate and only produce an explicitly estimated inclusive attribution for reporting.
			//
			// The estimate is weighted by the measured exclusive rule time for this invocation. This is
			// preferable to equal-splitting the whole analyzer across every rule: a rule that genuinely
			// consumes most of the rule-visible CPU receives most of the shared-cost estimate, while a
			// rule whose actual body is effectively free receives little. If every direct rule sample is
			// below the timing-resolution floor, fall back to equal rule weighting because proportional
			// allocation would be dominated by timer noise.
			double measuredAnalyzerMs = Math.Max(0.0, analyzerMilliseconds);
			double directRuleMs = Math.Max(0.0, s_ActiveAnalyzerRuleMs);
			double sharedMs = Math.Max(0.0, measuredAnalyzerMs - directRuleMs);
			if (ProfilingMode == ScanProfilingMode.AggregateProfiling)
			{
				info.InvocationCount++;
				info.InvocationsWithRules += s_ActiveAnalyzerRuleCount > 0 ? 1 : 0;
				info.InvocationsWithoutRules += s_ActiveAnalyzerRuleCount == 0 ? 1 : 0;
				info.AnalyzerMilliseconds += measuredAnalyzerMs;
				info.RuleMilliseconds += directRuleMs;
				info.SharedMilliseconds += sharedMs;
				if (s_ActiveAnalyzerRuleCount > 0) info.AnalyzerMillisecondsWithRules += measuredAnalyzerMs;
				else info.AnalyzerMillisecondsWithoutRules += measuredAnalyzerMs;
				info.RuleSamples += s_ActiveAnalyzerRuleCount;
				s_ActiveAnalyzer = null;
				return;
			}
			double allocatedSharedMs = 0.0;
			const double k_WeightFloorMs = 0.01; // 10 microseconds; below this Stopwatch noise dominates.

			info.InvocationCount++;
			if (s_ActiveAnalyzerSamplesByRule != null && s_ActiveAnalyzerSamplesByRule.Count > 0)
			{
				info.InvocationsWithRules++;
				info.AnalyzerMillisecondsWithRules += measuredAnalyzerMs;
				double measuredWeight = 0.0;
				foreach (var pair in s_ActiveAnalyzerSamplesByRule)
				{
					var samples = pair.Value;
					if (samples == null) continue;
					for (int i = 0; i < samples.Count; i++)
						measuredWeight += Math.Max(0.0, samples[i].Milliseconds);
				}

				bool useMeasuredWeights = measuredWeight >= k_WeightFloorMs;
				double fallbackRuleWeight = 1.0 / s_ActiveAnalyzerSamplesByRule.Count;

				foreach (var pair in s_ActiveAnalyzerSamplesByRule)
				{
					var samples = pair.Value;
					if (samples == null || samples.Count == 0) continue;

					double ruleWeight = 0.0;
					if (useMeasuredWeights)
					{
						for (int i = 0; i < samples.Count; i++)
							ruleWeight += Math.Max(0.0, samples[i].Milliseconds);
						ruleWeight = measuredWeight > 0.0 ? ruleWeight / measuredWeight : fallbackRuleWeight;
					}
					else ruleWeight = fallbackRuleWeight;

					double ruleSharedMs = sharedMs * ruleWeight;
					if (Rules.TryGetValue(pair.Key, out var globalRuleStats)) globalRuleStats.AddEstimatedShared(ruleSharedMs);
					double sampleDirectWeight = 0.0;
					for (int i = 0; i < samples.Count; i++) sampleDirectWeight += Math.Max(0.0, samples[i].Milliseconds);
					for (int i = 0; i < samples.Count; i++)
					{
						double sampleWeight = sampleDirectWeight > 0.0
							? Math.Max(0.0, samples[i].Milliseconds) / sampleDirectWeight
							: 1.0 / samples.Count;
						double sampleSharedMs = ruleSharedMs * sampleWeight;
						samples[i].SharedMilliseconds += sampleSharedMs;
						allocatedSharedMs += sampleSharedMs;
					}
				}
			}
			else
			{
				info.InvocationsWithoutRules++;
				info.AnalyzerMillisecondsWithoutRules += measuredAnalyzerMs;
			}

			info.AnalyzerMilliseconds += measuredAnalyzerMs;
			info.RuleMilliseconds += directRuleMs;
			info.SharedMilliseconds += sharedMs;
			info.AllocatedSharedMilliseconds += allocatedSharedMs;
			info.RuleSamples += s_ActiveAnalyzerRuleCount;
		}
		public readonly Dictionary<string, AnalyzerRuleTimingInfo> AnalyzerRuleAccounting = new Dictionary<string, AnalyzerRuleTimingInfo>(StringComparer.Ordinal);
		public IDisposable BeginRule(string ruleId, string assetPath)
		{
			if (string.IsNullOrEmpty(ruleId) || ProfilingMode < ScanProfilingMode.AggregateProfiling) return null;
			var scope = RentRuleTimingScope(this, ruleId, assetPath, s_ActiveAnalyzer);
			if (s_RuleStack == null) s_RuleStack = new Stack<RuleTimingScope>();
			s_RuleStack.Push(scope);
			return scope;
		}

		static RuleTimingScope RentRuleTimingScope(ScanTiming timing, string ruleId, string assetPath, string analyzerName)
		{
			if (s_RuleScopePool != null && s_RuleScopePool.Count > 0)
			{
				var pooled = s_RuleScopePool.Pop();
				pooled.Reset(timing, ruleId, assetPath, analyzerName);
				return pooled;
			}
			return new RuleTimingScope(timing, ruleId, assetPath, analyzerName);
		}

		internal void EndRule(RuleTimingScope scope, double milliseconds, int issueCount)
		{
			if (scope == null) return;
			if (ProfilingMode == ScanProfilingMode.AggregateProfiling)
			{
				double aggregateExclusiveMilliseconds = Math.Max(0.0, milliseconds - scope.NestedMilliseconds);
				if (!Rules.TryGetValue(scope.RuleId, out var aggregateStats))
					Rules[scope.RuleId] = aggregateStats = new RuleTimingStats(scope.RuleId, false);
				aggregateStats.AddAggregate(aggregateExclusiveMilliseconds, issueCount);
				s_ActiveAnalyzerRuleCount++;
				s_ActiveAnalyzerRuleMs += aggregateExclusiveMilliseconds;
				PopRuleScope(scope, milliseconds);
				return;
			}

			// Nested rule scopes are common in conditionally branched analyzers. Their time must not
			// be counted again in the parent rule, otherwise the parent is charged for child work.
			double exclusiveMilliseconds = Math.Max(0.0, milliseconds - scope.NestedMilliseconds);
			if (!Rules.TryGetValue(scope.RuleId, out var stats)) Rules[scope.RuleId] = stats = new RuleTimingStats(scope.RuleId, true);
			var sample = stats.Add(scope.AssetPath, exclusiveMilliseconds, issueCount, scope.AnalyzerName);
			s_ActiveAnalyzerRuleCount++;
			s_ActiveAnalyzerRuleMs += exclusiveMilliseconds;
			if (s_ActiveAnalyzer != null)
			{
				if (s_ActiveAnalyzerSamplesByRule == null)
					s_ActiveAnalyzerSamplesByRule = new Dictionary<string, List<RuleTimingSample>>(StringComparer.Ordinal);
				if (!s_ActiveAnalyzerSamplesByRule.TryGetValue(scope.RuleId, out var samples))
					s_ActiveAnalyzerSamplesByRule[scope.RuleId] = samples = new List<RuleTimingSample>();
				samples.Add(sample);
			}
			PopRuleScope(scope, milliseconds);
		}
		void PopRuleScope(RuleTimingScope scope, double milliseconds)
		{
			if (s_RuleStack != null && s_RuleStack.Count > 0)
			{
				if (ReferenceEquals(s_RuleStack.Peek(), scope))
				{
					s_RuleStack.Pop();
					if (s_RuleStack.Count > 0) s_RuleStack.Peek().NestedMilliseconds += milliseconds;
				}
				else
				{
					var temp = new Stack<RuleTimingScope>();
					while (s_RuleStack.Count > 0)
					{
						var candidate = s_RuleStack.Pop();
						if (ReferenceEquals(candidate, scope)) break;
						temp.Push(candidate);
					}
					if (s_RuleStack.Count > 0) s_RuleStack.Peek().NestedMilliseconds += milliseconds;
					while (temp.Count > 0) s_RuleStack.Push(temp.Pop());
				}
			}
			if (s_RuleScopePool == null) s_RuleScopePool = new Stack<RuleTimingScope>(8);
			if (s_RuleScopePool.Count < 256) s_RuleScopePool.Push(scope);
		}

		public static void MarkIssue(string ruleId, string assetPath)
		{
			if (s_Active == null || string.IsNullOrEmpty(ruleId) || s_Active.ProfilingMode < ScanProfilingMode.AggregateProfiling) return;
			if (s_Active.ProfilingMode == ScanProfilingMode.AggregateProfiling)
			{
				if (!s_Active.Rules.TryGetValue(ruleId, out var aggregateStats))
					s_Active.Rules[ruleId] = aggregateStats = new RuleTimingStats(ruleId, false);
				aggregateStats.MarkIssueOnly(assetPath);
				return;
			}
			if (s_RuleStack != null) foreach (var scope in s_RuleStack) if (string.Equals(scope.RuleId, ruleId, StringComparison.Ordinal)) { scope.IssueCount++; return; }
			if (!s_Active.Rules.TryGetValue(ruleId, out var stats)) s_Active.Rules[ruleId] = stats = new RuleTimingStats(ruleId);
			stats.MarkIssueOnly(assetPath);
		}

		public static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

		public void AddStage(string key, double ms) => Accumulate(Stages, key, ms);

		public void AddAnalyzer(string key, double ms)
		{
			Accumulate(Analyzers, key, ms);
			AnalyzerCalls.TryGetValue(key, out int calls);
			AnalyzerCalls[key] = calls + 1;
		}

		public void AddDiagnosticTiming(string key, double ms)
		{
			if (string.IsNullOrEmpty(key)) return;
			Accumulate(DiagnosticTimings, key, Math.Max(0.0, ms));
		}

		/// <summary>Merges worker-local timing into the main-thread scan timing after a background batch completes.</summary>
		public void MergeFrom(ScanTiming other)
		{
			if (other == null) return;
			foreach (var pair in other.Stages) Accumulate(Stages, pair.Key, pair.Value);
			foreach (var pair in other.Analyzers) Accumulate(Analyzers, pair.Key, pair.Value);
			foreach (var pair in other.DiagnosticTimings) Accumulate(DiagnosticTimings, pair.Key, pair.Value);
			foreach (var pair in other.AnalyzerCalls)
			{
				AnalyzerCalls.TryGetValue(pair.Key, out int current);
				AnalyzerCalls[pair.Key] = current + pair.Value;
			}
			CacheHits += other.CacheHits;
			CacheMisses += other.CacheMisses;
			BackgroundAssetWorkItems += other.BackgroundAssetWorkItems;
			BackgroundAssetCompleted += other.BackgroundAssetCompleted;
			BackgroundAssetWallMilliseconds += other.BackgroundAssetWallMilliseconds;
			BackgroundProjectPasses += other.BackgroundProjectPasses;
			BackgroundProjectCompleted += other.BackgroundProjectCompleted;
			BackgroundProjectWallMilliseconds += other.BackgroundProjectWallMilliseconds;
			BackgroundAssetQueueCapacity = Math.Max(BackgroundAssetQueueCapacity, other.BackgroundAssetQueueCapacity);
			BackgroundAssetQueueHighWater = Math.Max(BackgroundAssetQueueHighWater, other.BackgroundAssetQueueHighWater);
			BackgroundAssetHeavyQueueHighWater = Math.Max(BackgroundAssetHeavyQueueHighWater, other.BackgroundAssetHeavyQueueHighWater);
			BackgroundAssetResultQueueHighWater = Math.Max(BackgroundAssetResultQueueHighWater, other.BackgroundAssetResultQueueHighWater);
			BackgroundAssetHeavyWorkItems += other.BackgroundAssetHeavyWorkItems;
			BackgroundCacheWritesCommitted += other.BackgroundCacheWritesCommitted;
			BackgroundAssetExtractionMilliseconds += other.BackgroundAssetExtractionMilliseconds;
			BackgroundAssetWorkerUtilizationPercent = Math.Max(BackgroundAssetWorkerUtilizationPercent, other.BackgroundAssetWorkerUtilizationPercent);
			BackgroundAssetOutstandingHighWater = Math.Max(BackgroundAssetOutstandingHighWater, other.BackgroundAssetOutstandingHighWater);
			BackgroundAssetOrderedPendingHighWater = Math.Max(BackgroundAssetOrderedPendingHighWater, other.BackgroundAssetOrderedPendingHighWater);
			BackgroundAssetWorkerTargetHighWater = Math.Max(BackgroundAssetWorkerTargetHighWater, other.BackgroundAssetWorkerTargetHighWater);
			BackgroundAssetBackpressureEvents += other.BackgroundAssetBackpressureEvents;
			BackgroundCacheWriteHighWater = Math.Max(BackgroundCacheWriteHighWater, other.BackgroundCacheWriteHighWater);
			BackgroundWorkerTarget = Math.Max(BackgroundWorkerTarget, other.BackgroundWorkerTarget);
			foreach (var pair in other.Rules)
			{
				if (!Rules.TryGetValue(pair.Key, out var target))
				{
					target = new RuleTimingStats(pair.Key, ProfilingMode == ScanProfilingMode.DeepProfiling || ProfilingMode == ScanProfilingMode.Validation);
					Rules[pair.Key] = target;
				}
				MergeRuleStats(target, pair.Value);
			}
			foreach (var pair in other.AnalyzerRuleAccounting)
			{
				if (!AnalyzerRuleAccounting.TryGetValue(pair.Key, out var target))
					AnalyzerRuleAccounting[pair.Key] = target = new AnalyzerRuleTimingInfo(pair.Key);
				var source = pair.Value;
				target.InvocationCount += source.InvocationCount;
				target.InvocationsWithRules += source.InvocationsWithRules;
				target.InvocationsWithoutRules += source.InvocationsWithoutRules;
				target.AnalyzerMilliseconds += source.AnalyzerMilliseconds;
				target.RuleMilliseconds += source.RuleMilliseconds;
				target.SharedMilliseconds += source.SharedMilliseconds;
				target.AllocatedSharedMilliseconds += source.AllocatedSharedMilliseconds;
				target.AnalyzerMillisecondsWithRules += source.AnalyzerMillisecondsWithRules;
				target.AnalyzerMillisecondsWithoutRules += source.AnalyzerMillisecondsWithoutRules;
				target.RuleSamples += source.RuleSamples;
			}
		}

		static void MergeRuleStats(RuleTimingStats target, RuleTimingStats source)
		{
			if (target == null || source == null) return;
			target.MergeFrom(source);
		}

		static void Accumulate(Dictionary<string, double> map, string key, double ms)
		{
			map.TryGetValue(key, out double total);
			map[key] = total + ms;
		}

		public double TotalWorkMs
		{
			get
			{
				double total = 0;
				foreach (var pair in Stages) total += pair.Value;
				foreach (var pair in Analyzers) total += pair.Value;
				return total;
			}
		}

		public string Report()
		{
			var lines = new List<KeyValuePair<string, double>>();
			foreach (var pair in Stages) lines.Add(pair);
			foreach (var pair in Analyzers)
			{
				AnalyzerCalls.TryGetValue(pair.Key, out int calls);
				lines.Add(new KeyValuePair<string, double>(pair.Key + "  (" + calls + " assets)", pair.Value));
			}
			lines.Sort((a, b) => b.Value.CompareTo(a.Value));

			var sb = new StringBuilder();
			sb.Append("total work ").Append((TotalWorkMs / 1000.0).ToString("0.00")).AppendLine(" s");
			if (CacheHits + CacheMisses > 0)
			{
				sb.Append("cache ").Append(CacheHits).Append(" hits / ")
				  .Append(CacheMisses).AppendLine(" misses");
			}
			foreach (var line in lines)
			{
				sb.Append("  ").Append(line.Key.PadRight(40))
				  .Append((line.Value / 1000.0).ToString("0.000")).AppendLine(" s");
			}
			return sb.ToString();
		}
	}

	public sealed class RuleTimingStats
	{
		const int k_HistogramBuckets = 64;
		const double k_FirstBucketMilliseconds = 0.01;
		const int k_MaxDiagnosticSamples = 128;
		public readonly string RuleId;
		public int Evaluations;
		public int Issues;
		public double DirectMilliseconds { get; private set; }
		public double EstimatedSharedMilliseconds { get; private set; }
		readonly long[] m_Histogram;
		readonly List<RuleTimingSample> m_DiagnosticSamples;
		RuleTimingSample m_Slowest1;
		RuleTimingSample m_Slowest2;
		RuleTimingSample m_Fastest1;
		RuleTimingSample m_Fastest2;
		readonly bool m_CollectDetail;

		public int SampleCount => Evaluations;
		public double TotalMilliseconds => DirectMilliseconds + EstimatedSharedMilliseconds;
		public double MinMilliseconds => !m_CollectDetail || Evaluations == 0 ? 0.0 : m_Histogram[0] > 0 ? k_FirstBucketMilliseconds : (m_Fastest1 != null ? m_Fastest1.Milliseconds : 0.0);
		public double MaxMilliseconds => !m_CollectDetail ? 0.0 : (m_Slowest1 != null ? m_Slowest1.Milliseconds : 0.0);

		public RuleTimingStats(string ruleId) : this(ruleId, true) { }
		public RuleTimingStats(string ruleId, bool collectDetail)
		{
			RuleId = ruleId;
			m_CollectDetail = collectDetail;
			if (collectDetail)
			{
				m_Histogram = new long[k_HistogramBuckets];
				m_DiagnosticSamples = new List<RuleTimingSample>(k_MaxDiagnosticSamples);
			}
		}

		public void AddAggregate(double milliseconds, int issueCount)
		{
			Evaluations++;
			Issues += issueCount;
			DirectMilliseconds += Math.Max(0.0, milliseconds);
		}

		public RuleTimingSample Add(string assetPath, double milliseconds, int issueCount, string analyzerName)
		{
			Evaluations++;
			Issues += issueCount;
			double direct = Math.Max(0.0, milliseconds);
			DirectMilliseconds += direct;
			if (!m_CollectDetail) return null;
			m_Histogram[HistogramBucket(direct)]++;
			var sample = new RuleTimingSample(assetPath, direct, issueCount, analyzerName);
			UpdateExtremes(sample);
			if (m_DiagnosticSamples.Count < k_MaxDiagnosticSamples) m_DiagnosticSamples.Add(sample);
			return sample;
		}

		public void AddEstimatedShared(double milliseconds) => EstimatedSharedMilliseconds += Math.Max(0.0, milliseconds);

		void UpdateExtremes(RuleTimingSample sample)
		{
			if (sample == null) return;
			if (m_Slowest1 == null || sample.TotalMilliseconds > m_Slowest1.TotalMilliseconds)
			{ m_Slowest2 = m_Slowest1; m_Slowest1 = sample; }
			else if (m_Slowest2 == null || sample.TotalMilliseconds > m_Slowest2.TotalMilliseconds) m_Slowest2 = sample;
			if (m_Fastest1 == null || sample.TotalMilliseconds < m_Fastest1.TotalMilliseconds)
			{ m_Fastest2 = m_Fastest1; m_Fastest1 = sample; }
			else if (m_Fastest2 == null || sample.TotalMilliseconds < m_Fastest2.TotalMilliseconds) m_Fastest2 = sample;
		}

		int HistogramBucket(double ms)
		{
			if (ms <= k_FirstBucketMilliseconds) return 0;
			double scaled = ms / k_FirstBucketMilliseconds;
			int bucket = 1;
			while (scaled > 2.0 && bucket < k_HistogramBuckets - 1) { scaled *= 0.5; bucket++; }
			return bucket;
		}

		public double Percentile(double percentile)
		{
			if (!m_CollectDetail || Evaluations <= 0) return 0.0;
			percentile = Math.Max(0.0, Math.Min(1.0, percentile));
			long target = Math.Max(1L, (long)Math.Ceiling(Evaluations * percentile));
			long seen = 0;
			for (int i = 0; i < m_Histogram.Length; i++)
			{
				seen += m_Histogram[i];
				if (seen >= target) return HistogramRepresentative(i);
			}
			return MaxMilliseconds;
		}

		double HistogramRepresentative(int bucket)
		{
			if (bucket <= 0) return k_FirstBucketMilliseconds;
			return k_FirstBucketMilliseconds * Math.Pow(2.0, bucket - 0.5);
		}

		public IReadOnlyList<RuleTimingSample> DiagnosticSamples => m_DiagnosticSamples;
		public RuleTimingSample Slowest1 => m_Slowest1;
		public RuleTimingSample Slowest2 => m_Slowest2;
		public RuleTimingSample Fastest1 => m_Fastest1;
		public RuleTimingSample Fastest2 => m_Fastest2;

		internal void MergeFrom(RuleTimingStats source)
		{
			Evaluations += source.Evaluations;
			Issues += source.Issues;
			DirectMilliseconds += source.DirectMilliseconds;
			EstimatedSharedMilliseconds += source.EstimatedSharedMilliseconds;
			if (!m_CollectDetail || !source.m_CollectDetail) return;
			for (int i = 0; i < m_Histogram.Length; i++) m_Histogram[i] += source.m_Histogram[i];
			for (int i = 0; i < source.m_DiagnosticSamples.Count && m_DiagnosticSamples.Count < k_MaxDiagnosticSamples; i++) m_DiagnosticSamples.Add(source.m_DiagnosticSamples[i]);
			UpdateExtremes(source.m_Slowest1); UpdateExtremes(source.m_Slowest2); UpdateExtremes(source.m_Fastest1); UpdateExtremes(source.m_Fastest2);
		}

		public void MarkIssueOnly(string assetPath) { Issues++; }
	}

	public sealed class AnalyzerRuleTimingInfo
	{
		public readonly string AnalyzerName;
		public double AnalyzerMilliseconds;
		public double RuleMilliseconds;
		public double SharedMilliseconds;
		public double AllocatedSharedMilliseconds;
		public int RuleSamples;
		public int InvocationCount;
		public int InvocationsWithRules;
		public int InvocationsWithoutRules;
		public double AnalyzerMillisecondsWithRules;
		public double AnalyzerMillisecondsWithoutRules;
		public AnalyzerRuleTimingInfo(string analyzerName) { AnalyzerName = analyzerName; }
	}
	public sealed class RuleTimingSample
	{
		public readonly string AssetPath;
		public readonly double Milliseconds;
		public double SharedMilliseconds;
		public readonly int Issues;
		public readonly string AnalyzerName;
		public double TotalMilliseconds => Milliseconds + SharedMilliseconds;
		public RuleTimingSample(string assetPath, double milliseconds, int issues, string analyzerName) { AssetPath = assetPath; Milliseconds = milliseconds; Issues = issues; AnalyzerName = analyzerName; }
	}
	public sealed class RuleTimingScope : IDisposable
	{
		ScanTiming m_Timing; double m_Start; bool m_Disposed;
		public string RuleId { get; private set; }
		public string AssetPath { get; private set; }
		public string AnalyzerName { get; private set; }
		public int IssueCount;
		public double NestedMilliseconds;
		public RuleTimingScope(ScanTiming timing, string ruleId, string assetPath, string analyzerName) { Reset(timing, ruleId, assetPath, analyzerName); }
		public void Reset(ScanTiming timing, string ruleId, string assetPath, string analyzerName) { m_Timing = timing; RuleId = ruleId; AssetPath = assetPath; AnalyzerName = analyzerName; IssueCount = 0; NestedMilliseconds = 0.0; m_Start = ScanTiming.Now; m_Disposed = false; }
		public void Dispose() { if (m_Disposed) return; m_Disposed = true; m_Timing.EndRule(this, ScanTiming.Now - m_Start, IssueCount); }
	}
	public static class RuleTiming
	{ public static IDisposable Begin(string ruleId, string assetPath) { var timing = ScanTiming.Active; return timing == null ? null : timing.BeginRule(ruleId, assetPath); } }

	/// <summary>The output of one full scan.</summary>
	public sealed class ScanResult
	{
		public DateTime ScanTime = DateTime.Now;
		public double DurationSeconds;
		public readonly ScanTiming Timing = new ScanTiming();

		public readonly List<AssetEntry> Entries = new List<AssetEntry>();
		public readonly Dictionary<AssetCategory, List<AssetEntry>> ByCategory =
			new Dictionary<AssetCategory, List<AssetEntry>>();

		public int ScannedAssets;
		public int CriticalCount;
		public int WarningCount;
		public int InfoCount;
		/// <summary>Memory and disk savings added together. Only meaningful for ranking.</summary>
		public long PotentialSavings;

		/// <summary>Runtime memory every fix in this result would free.</summary>
		public long PotentialMemorySavings;

		/// <summary>Bytes every fix in this result would take off the build.</summary>
		public long PotentialDiskSavings;

		public long TotalFileSize;

		/// <summary>Set when a targeted refresh may have left the duplicate / unused pass out of date.</summary>
		public bool CrossAssetPassesStale;

		/// <summary>What made the cross-asset passes stale, for the banner and the finding notes.</summary>
		public string CrossAssetStaleReason;

		/// <summary>Identity of the project-wide evidence used for cross-asset findings.</summary>
		public long CrossAssetEvidenceJournalGeneration = -1;
		public string CrossAssetEvidenceScopeFingerprint;
		public string CrossAssetEvidenceContentIdentity;
		public bool CrossAssetEvidenceComplete;

		public void CaptureCrossAssetEvidence(ReferenceIndex references, IReadOnlyList<string> allAssetPaths)
		{
			CrossAssetEvidenceJournalGeneration = references != null ? references.IndexedJournalGeneration : -1;
			CrossAssetEvidenceScopeFingerprint = references != null ? references.ScopeFingerprint : null;
			CrossAssetEvidenceContentIdentity = EvidenceContract.ComputeContentIdentity(allAssetPaths);
			bool graphIsCurrent = references != null
				&& references.IndexedJournalGeneration >= 0
				&& references.IndexedJournalGeneration == AssetDatabaseThreadSafeUtility.JournalGeneration;
			CrossAssetEvidenceComplete = references != null && references.IsProjectWide && references.IsComplete && graphIsCurrent
				&& CrossAssetEvidenceJournalGeneration >= 0 && !string.IsNullOrEmpty(CrossAssetEvidenceScopeFingerprint)
				&& !string.IsNullOrEmpty(CrossAssetEvidenceContentIdentity);
			if (!CrossAssetEvidenceComplete && references != null && references.IsComplete && !graphIsCurrent)
				CrossAssetPassesStale = true;
		}

		public void MarkCrossAssetEvidenceStale(string reason)
		{
			CrossAssetPassesStale = true;
			CrossAssetStaleReason = reason;
			CrossAssetEvidenceComplete = false;
		}

		/// <summary>
		/// True for the categories produced by a pass that looked at the whole project at once.
		/// Their findings are statements about relationships between assets, so a change to any one
		/// asset can falsify them.
		/// </summary>
		public static bool IsCrossAssetCategory(AssetCategory category)
		{
			return category == AssetCategory.Duplicate || category == AssetCategory.Unused
				|| category == AssetCategory.Build || category == AssetCategory.Package;
		}

		/// <summary>Categories whose rows are snapshot data produced by a project-wide/report pass rather than filesystem assets.</summary>
		public static bool IsPersistedDataCategory(AssetCategory category)
		{
			return category == AssetCategory.Build || category == AssetCategory.Package
				|| category == AssetCategory.BuildStep || category == AssetCategory.BuildFile;
		}

		/// <summary>
		/// Rules that live on a per-asset row but were decided by comparing assets to each other.
		/// These do not show up as a cross-asset category, so they have to be named.
		/// <para>
		/// Public so <see cref="ScanResultStore"/> can tell which issues on an otherwise per-asset row
		/// it must restore as data rather than by re-running that asset's own analyzer - see its
		/// remarks for why a project-wide comparison cannot be reproduced for one asset in isolation.
		/// </para>
		/// </summary>
		public static bool IsCrossAssetRule(string ruleId)
		{
			return ruleId == "MAT004" || ruleId == "MAT009";
		}

		/// <summary>
		/// Retires every finding that depended on comparing assets to one another, after something
		/// changed that could have altered the comparison.
		/// <para>
		/// A duplicate group is a claim about file contents, an "unused" verdict a claim about the
		/// reference graph, a material family a claim about other materials. Rewriting one prefab can
		/// falsify all three, and the rows saying so stay on screen with their Fix buttons intact -
		/// buttons wired to a reference graph captured before the change. Flagging the result as
		/// stale was not enough on its own: it was displayed only when the settings had also changed,
		/// and it disabled nothing.
		/// </para>
		/// </summary>
		/// <returns>How many findings were retired.</returns>
		public int InvalidateCrossAssetFindings(string reason)
		{
			MarkCrossAssetEvidenceStale(reason);

			int retired = 0;
			foreach (var entry in Entries)
			{
				bool crossAssetRow = IsCrossAssetCategory(entry.Category);
				for (int i = 0; i < entry.Issues.Count; i++)
				{
					var issue = entry.Issues[i];
					if (issue.Stale) continue;
					if (!crossAssetRow && !IsCrossAssetRule(issue.RuleId)) continue;

					issue.MarkStale(reason);
					retired++;
				}
			}
			return retired;
		}

		/// <summary>
		/// Why this scan does not describe the whole project. Empty when it does.
		/// <para>
		/// Every reason is recorded, including the deliberate ones - a folder-scoped run is
		/// incomplete on purpose, and saying so is not a complaint. What matters is that "no findings
		/// here" and "not looked at" are never the same sentence: a clean-looking report produced by
		/// a scan that skipped half the project is the most misleading thing this tool could show.
		/// </para>
		/// </summary>
		public readonly List<string> CompletenessCaveats = new List<string>();

		/// <summary>Important analysis limitations that do not make the scan incomplete, but can weaken the
		/// interpretation of particular finding families (for example, dynamic asset loading). These are
		/// deliberately separate from <see cref="CompletenessCaveats"/> so a complete static scan is not
		/// mislabeled as incomplete merely because it cannot prove runtime behavior.
		/// </summary>
		public readonly List<string> AnalysisLimitations = new List<string>();

		public void AddLimitation(string limitation)
		{
			if (string.IsNullOrEmpty(limitation) || AnalysisLimitations.Contains(limitation)) return;
			AnalysisLimitations.Add(limitation);
		}

		/// <summary>
		/// True when the scan was stopped before it finished - cancelled, or aborted by the
		/// watchdog. Findings from an aborted scan describe whatever it got through, so nothing may
		/// be concluded from an absence in them.
		/// </summary>
		public bool Aborted;

		public bool IsComplete => !Aborted && CompletenessCaveats.Count == 0;

		public void AddCaveat(string caveat)
		{
			if (string.IsNullOrEmpty(caveat) || CompletenessCaveats.Contains(caveat)) return;
			CompletenessCaveats.Add(caveat);
		}

		/// <summary>How many assets could not be analyzed at all, and by which analyzer.</summary>
		public readonly Dictionary<string, int> FailuresByAnalyzer = new Dictionary<string, int>();

		public int FailedAssets;

		/// <summary>What this scan was pointed at, and what it deliberately left out.</summary>
		public ScanScope Scope;

		/// <summary>The settings revision this scan ran under. See <see cref="OptimizerSettings.Revision"/>.</summary>
		public int SettingsRevision;

		/// <summary>
		/// Assets that took long enough to be felt as a freeze, worst first. Analyzing one asset is
		/// atomic, so these are exactly the files that make the editor look hung - naming them turns
		/// an unexplained stall into something the user can act on.
		/// </summary>
		public readonly List<KeyValuePair<string, double>> SlowAssets = new List<KeyValuePair<string, double>>();

		public int IssueCount => CriticalCount + WarningCount + InfoCount;

		public List<AssetEntry> For(params AssetCategory[] categories)
		{
			var list = new List<AssetEntry>();
			foreach (var category in categories)
				if (ByCategory.TryGetValue(category, out var entries)) list.AddRange(entries);
			return list;
		}

		public void Add(AssetEntry entry)
		{
			Entries.Add(entry);
			if (!ByCategory.TryGetValue(entry.Category, out var list))
			{
				list = new List<AssetEntry>();
				ByCategory[entry.Category] = list;
			}
			list.Add(entry);
		}

		/// <summary>
		/// Drops entries for these paths so they can be re-analyzed in place. Only categories
		/// produced by per-asset analyzers are eligible: rows owned by the cross-asset passes
		/// (duplicates, unused) would not be regenerated by a targeted refresh, so removing them
		/// would silently lose findings.
		/// </summary>
		public int RemovePaths(HashSet<string> paths, Func<AssetEntry, bool> extra = null)
		{
			bool Match(AssetEntry e)
			{
				if (extra != null && extra(e)) return true;
				return paths.Contains(e.Path) && IsPerAssetCategory(e.Category);
			}

			int removed = Entries.RemoveAll(Match);
			foreach (var pair in ByCategory) pair.Value.RemoveAll(Match);
			return removed;
		}

		/// <summary>
		/// Drops every row, in any category, whose asset no longer exists on disk. Scoped to the paths
		/// a fix batch touched, so an unrelated row cannot disappear because of a stale check.
		/// </summary>
		public int RemoveDeleted(HashSet<string> paths)
		{
			if (paths == null || paths.Count == 0) return 0;

			var gone = new HashSet<string>(StringComparer.Ordinal);
			foreach (var path in paths)
			{
				if (string.IsNullOrEmpty(path)) continue;
				if (!System.IO.File.Exists(AssetPathUtility.ToAbsolutePath(path))
					&& !System.IO.Directory.Exists(AssetPathUtility.ToAbsolutePath(path)))
					gone.Add(path);
			}
			if (gone.Count == 0) return 0;

			bool Match(AssetEntry e) => gone.Contains(e.Path);
			int removed = Entries.RemoveAll(Match);
			foreach (var pair in ByCategory) pair.Value.RemoveAll(Match);
			return removed;
		}

		public static bool IsPerAssetCategory(AssetCategory category)
		{
			switch (category)
			{
				case AssetCategory.Texture:
				case AssetCategory.Audio:
				case AssetCategory.Model:
				case AssetCategory.Material:
				case AssetCategory.Shader:
				case AssetCategory.Animation:
				case AssetCategory.Prefab:
				case AssetCategory.Scene:
				case AssetCategory.Script:
				case AssetCategory.Font:
				case AssetCategory.SpriteAtlas:
				case AssetCategory.Video:
				case AssetCategory.Controller:
					return true;
				default:
					return false;
			}
		}

		/// <summary>Rolls the headline counters back up from the entries.</summary>
		public void Recount()
		{
			CriticalCount = 0;
			WarningCount = 0;
			InfoCount = 0;
			PotentialSavings = 0;
			PotentialMemorySavings = 0;
			PotentialDiskSavings = 0;
			TotalFileSize = 0;

			foreach (var entry in Entries)
			{
				if (IsPerAssetCategory(entry.Category))
					TotalFileSize += Math.Max(0, entry.FileSize);
				PotentialSavings += entry.PotentialSavings;
				PotentialMemorySavings += entry.PotentialMemorySavings;
				PotentialDiskSavings += entry.PotentialDiskSavings;
				for (int i = 0; i < entry.Issues.Count; i++)
				{
					switch (entry.Issues[i].Severity)
					{
						case IssueSeverity.Critical: CriticalCount++; break;
						case IssueSeverity.Warning: WarningCount++; break;
						default: InfoCount++; break;
					}
				}
			}
		}

		/// <summary>Restores stable scan ordering after parallel materialization.</summary>
		public void ReorderDeterministically()
		{
			Entries.Sort(CompareEntriesDeterministically);
			ByCategory.Clear();
			for (int i = 0; i < Entries.Count; i++)
			{
				var entry = Entries[i];
				if (entry == null) continue;
				if (!ByCategory.TryGetValue(entry.Category, out var list))
					ByCategory[entry.Category] = list = new List<AssetEntry>();
				list.Add(entry);
			}
		}

		static int CompareEntriesDeterministically(AssetEntry a, AssetEntry b)
		{
			if (ReferenceEquals(a, b)) return 0;
			if (a == null) return -1;
			if (b == null) return 1;
			int c = a.ScanOrder.CompareTo(b.ScanOrder);
			if (c != 0) return c;
			c = a.Category.CompareTo(b.Category);
			if (c != 0) return c;
			c = StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
			if (c != 0) return c;
			c = StringComparer.Ordinal.Compare(a.Guid, b.Guid);
			if (c != 0) return c;
			return StringComparer.Ordinal.Compare(a.Name, b.Name);
		}

		public void ReplaceCategories(
	IEnumerable<AssetEntry> replacement,
	params AssetCategory[] categories)
		{
			if (categories == null || categories.Length == 0)
				return;

			var categorySet = new HashSet<AssetCategory>(categories);

			// Remove the old rows from the master list.
			Entries.RemoveAll(entry =>
				entry != null && categorySet.Contains(entry.Category));

			// Remove the old rows from the category index.
			foreach (var category in categories)
			{
				if (ByCategory.TryGetValue(category, out var list))
				{
					list.Clear();
				}
			}

			// Add the newly generated rows through the normal Add() path so
			// Entries and ByCategory remain consistent.
			if (replacement != null)
			{
				foreach (var entry in replacement)
				{
					if (entry == null)
						continue;

					Add(entry);
				}
			}

			// Keep all headline counts/savings consistent with the modified result.
			Recount();
		}
	}

	/// <summary>
	/// Walks the project in time-sliced steps so the editor stays responsive.
	/// Call <see cref="Begin"/>, then <see cref="Tick"/> from EditorApplication.update until <see cref="IsRunning"/> is false.
	/// </summary>
	public sealed class ProjectScanner
	{
		/// <summary>
		/// Analyzers load textures, meshes and clips to measure them, and those stay resident for the
		/// rest of the scan. The loaded set is swept periodically once enough assets have actually
		/// been loaded - sweeping is itself expensive, so it is driven by loads, not by index.
		/// </summary>
		const int k_UnloadEveryLoads = 1500;
		const int k_UnloadThreshold = 2000;

		/// <summary>
		/// A sweep walks every loaded object, so its cost grows with the project. On a very large one
		/// the sweeps themselves become a visible part of the scan, so the interval is stretched to
		/// keep their total share roughly constant.
		/// </summary>
		const int k_LargeProjectAssets = 50000;

		/// <summary>An asset slower than this is recorded, so a stall can be attributed to a file.</summary>
		const double k_SlowAssetMs = 750.0;
		const int k_MaxSlowAssetsTracked = 40;

		/// <summary>
		/// Analyzer failures are logged individually up to this many, then only counted. A project
		/// with a systemic problem would otherwise put one warning per asset into the console, which
		/// is slow in itself and buries anything worth reading.
		/// </summary>
		const int k_MaxLoggedFailures = 25;

		/// <summary>The status line is rebuilt at most this often; it is UI, not instrumentation.</summary>
		const double k_StatusIntervalMs = 120.0;

		/// <summary>
		/// Yielded by a step that wants the editor to repaint before the next one starts, rather than
		/// merely offering a slice back. Used ahead of work that blocks for a long time and cannot be
		/// broken up: without it the status explaining the pause is written but never drawn.
		/// </summary>
		const int k_RepaintNow = 1;

		/// <summary>Extensions whose fixes rewrite the asset file itself, not just its .meta.</summary>
		static readonly HashSet<string> k_InPlaceEdited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".mat", ".anim", ".prefab", ".asset", ".controller", ".unity", ".shadergraph"
		};

		readonly List<IAssetAnalyzer> m_AssetAnalyzers = new List<IAssetAnalyzer>();
		readonly List<IProjectAnalyzer> m_ProjectAnalyzers = new List<IProjectAnalyzer>();
		readonly ProjectSettingsAnalyzer m_SettingsAnalyzer;
		readonly MaterialShaderAnalyzer m_MaterialAnalyzer;
		readonly Dictionary<AssetKind, List<IAssetAnalyzer>> m_AnalyzersByKind =
			new Dictionary<AssetKind, List<IAssetAnalyzer>>();
		readonly List<IAssetAnalyzer> m_FallbackAnalyzers = new List<IAssetAnalyzer>();

		/// <summary>
		/// Estimates are padded by this much. A scan that beats its estimate feels fast; one that
		/// overruns feels broken, so the error is spent on the side that costs nothing.
		/// </summary>
		const double k_EstimateSafety = 1.15;

		/// <summary>
		/// With no history, how much more expensive the remaining bar is assumed to be per unit of
		/// progress than the part already covered.
		/// </summary>
		const double k_UnknownTailPenalty = 0.6;

		/// <summary>
		/// How far the estimate has to overshoot the standing promise before the promise is raised.
		/// Wide enough that normal variation never pushes the countdown backwards.
		/// </summary>
		const double k_ReQuoteThreshold = 8.0;

		IEnumerator<int> m_Steps;
		ScanContext m_Context;
		Stopwatch m_Stopwatch;
		ScanCache m_Cache;
		int m_Failures;

		ScanProfile m_Profile;
		ScanProfile.Recorder m_Shape;
		CancellationTokenSource m_FileMetadataCts;
		CancellationTokenSource m_BackgroundCts;
		Task<AssetDatabaseThreadSafeBuildResult> m_ThreadSafeAssetIndexTask;
		BackgroundAssetScheduler m_BackgroundAssetScheduler;
		BackgroundAssetStreamState m_BackgroundAssetStream;
		Task<BackgroundProjectBatchResult> m_BackgroundProjectTask;
		BackgroundAssetContext m_BackgroundAssetContext;
		BackgroundProjectContext m_BackgroundProjectContext;
		readonly Dictionary<IBackgroundAssetAnalyzer, BitArray> m_BackgroundScheduledByAnalyzer = new Dictionary<IBackgroundAssetAnalyzer, BitArray>();
		long m_BackgroundAssetNextOrdinal;
		double m_BackgroundAssetExtractionMilliseconds;
		AssetLoadPrefetcher m_AssetLoadPrefetcher;

		/// <summary>What shape of scan this is, so its timing history is not mixed with another's.</summary>
		string m_ProfileKey;

		/// <summary>The lowest remaining time promised so far, and when it was promised.</summary>
		double m_QuotedRemaining = -1.0;
		double m_QuotedAt;

		public bool IsRunning { get; private set; }
		public float Progress { get; private set; }
		public string Status { get; private set; } = string.Empty;

		/// <summary>How long the running scan has been going.</summary>
		public double ElapsedSeconds => m_Stopwatch != null ? m_Stopwatch.Elapsed.TotalSeconds : 0.0;

		/// <summary>
		/// Seconds still to go, or -1 while there is not enough to go on.
		/// <para>
		/// Predicted from the shape of the previous scan rather than from progress alone. Progress is
		/// not proportional to time - the project-wide passes are the last fifth of the bar and can be
		/// a third of the run - so extrapolating "98% done, therefore nearly finished" produces a
		/// small number that stays small however long the tail actually takes.
		/// </para>
		/// <para>
		/// The result is biased upwards and only ever falls. Finishing sooner than promised is a
		/// pleasant surprise; a countdown that stalls, or climbs, reads as a stuck program. Whatever
		/// is shown is therefore the smallest figure predicted so far, less the time since it was
		/// predicted, so it always ticks down at real speed even when the model is wrong.
		/// </para>
		/// </summary>
		public double RemainingSeconds
		{
			get
			{
				if (!IsRunning) return -1.0;

				double elapsed = ElapsedSeconds;
				if (elapsed < 1.5 || Progress < 0.02f) return -1.0;

				double predicted = -1.0;
				if (m_Profile != null && m_Profile.IsUsable)
					predicted = m_Profile.PredictTotalSeconds(Progress, elapsed);

				// No usable history: fall back to straight extrapolation, but assume the untravelled
				// part of the bar is more expensive per unit than the part already covered, which on
				// every project shape measured here it is.
				if (predicted <= 0.0)
				{
					double linear = elapsed / Mathf.Max(0.02f, Progress);
					predicted = linear * (1.0 + k_UnknownTailPenalty * (1.0 - Progress));
				}

				double remaining = (predicted * k_EstimateSafety) - elapsed;
				if (remaining < 0.0) remaining = 0.0;

				// Hold the promise made so far and let real time eat into it, so the number moves
				// every second regardless of what the model does.
				double sinceQuoted = elapsed - m_QuotedAt;
				double quoted = m_QuotedRemaining - sinceQuoted;

				// Falling is free - beating the estimate is the pleasant direction. Rising is
				// allowed only past a dead band, so a badly wrong estimate is corrected instead of
				// decaying to zero and sitting at "finishing up", without the figure jittering
				// upward every tick on ordinary noise.
				bool firstQuote = m_QuotedRemaining < 0.0;
				bool better = remaining < quoted;
				bool clearlyWorse = remaining > quoted + k_ReQuoteThreshold;

				if (firstQuote || better || clearlyWorse)
				{
					m_QuotedRemaining = remaining;
					m_QuotedAt = elapsed;
					quoted = remaining;
				}

				return quoted > 0.0 ? quoted : 0.0;
			}
		}
		public ScanResult Result { get; private set; }
		public event Action<ScanResult> Completed;

		public ProjectScanner()
		{
			m_MaterialAnalyzer = new MaterialShaderAnalyzer();
			m_SettingsAnalyzer = new ProjectSettingsAnalyzer();

			// Asset analyzers: primary content first, then derived/specialized content.
			m_AssetAnalyzers.Add(new TextureAnalyzer());
			m_AssetAnalyzers.Add(new ModelAnalyzer());
			m_AssetAnalyzers.Add(new AnimationAnalyzer());
			m_AssetAnalyzers.Add(new AnimatorControllerAnalyzer());
			m_AssetAnalyzers.Add(m_MaterialAnalyzer);
			m_AssetAnalyzers.Add(new PrefabSceneAnalyzer());
			m_AssetAnalyzers.Add(new SpriteAtlasAnalyzer());
			m_AssetAnalyzers.Add(new AudioAnalyzer());
			m_AssetAnalyzers.Add(new VideoAnalyzer());
			m_AssetAnalyzers.Add(new FontAnalyzer());
			m_AssetAnalyzers.Add(new TextMeshProAnalyzer());
			m_AssetAnalyzers.Add(new ScriptAnalyzer());
			m_AssetAnalyzers.Add(new WorldDataAnalyzer());

			foreach (var analyzer in AnalyzerRegistry.CreateAssetAnalyzers())
			{
				bool duplicate = false;
				for (int i = 0; i < m_AssetAnalyzers.Count; i++) if (m_AssetAnalyzers[i].GetType() == analyzer.GetType()) { duplicate = true; break; }
				if (!duplicate) m_AssetAnalyzers.Add(analyzer);
			}

			// Project analyzers: shared/project-wide state first, then supplemental
			// analysis, with unused/duplicate analysis last.
			m_ProjectAnalyzers.Add(new BuildProfileAnalyzer());
			m_ProjectAnalyzers.Add(new AndroidCompatibilityAnalyzer());
			m_ProjectAnalyzers.Add(m_SettingsAnalyzer);
			m_ProjectAnalyzers.Add(new ProjectAuditorSupplementAnalyzer());
			m_ProjectAnalyzers.Add(new AddressablesBuildLayoutAnalyzer());
			m_ProjectAnalyzers.Add(new RenderingEvidenceAnalyzer());
			m_ProjectAnalyzers.Add(m_MaterialAnalyzer);
			m_ProjectAnalyzers.Add(new DuplicateAnalyzer());
			m_ProjectAnalyzers.Add(new UnusedAnalyzer());

			// Optional dependency/assembly analyzers are evaluated after the reference graph has been built.
			foreach (var analyzer in AnalyzerRegistry.CreateProjectAnalyzers())
			{
				bool duplicate = false;
				for (int i = 0; i < m_ProjectAnalyzers.Count; i++) if (m_ProjectAnalyzers[i].GetType() == analyzer.GetType()) { duplicate = true; break; }
				if (!duplicate) m_ProjectAnalyzers.Add(analyzer);
			}

			BuildAnalyzerRouting();
		}

		/// <summary>
		/// Narrows the per-file analyzer set using the already-known extension classification. The old
		/// scanner asked every analyzer whether it handled every path; on large projects that is millions
		/// of virtual calls and string comparisons that can never produce work. Handles() remains the
		/// final correctness gate for overlapping/special cases.
		/// </summary>
		void BuildAnalyzerRouting()
		{
			for (int i = 0; i < m_AssetAnalyzers.Count; i++)
			{
				var analyzer = m_AssetAnalyzers[i];
				AddRoutesForMask(analyzer.SupportedKinds, analyzer);
			}
		}

		void AddRoutesForMask(AssetKindMask mask, IAssetAnalyzer analyzer)
		{
			for (int i = 0; i < AssetKinds.All.Length; i++)
			{
				var kind = AssetKinds.All[i];
				if ((mask & (AssetKindMask)(1 << (int)kind)) != 0) AddRoutes(kind, analyzer);
			}
			if ((mask & AssetKindMask.Other) != 0) AddFallback(analyzer);
		}

		bool AnalyzerNeeded(AssetKindMask mask, Func<AssetKind, bool> enabled, bool materialDuplicateWarmup)
		{
			for (int i = 0; i < AssetKinds.All.Length; i++)
			{
				var kind = AssetKinds.All[i];
				if ((mask & (AssetKindMask)(1 << (int)kind)) != 0 && enabled(kind)) return true;
			}
			return materialDuplicateWarmup;
		}

		void AddRoutes(AssetKind kind, IAssetAnalyzer analyzer)
		{
			if (!m_AnalyzersByKind.TryGetValue(kind, out var list))
				m_AnalyzersByKind[kind] = list = new List<IAssetAnalyzer>();
			if (!list.Contains(analyzer)) list.Add(analyzer);
		}

		void AddFallback(IAssetAnalyzer analyzer)
		{
			if (!m_FallbackAnalyzers.Contains(analyzer)) m_FallbackAnalyzers.Add(analyzer);
		}

		/// <summary>
		/// Starts a scan, if the editor can be held still for it.
		/// <para>
		/// Returns false when the guard could not take both editor locks. A scan without them is
		/// walking a project that Unity may recompile or reimport underneath it, so there is no
		/// useful degraded mode - it does not start.
		/// </para>
		/// </summary>
		public bool Begin(OptimizerSettings settings)
		{
			Cancel();

			// The guard comes first, before any state is set up. Nothing should look like a running
			// scan until the editor is actually held still.
			if (!ScanGuard.TryAcquire())
			{
				Status = "Could not start: the editor could not be held still for a scan.";
				return false;
			}

			ScanGuard.WatchdogExpired += OnWatchdogExpired;

			m_Context = new ScanContext { Settings = settings };
			Result = new ScanResult { SettingsRevision = OptimizerSettings.Revision };
			m_Stopwatch = Stopwatch.StartNew();
			m_Failures = 0;
			m_FileMetadataCts?.Dispose();
			m_FileMetadataCts = new CancellationTokenSource();
			m_ThreadSafeAssetIndexTask = null;
			m_BackgroundCts?.Cancel();
			m_BackgroundCts?.Dispose();
			m_BackgroundCts = new CancellationTokenSource();
			m_BackgroundAssetScheduler = null;
			m_BackgroundAssetStream = null;
			m_BackgroundProjectTask = null;
			m_BackgroundAssetContext = null;
			m_BackgroundProjectContext = null;
			m_BackgroundScheduledByAnalyzer.Clear();
			m_BackgroundAssetNextOrdinal = 0;
			m_BackgroundAssetExtractionMilliseconds = 0.0;
			m_AssetLoadPrefetcher?.Dispose();
			m_AssetLoadPrefetcher = null;

			// The profile is loaded once the scope is resolved, because its key includes how many
			// assets this run covers and that is not known yet.
			m_Profile = null;
			m_ProfileKey = null;
			m_Shape = new ScanProfile.Recorder();
			m_QuotedRemaining = -1.0;
			m_QuotedAt = 0.0;
			IsRunning = true;
			Progress = 0f;
			Status = "Collecting assets...";
			m_Steps = Run();
			return true;
		}

		public void Cancel()
		{
			Stop("Cancelled", markResultIncomplete: IsRunning);
		}

		/// <summary>
		/// The watchdog decided this scan is gone. It is stopped here, while the guard still holds
		/// the editor locks - the guard releases them immediately afterwards.
		/// <para>
		/// Previously the watchdog only released the locks. The scan kept running against an asset
		/// database that could now refresh and a domain that could now reload, which is precisely
		/// the state the guard exists to prevent; the warning said the scan had been "abandoned"
		/// while it was in fact still working.
		/// </para>
		/// </summary>
		void OnWatchdogExpired()
		{
			if (!IsRunning) return;

			Result?.AddCaveat("The scan was aborted by the watchdog after it stopped responding, so these "
							  + "results cover only part of the project.");
			Stop("Aborted - the scan stopped responding", markResultIncomplete: true);
		}

		/// <summary>
		/// Ends the scan and puts the editor back. Idempotent: every exit path funnels through here,
		/// and calling it twice releases nothing twice.
		/// </summary>
		static void ObserveBackgroundFault(Task task)
		{
			if (task == null) return;
			if (task.IsCompleted && task.IsFaulted)
			{
				_ = task.Exception;
				return;
			}
			_ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
		}

		void Stop(string status, bool markResultIncomplete)
		{
			bool wasRunning = IsRunning;

			// Cleared before anything else, so a re-entrant call - a watchdog firing while Cancel is
			// running - cannot start unwinding a second time.
			IsRunning = false;
			m_Steps?.Dispose();
			m_Steps = null;
			m_FileMetadataCts?.Cancel();
			m_FileMetadataCts?.Dispose();
			m_FileMetadataCts = null;
			ObserveBackgroundFault(m_ThreadSafeAssetIndexTask);
			m_ThreadSafeAssetIndexTask = null;
			m_BackgroundCts?.Cancel();
			if (m_BackgroundAssetScheduler != null)
			{
				m_BackgroundAssetScheduler.Cancel();
				ObserveBackgroundFault(m_BackgroundAssetScheduler.Completion);
			}
			ObserveBackgroundFault(m_BackgroundProjectTask);
			m_BackgroundCts?.Dispose();
			m_BackgroundCts = null;
			m_AssetLoadPrefetcher?.Dispose();
			m_AssetLoadPrefetcher = null;
			Status = status;

			if (markResultIncomplete && Result != null) Result.Aborted = true;

			if (!wasRunning) return;

			ScanGuard.WatchdogExpired -= OnWatchdogExpired;
			ScanGuard.Release();
		}

		/// <summary>Advances the scan for at most <paramref name="budgetMs"/> milliseconds.</summary>
		public void Tick(double budgetMs)
		{
			if (!IsRunning || m_Steps == null) return;

			// Re-checked after every step below as well: the watchdog can fire from
			// EditorApplication.update part-way through this loop, and a step taken after that is a
			// step taken with the editor no longer held still.
			ScanGuard.Heartbeat();
			m_Shape?.Sample(Progress, ElapsedSeconds);

			var watch = Stopwatch.StartNew();
			try
			{
				while (watch.Elapsed.TotalMilliseconds < budgetMs)
				{
					if (!IsRunning || m_Steps == null) return;

					if (!m_Steps.MoveNext())
					{
						Finish();
						return;
					}
					if (m_Steps.Current == k_RepaintNow) break;
				}
			}
			catch (Exception e)
			{
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Scan failed: " + e);
				Finish();
			}
		}

		void Finish()
		{
			bool wasRunning = IsRunning;
			IsRunning = false;
			m_Steps?.Dispose();
			m_Steps = null;
			m_FileMetadataCts?.Cancel();
			m_FileMetadataCts?.Dispose();
			m_FileMetadataCts = null;
			ObserveBackgroundFault(m_ThreadSafeAssetIndexTask);
			m_ThreadSafeAssetIndexTask = null;
			m_BackgroundCts?.Cancel();
			if (m_BackgroundAssetScheduler != null)
			{
				m_BackgroundAssetScheduler.Cancel();
				ObserveBackgroundFault(m_BackgroundAssetScheduler.Completion);
			}
			ObserveBackgroundFault(m_BackgroundProjectTask);
			m_BackgroundCts?.Dispose();
			m_BackgroundCts = null;
			m_AssetLoadPrefetcher?.Dispose();
			m_AssetLoadPrefetcher = null;
			Progress = 1f;
			Status = "Done";
			if (m_Stopwatch != null)
			{
				// Stopped, not just read: ElapsedSeconds is what the window reports, and a clock that
				// keeps counting after the scan ends disagrees with the total in the summary.
				m_Stopwatch.Stop();
				double total = m_Stopwatch.Elapsed.TotalSeconds;
				if (Result != null) Result.DurationSeconds = total;

				// Record how this run was shaped so the next scan of the same shape can predict
				// from it. A cancelled run is not recorded: its tail was never measured.
				if (m_Shape != null && Progress >= 0.999f && !string.IsNullOrEmpty(m_ProfileKey))
					m_Shape.Build(total, m_ProfileKey).Save();
			}

			// Released before the completion callback: publishing a result rebuilds the UI, and any
			// exception thrown in there must not be able to strand the editor with a lock held.
			if (wasRunning)
			{
				ScanGuard.WatchdogExpired -= OnWatchdogExpired;
				ScanGuard.Release();
			}
			Completed?.Invoke(Result);
		}

		/// <summary>
		/// Logs an analyzer failure, up to a cap. Beyond that they are only counted: a project where
		/// every asset of a type fails would otherwise flood the console with the same message.
		/// </summary>
		void ReportFailure(string what, string path, Exception e)
		{
			m_Failures++;

			// Also recorded on the result. A console message scrolls away, and "12 assets could not
			// be analyzed" is the difference between a clean report and a report with holes in it.
			if (Result != null)
			{
				Result.FailedAssets++;
				Result.FailuresByAnalyzer.TryGetValue(what, out int count);
				Result.FailuresByAnalyzer[what] = count + 1;
			}

			if (m_Failures <= k_MaxLoggedFailures)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" {what} failed on '{path}': {e}");
				if (m_Failures == k_MaxLoggedFailures)
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Further analyzer failures will be counted, not logged.");
			}
		}

		// --- incremental refresh -------------------------------------------------------

		/// <summary>
		/// Re-analyzes only the given assets and patches them into the existing result, instead of
		/// throwing the whole scan away. This is what runs after a fix: fixing one texture should
		/// cost one texture's worth of work, not a full project sweep.
		/// </summary>
		public int RefreshPaths(IReadOnlyList<string> paths)
		{
			if (Result == null || m_Context == null || paths == null || paths.Count == 0) return 0;

			var assetPaths = new HashSet<string>(StringComparer.Ordinal);
			bool touchedProjectSettings = false;
			foreach (var path in paths)
			{
				if (string.IsNullOrEmpty(path)) continue;
				if (path.StartsWith("ProjectSettings/", StringComparison.Ordinal)) touchedProjectSettings = true;
				else assetPaths.Add(path);
			}

			// Project settings entries are produced as a set, so they are rebuilt as a set.
			Result.RemovePaths(assetPaths,
				touchedProjectSettings ? (Func<AssetEntry, bool>)(e => e.Category == AssetCategory.ProjectSetting) : null);

			// A fix that deletes its asset - a stray file, a collapsed duplicate - leaves a row
			// describing a file that is no longer there. Those rows belong to the cross-asset passes,
			// which RemovePaths deliberately will not touch, so they are dropped by existence here
			// instead: keeping them would invite a second click on a fix that cannot run again.
			Result.RemoveDeleted(assetPaths);

			int rebuilt = 0;
			foreach (var path in assetPaths)
			{
				var absolutePath = AssetPathUtility.ToAbsolutePath(path);
				if (!System.IO.File.Exists(absolutePath) && !System.IO.Directory.Exists(absolutePath)) continue;
				m_Context.FileMetadata?.SetFileSize(path, FileUtility.FileSizeOf(path));
				m_Cache?.Invalidate(path);

				var entries = AnalyzeAsset(path, m_Context);
				foreach (var entry in entries)
				{
					Result.Add(entry);
					rebuilt++;
				}
			}

			if (touchedProjectSettings)
			{
				foreach (var entry in m_SettingsAnalyzer.Analyze(m_Context))
				{
					if (entry == null) continue;
					Result.Add(entry);
					rebuilt++;
				}
			}

			// Duplicate hashes, the reference graph and build reachability all depend on file
			// content, which an in-place asset edit - a material, prefab, scene or clip rewrite -
			// changes. Importer-only fixes leave the source file untouched, so they cannot
			// invalidate those passes.
			//
			// A deleted asset does the same thing from the other direction: whatever referenced it,
			// and whatever it was a duplicate of, are now describing a project that has moved on.
			string invalidatedBy = null;
			foreach (var path in assetPaths)
			{
				if (k_InPlaceEdited.Contains(System.IO.Path.GetExtension(path)))
				{
					invalidatedBy = path;
					break;
				}
			}

			if (invalidatedBy == null)
			{
				foreach (var path in assetPaths)
				{
					if (!System.IO.File.Exists(AssetPathUtility.ToAbsolutePath(path)))
					{
						invalidatedBy = path;
						break;
					}
				}
			}

			if (invalidatedBy != null)
			{
				// The findings are kept and their fixes retired, rather than the rows being dropped.
				// They were true when they were made and probably still are; what is no longer true
				// is that the tool can act on them without looking again.
				Result.InvalidateCrossAssetFindings(
					"'" + invalidatedBy + "' changed after this scan, so duplicate, unused and "
					+ "reachability findings need a fresh pass before they can be acted on.");
			}

			Result.Recount();
			m_Cache?.Save();
			return rebuilt;
		}

		/// <summary>
		/// Runs every per-asset analyzer that handles <paramref name="path"/> against it, fresh, and
		/// returns whatever they produce. The one place both <see cref="RefreshPaths"/> and
		/// <see cref="ScanResultStore"/> get a live, fully-functional <see cref="AssetEntry"/> - Fix and
		/// Verify delegates included - for a single asset without re-running the whole scan.
		/// <para>
		/// Only per-asset analyzers are involved; nothing here touches <see cref="IProjectAnalyzer"/>.
		/// Those need a walk over the whole reference scope and cannot be reproduced for one asset in
		/// isolation - see <see cref="ScanResultStore"/>'s remarks on why cross-asset findings are
		/// restored as data rather than re-derived.
		/// </para>
		/// </summary>
		public List<AssetEntry> AnalyzeAsset(string path, ScanContext ctx)
		{
			var results = new List<AssetEntry>();
			AssetKind kind = AssetKinds.Of(path);
			if (!m_AnalyzersByKind.TryGetValue(kind, out var routed)) routed = m_FallbackAnalyzers;
			try
			{
				for (int a = 0; a < routed.Count; a++)
				{
					var analyzer = routed[a];
					if (!analyzer.Handles(path)) continue;
					try
					{
						var entry = analyzer.Analyze(path, ctx);
						if (entry != null) results.Add(entry);
					}
					catch (Exception e)
					{
						ReportFailure(analyzer.GetType().Name, path, e);
					}
				}
			}
			finally
			{
				ctx.ClearLoadedObjects(path);
			}
			return results;
		}

		/// <summary>
		/// A minimal <see cref="ScanContext"/> for re-analyzing individual assets outside of a full
		/// scan - used by <see cref="ScanResultStore"/> to rebuild a completed scan's per-asset findings
		/// after a domain reload. Deliberately does not populate <see cref="ScanContext.References"/>,
		/// <see cref="ScanContext.MaterialUsage"/>, <see cref="ScanContext.ReachableAssets"/> or
		/// <see cref="ScanContext.BuildSet"/>: no per-asset <see cref="IAssetAnalyzer.Analyze"/> method
		/// reads them (only the project-wide passes do, and they null-check defensively), so leaving
		/// them unset costs nothing and avoids pretending a partial context is a real scan's context.
		/// <para>
		/// Loads the same <see cref="ScanCache"/> a real scan would, so re-analyzing an asset whose
		/// dependency hash has not moved since the last scan is a cache hit, not a re-measure - this is
		/// what keeps a restore fast rather than turning it into a scan by another name.
		/// </para>
		/// </summary>
		public ScanContext PrepareRestoreContext(OptimizerSettings settings)
		{
			var ctx = new ScanContext { Settings = settings, Cache = ScanCache.Load(settings) };
			for (int a = 0; a < m_AssetAnalyzers.Count; a++)
			{
				var analyzer = m_AssetAnalyzers[a];
				bool needed = AnalyzerNeeded(analyzer.SupportedKinds, kind => ctx.Settings.IsKindEnabled(kind), analyzer == m_MaterialAnalyzer && ctx.Settings.scanDuplicates);
				if (needed) analyzer.BeginScan(ctx);
			}
			return ctx;
		}

		/// <summary>
		/// Adopts a restored scan and the context <see cref="PrepareRestoreContext"/> built for it as
		/// this scanner's own - the same way <see cref="Begin"/> would have, had this scanner actually
		/// produced them.
		/// <para>
		/// <see cref="RefreshPaths"/>, which every fix and review action routes through afterwards to
		/// update the UI, works only against <see cref="Result"/> and this scanner's own context; both
		/// are otherwise null until a scan runs. Without this call, a fix applied right after a restored
		/// window comes up still succeeds - it edits the asset directly - but <see cref="RefreshPaths"/>
		/// sees <c>Result == null</c>, does nothing, and the table goes on showing the pre-fix row. This
		/// is what keeps a restored scan and a freshly completed one behaving identically from here on.
		/// </para>
		/// </summary>
		public void AdoptRestoredResult(ScanResult result, ScanContext context)
		{
			Result = result;
			m_Context = context;
			m_Cache = context?.Cache;
		}

		// --- background execution ------------------------------------------------------

		void AddBackgroundFailure(ScanFailure failure)
		{
			m_Failures++;
			if (Result == null) return;
			Result.FailedAssets++;
			if (Result.FailuresByAnalyzer.TryGetValue(failure.Analyzer, out int count)) Result.FailuresByAnalyzer[failure.Analyzer] = count + 1;
			else Result.FailuresByAnalyzer[failure.Analyzer] = 1;
			if (m_Failures <= k_MaxLoggedFailures)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " " + failure.Analyzer + " failed on '" + failure.Path + "': " + failure.Message);
				if (m_Failures == k_MaxLoggedFailures) Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Further analyzer failures will be counted, not logged.");
			}
		}

		bool NeedsProjectWideReferences(OptimizerSettings settings)
		{
			if (settings == null) return false;
			if (settings.scanDuplicates || settings.scanUnused) return true;
			for (int i = 0; i < m_ProjectAnalyzers.Count; i++)
			{
				var analyzer = m_ProjectAnalyzers[i];
				if (analyzer == null || !analyzer.Enabled(settings)) continue;
				if (analyzer is IReferenceAwareBackgroundProjectAnalyzer) return true;
			}
			return false;
		}

		IEnumerator<int> BuildReferenceIndexForProjectPass(float start, float span)
		{
			if (m_Context.References != null) yield break;
			var referenceScope = m_Context.ReferenceScopePaths != null && m_Context.ReferenceScopePaths.Count > 0
				? (IReadOnlyList<string>)m_Context.ReferenceScopePaths
				: m_Context.AllAssetPaths;
			bool projectWide = m_Context.ReferenceScopePaths != null && m_Context.ReferenceScopePaths.Count > 0;
			if (!projectWide && referenceScope.Count == 0)
			{
				m_Context.References = new ReferenceIndex();
				yield break;
			}
			Status = "Building project dependency graph...";
			yield return k_RepaintNow;
			var builder = ReferenceIndex.BuildIncremental(referenceScope, projectWide,
				done => Progress = start + span * Mathf.Clamp01(done), m_Context.GetReferenceDependencyHash, m_Context.GetReferenceValidationStamp);
			while (builder.MoveNext())
			{
				if (builder.Current != null)
				{
					m_Context.References = builder.Current;
					break;
				}
				yield return 0;
			}
			m_Context.References = m_Context.References ?? new ReferenceIndex();
			if (!projectWide)
				Result.AddCaveat("The dependency graph available to this scan is not project-wide; destructive cross-asset actions remain disabled.");
		}

		static BackgroundWorkloadKind ClassifyBackgroundWorkload(IBackgroundAssetAnalyzer analyzer)
		{
			if (analyzer == null) return BackgroundWorkloadKind.Mixed;
			string name = analyzer.GetType().Name;
			if (name.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("UIToolkit", StringComparison.OrdinalIgnoreCase) >= 0)
				return BackgroundWorkloadKind.IoHeavy;
			if (name.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Dependency", StringComparison.OrdinalIgnoreCase) >= 0)
				return BackgroundWorkloadKind.CpuHeavy;
			return BackgroundWorkloadKind.Mixed;
		}

		static bool IsHeavyBackgroundAsset(string path)
		{
			try { return !string.IsNullOrEmpty(path) && new FileInfo(path).Length >= 64L * 1024L * 1024L; }
			catch { return false; }
		}

		bool IsBackgroundScheduled(IBackgroundAssetAnalyzer analyzer, int assetIndex)
		{
			return analyzer != null && m_BackgroundScheduledByAnalyzer.TryGetValue(analyzer, out var bits)
				&& assetIndex >= 0 && assetIndex < bits.Length && bits[assetIndex];
		}

		void EnsureBackgroundSchedulingBits(IBackgroundAssetAnalyzer analyzer, int count)
		{
			if (analyzer == null) return;
			if (!m_BackgroundScheduledByAnalyzer.TryGetValue(analyzer, out var bits) || bits.Length != count)
				m_BackgroundScheduledByAnalyzer[analyzer] = new BitArray(Math.Max(0, count), false);
		}

		static bool ShouldPrefetchAssetLoad(string path)
		{
			var kind = AssetKinds.Of(path);
			return kind == AssetKind.Model || kind == AssetKind.Animation;
		}

		bool TryScheduleBackgroundAsset(int assetIndex, int routeIndex, string path, IBackgroundAssetAnalyzer analyzer, out BackgroundAssetWorkItem work)
		{
			work = default;
			if (analyzer == null || string.IsNullOrEmpty(path) || IsBackgroundScheduled(analyzer, assetIndex)) return false;
			if (analyzer is ISelectiveBackgroundAssetAnalyzer selective && !selective.SupportsBackground(path)) return false;
			if (m_BackgroundAssetScheduler == null || m_BackgroundAssetContext == null) return false;

			double prepareStart = 0.0;
			try
			{
				prepareStart = ScanTiming.Now;
				m_BackgroundAssetContext.PrepareAssetIdentity(path, m_Context);
				analyzer.PrepareBackground(path, m_Context, m_BackgroundAssetContext);
				m_BackgroundAssetExtractionMilliseconds += Math.Max(0.0, ScanTiming.Now - prepareStart);
				work = new BackgroundAssetWorkItem(new AssetId(assetIndex), m_BackgroundAssetNextOrdinal++, path, analyzer, routeIndex,
					IsHeavyBackgroundAsset(AssetPathUtility.ToAbsolutePath(path)), ClassifyBackgroundWorkload(analyzer));
				return true;
			}
			catch (Exception e)
			{
				m_BackgroundAssetExtractionMilliseconds += Math.Max(0.0, ScanTiming.Now - prepareStart);
				if (m_Failures < k_MaxLoggedFailures)
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Background preparation failed for {path} ({analyzer.GetType().Name}); falling back to main-thread analysis. {e.Message}");
				return false;
			}
		}

		void MarkBackgroundScheduled(IBackgroundAssetAnalyzer analyzer, int assetIndex)
		{
			EnsureBackgroundSchedulingBits(analyzer, m_Context.WorkKinds.Count);
			m_BackgroundScheduledByAnalyzer[analyzer][assetIndex] = true;
		}

		int DrainBackgroundAssetResults(int maxResults, bool commitCacheWrites = true)
		{
			var scheduler = m_BackgroundAssetScheduler;
			var stream = m_BackgroundAssetStream;
			if (scheduler == null || stream == null) return 0;
			int drained = 0;
			while (drained < Math.Max(1, maxResults) && scheduler.TryTakeResult(out var result))
			{
				stream.Pending[result.Work.Ordinal] = result;
				stream.PendingHighWater = Math.Max(stream.PendingHighWater, stream.Pending.Count);
				drained++;
			}

			while (stream.Pending.TryGetValue(stream.NextOrdinal, out var ordered))
			{
				stream.Pending.Remove(stream.NextOrdinal);
				stream.Completed++;
				if (ordered.HasFailure) AddBackgroundFailure(ordered.Failure);
				else if (ordered.ElapsedMilliseconds >= k_SlowAssetMs) AddSlowAsset(ordered.Work.Path, ordered.ElapsedMilliseconds);

				if (ordered.Entry != null)
				{
					AssetEntry entry = ordered.Entry;
					if (entry.BackgroundPayload != null && ordered.Work.Analyzer is IHybridBackgroundAssetAnalyzer hybrid)
					{
						double hybridStart = ScanTiming.Now;
						try { entry = hybrid.CompleteBackground(entry, m_Context); }
						catch (Exception e)
						{
							ReportFailure(hybrid.GetType().Name, ordered.Work.Path, e);
							entry = null;
						}
						Result.Timing.AddDiagnosticTiming("Background/HybridMainThreadCompletion", ScanTiming.Now - hybridStart);
					}
					if (entry != null)
					{
						entry.AssetId = ordered.Work.Asset.Value;
						entry.ScanOrder = ((long)ordered.Work.Asset.Value << 32) | (uint)Math.Max(0, ordered.Work.RouteIndex);
						Result.Add(entry);
						m_Context.EntriesByPath[entry.Category + ":" + entry.Path] = entry;
					}
				}

				scheduler.MarkMaterialized(ordered.Work);
				stream.Materialized++;
				stream.NextOrdinal++;
				if (commitCacheWrites) CommitBackgroundCacheWrites(128);
			}

			return drained;
		}

		int CommitBackgroundCacheWrites(int maxWrites)
		{
			if (m_BackgroundAssetContext == null || m_Cache == null) return 0;
			var writes = new List<BackgroundCacheWrite>(Math.Min(Math.Max(1, maxWrites), 256));
			int count = m_BackgroundAssetContext.DrainCacheWrites(writes, maxWrites);
			for (int i = 0; i < writes.Count; i++)
				m_Cache.SetUntyped(writes[i].Key, writes[i].Facts, writes[i].DependencyHash);
			Result.Timing.BackgroundCacheWritesCommitted += count;
			return count;
		}

		void IntegrateBackgroundProjectResult(BackgroundProjectBatchResult batch)
		{
			if (batch == null) return;
			if (batch.Timing != null) Result.Timing.MergeFrom(batch.Timing);
			Result.Timing.BackgroundProjectPasses += batch.WorkCount;
			Result.Timing.BackgroundProjectCompleted += batch.Completed;
			Result.Timing.BackgroundProjectWallMilliseconds += batch.WallMilliseconds;
			if (batch.Failures != null)
				for (int i = 0; i < batch.Failures.Count; i++) AddBackgroundFailure(batch.Failures[i]);
			if (batch.Results == null) return;
			for (int i = 0; i < batch.Results.Length; i++)
			{
				var projectResult = batch.Results[i];
				if (projectResult == null || projectResult.Entries == null) continue;
				for (int e = 0; e < projectResult.Entries.Count; e++)
				{
					var entry = projectResult.Entries[e];
					if (entry != null) Result.Add(entry);
				}
			}
		}

		// --- the scan ------------------------------------------------------------------

		IEnumerator<int> Run()
		{
			var settings = m_Context.Settings;
			var timing = Result.Timing;
			timing.ConfigureProfiling(ScanBenchmark.EffectiveMode(settings));

			// --- collect ---------------------------------------------------------
			// Scope resolution is itself sliced. On a project with hundreds of thousands of assets
			// it can involve a stat per file, which is long enough to be felt as a freeze if it all
			// happens inside one editor frame.
			Status = "Working out what to scan (script compilation is paused until this finishes)...";
			// Handed back before the scope is built, so that status actually reaches the screen: in
			// build-only mode the constructor walks the whole dependency graph and returns nothing to
			// paint until it is done.
			yield return k_RepaintNow;

			double t0 = ScanTiming.Now;
			var scope = new ScanScope(settings);
			Result.Scope = scope;

			string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
			AssetDatabaseThreadSafeUtility.Initialize(projectRoot, includePackages: true);

			// The project-state snapshot is now the scanner's canonical path universe. Unity's full path
			// enumeration remains available only as an explicit validation/recovery boundary, not on the
			// normal warm path. Packages are indexed too because reference-scope semantics intentionally
			// include package referrers even though package assets themselves are not scanned for fixes.
			m_ThreadSafeAssetIndexTask = null;
			AssetDatabaseThreadSafeBuildProgress assetIndexProgress = null;
			double assetIndexStart = ScanTiming.Now;
			if (!AssetDatabaseThreadSafeUtility.IsComplete || AssetDatabaseThreadSafeUtility.NeedsAuthoritativeReconcile)
			{
				assetIndexProgress = new AssetDatabaseThreadSafeBuildProgress();
				Status = AssetDatabaseThreadSafeUtility.NeedsAuthoritativeReconcile
					? "Reconciling project state..."
					: "Building project state snapshot...";
				yield return k_RepaintNow;
				var indexToken = m_FileMetadataCts != null ? m_FileMetadataCts.Token : CancellationToken.None;
				m_ThreadSafeAssetIndexTask = AssetDatabaseThreadSafeUtility.RebuildIndexAsync(true, assetIndexProgress, indexToken);
			}

			if (m_ThreadSafeAssetIndexTask != null)
			{
				while (!m_ThreadSafeAssetIndexTask.IsCompleted)
				{
					if (assetIndexProgress != null && !string.IsNullOrEmpty(assetIndexProgress.CurrentDirectory))
						Status = "Building project state snapshot... " + assetIndexProgress.CurrentDirectory;
					yield return 0;
				}

				if (m_ThreadSafeAssetIndexTask.IsFaulted || m_ThreadSafeAssetIndexTask.IsCanceled)
				{
					ObserveBackgroundFault(m_ThreadSafeAssetIndexTask);
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Project state snapshot could not be built; using Unity's authoritative path enumeration for this scan only.");
				}
				else
				{
					try
					{
						AssetDatabaseThreadSafeUtility.PublishBuild(m_ThreadSafeAssetIndexTask.Result);
						AssetDatabaseThreadSafeUtility.ApplyPendingChanges();
					}
					catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Project state snapshot could not be published: " + e.Message); }
				}
			}
			if (m_ThreadSafeAssetIndexTask != null)
			{
				// A background filesystem snapshot can race an editor import. Canonicalize against the authoritative
				// path list even when queued metadata deltas left the temporary snapshot marked stale; this prevents
				// the first scan from using a different asset universe than the next scan.
				if (AssetDatabaseThreadSafeUtility.IsInitialized)
				{
					double authoritativeStart = ScanTiming.Now;
					var authoritativePaths = AssetDatabase.GetAllAssetPaths();
					if (!AssetDatabaseThreadSafeUtility.ReconcileAuthoritativeAssetPaths(authoritativePaths))
						Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " The rebuilt project snapshot could not be reconciled with Unity's authoritative asset paths; this scan will use Unity's path universe for correctness.");
					timing.AddStage("project state authoritative reconcile", ScanTiming.Now - authoritativeStart);
				}
				timing.AddStage("project state snapshot", ScanTiming.Now - assetIndexStart);
			}

			string[] allPaths;
			if (AssetDatabaseThreadSafeUtility.IsComplete)
			{
				allPaths = AssetDatabaseThreadSafeUtility.GetAllAssetPaths();
				if (ScanBenchmark.IsValidation(settings))
				{
					var authoritativePaths = AssetDatabase.GetAllAssetPaths();
					if (!AssetDatabaseThreadSafeUtility.ValidateAuthoritativeAssetPaths(authoritativePaths))
						Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Validation detected a project-state path-set mismatch; results use the snapshot only for this validation run.");
				}
			}
			else
			{
				allPaths = AssetDatabase.GetAllAssetPaths();
				timing.AddDiagnosticTiming("ProjectState/AuthoritativeFallback", 0.0);
			}
			t0 = ScanTiming.Now;

			// Build reachability is its own stage now, driven from here so it can give frames back.
			// It uses the published project-state index, avoiding a second Unity AssetDatabase path enumeration.
			if (scope.NeedsBuildSet)
			{
				double r0 = ScanTiming.Now;
				Status = "Working out what a build reaches...";
				yield return k_RepaintNow;

				var reachability = scope.BuildReachableSetIncremental(status => Status = status);
				while (reachability.MoveNext())
				{
					if (reachability.Current) break;
					yield return 0;
				}
				timing.AddStage("build reachability", ScanTiming.Now - r0);
			}

			for (int i = 0; i < allPaths.Length; i++)
			{
				scope.Consider(allPaths[i]);
				if ((i & 1023) == 0)
				{
					Progress = 0.02f * (i / (float)Mathf.Max(1, allPaths.Length));
					yield return 0;
				}
			}
			m_Context.AllAssetPaths.AddRange(scope.Paths);
			m_Context.WorkKinds.Clear();
			m_Context.WorkKinds.Capacity = Math.Max(m_Context.WorkKinds.Capacity, m_Context.AllAssetPaths.Count);
			for (int wi = 0; wi < scope.Paths.Count; wi++)
			{
				string path = scope.Paths[wi];
				m_Context.WorkKinds.Add((byte)AssetKinds.Of(path));
			}
			m_Context.ReferenceScopePaths.AddRange(scope.ReferenceScopePaths);
			m_Context.BuildSet = scope.BuildSet;
			m_Context.BuildReachabilityComplete = scope.BuildReachabilityComplete;
			m_Context.BuildReachabilityCaveat = scope.ReachabilityIncompletenessSummary();
			timing.AddStage("resolve scan scope", ScanTiming.Now - t0);
			timing.BenchmarkIdentifier = ScanBenchmark.BuildIdentifier(settings, scope.Paths.Count, AnalyzerRegistry.Signature);

			// The persistent project snapshot already contains the metadata used by the scan. Reuse it
			// directly when complete; package-manager paths or an unavailable snapshot still use the original
			// parallel filesystem builder as a correctness fallback.
			if (m_Context.AllAssetPaths.Count > 0)
			{
				double metadataStart = ScanTiming.Now;
				if (AssetDatabaseThreadSafeUtility.IsComplete)
				{
					m_Context.FileMetadata = ScanFileMetadata.FromThreadSafeSnapshot();
					timing.AddStage("filesystem metadata", ScanTiming.Now - metadataStart);
				}
				else
				{
					Status = "Preparing filesystem metadata...";
					yield return k_RepaintNow;
					int metadataWorkerCount = ScanPerformance.EffectiveWorkerCount(settings);
					var cancellationToken = m_FileMetadataCts != null ? m_FileMetadataCts.Token : CancellationToken.None;
					var metadataTask = Task.Run(() => ScanFileMetadata.Build(
						allPaths, projectRoot, metadataWorkerCount, cancellationToken, true), cancellationToken);
					while (!metadataTask.IsCompleted) yield return 0;
					if (metadataTask.IsFaulted)
					{
						Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Filesystem metadata preparation failed: "
							+ metadataTask.Exception?.GetBaseException().Message);
						m_Context.FileMetadata = new ScanFileMetadata();
					}
					else m_Context.FileMetadata = metadataTask.Result;
					timing.AddStage("filesystem metadata", ScanTiming.Now - metadataStart);
				}
				double catalogStart = ScanTiming.Now;
				var catalog = new AssetCatalog();
				for (int ci = 0; ci < m_Context.AllAssetPaths.Count; ci++)
				{
					string catalogPath = m_Context.AllAssetPaths[ci];
					var meta = m_Context.FileMetadata;
					catalog.AddOrUpdate(catalogPath, meta != null ? meta.GetGuid(catalogPath) : string.Empty, AssetKinds.Of(catalogPath), meta != null ? meta.GetFileSize(catalogPath) : 0L, meta != null ? meta.GetLastWriteTicks(catalogPath) : 0L);
				}
				m_Context.AssetCatalog = catalog;
				timing.AddStage("build asset catalog", ScanTiming.Now - catalogStart);
			}

			// Now that the size of this run is known, load the timing history recorded for runs of
			// the same shape. A different scope or a project an order of magnitude larger has its
			// own history and is not a model for this one.
			m_ProfileKey = ScanProfile.KeyFor(settings, scope.Paths.Count) + "|analyzers=" + AnalyzerRegistry.Signature;
			m_Profile = ScanProfile.Load(m_ProfileKey);

			if (scope.Paths.Count == 0)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " The current scan scope matches no assets. "
								 + "Check Scan Scope: " + scope.Summary());
			}
			yield return 0;

			t0 = ScanTiming.Now;
			m_Cache = ScanCache.Load(settings);
			timing.AddStage("load cache", ScanTiming.Now - t0);
			yield return 0;

			m_Context.Cache = m_Cache;
			var presentKinds = new bool[AssetKinds.All.Length];
			for (int wi = 0; wi < m_Context.WorkKinds.Count; wi++)
			{
				int k = m_Context.WorkKinds[wi];
				if (k >= 0 && k < presentKinds.Length) presentKinds[k] = true;
			}
			// Initialize static settings/rule data on Unity's main thread before worker tasks begin.
			// Background analyzers only consume these objects; they never mutate them.
			var settingsWarmup = OptimizerSettings.Instance;
			var ruleCatalogWarmupStart = ScanTiming.Now;
			var ruleCatalogWarmup = RuleCatalog.All;
			timing.AddStage("analyzer warm-up/RuleCatalog", ScanTiming.Now - ruleCatalogWarmupStart);
			for (int ai = 0; ai < m_AssetAnalyzers.Count; ai++)
			{
				var analyzer = m_AssetAnalyzers[ai];
				bool needed = AnalyzerNeeded(analyzer.SupportedKinds, kind => presentKinds[(int)kind], analyzer == m_MaterialAnalyzer && settings.scanDuplicates);
				if (!needed) continue;
				double analyzerWarmupStart = ScanTiming.Now;
				try { analyzer.BeginScan(m_Context); }
				finally { timing.AddStage("analyzer warm-up/" + analyzer.GetType().Name, ScanTiming.Now - analyzerWarmupStart); }
			}
			yield return 0;

			var adaptiveFrameBudget = new ScanPerformance.AdaptiveFrameBudgetController(settings);
			m_AssetLoadPrefetcher = new AssetLoadPrefetcher(settings);
			int configuredBatch = ScanPerformance.EffectiveBatchSize(settings);
			int workerCount = ScanPerformance.MaximumWorkerCount(settings);
			int queueCapacity = ScanPerformance.EffectiveQueueCapacity(settings, workerCount);

			// Workers start before the first asset is traversed when at least one enabled routed analyzer
			// can execute in the background. The producer below extracts Unity facts one asset at a time
			// and pushes them into bounded queues, creating real overlap and backpressure.
			bool hasBackgroundWork = false;
			for (int ai = 0; ai < m_AssetAnalyzers.Count; ai++)
			{
				if (!(m_AssetAnalyzers[ai] is IBackgroundAssetAnalyzer bgAnalyzer)) continue;
				if (!AnalyzerNeeded(bgAnalyzer.SupportedKinds, kind => presentKinds[(int)kind], false)) continue;
				EnsureBackgroundSchedulingBits(bgAnalyzer, m_Context.WorkKinds.Count);
				hasBackgroundWork = true;
			}
			if (hasBackgroundWork)
			{
				m_BackgroundAssetContext = new BackgroundAssetContext(settings, m_Context.FileMetadata, Path.GetDirectoryName(Application.dataPath) ?? string.Empty, m_Context.AssetCatalog != null ? new Func<string, string>(m_Context.AssetCatalog.PathForGuid) : null,
					Math.Max(1024, queueCapacity * 2), m_BackgroundCts != null ? m_BackgroundCts.Token : CancellationToken.None);
				m_BackgroundAssetScheduler = BackgroundAssetScheduler.Start(m_BackgroundAssetContext, workerCount, queueCapacity,
					m_BackgroundCts != null ? m_BackgroundCts.Token : CancellationToken.None);
				m_BackgroundAssetStream = new BackgroundAssetStreamState(m_BackgroundAssetScheduler);
				timing.BackgroundAssetQueueCapacity = queueCapacity;
			}
			yield return 0;

			// --- per-asset passes --------------------------------------------------
			int total = Mathf.Max(1, m_Context.AllAssetPaths.Count);
			double perAssetStart = ScanTiming.Now;
			int loadsSinceSweep = 0;
			double lastStatus = 0;
			int unloadEvery = total >= k_LargeProjectAssets ? k_UnloadEveryLoads * 8 : k_UnloadEveryLoads;
			double sliceStart = ScanTiming.Now;
			int batchCounter = 0;
			ScanTiming ruleTimingContext = null;
			for (int i = 0; i < m_Context.WorkKinds.Count; i++)
			{
				var path = m_Context.AllAssetPaths[i];
				m_AssetLoadPrefetcher?.Pump();
				m_AssetLoadPrefetcher?.Prefetch(m_Context.AllAssetPaths, i, m_AssetLoadPrefetcher != null ? 4 : 0, ShouldPrefetchAssetLoad);
				float mainProgress = i / (float)total;
				float backgroundProgress = m_BackgroundAssetContext != null && m_BackgroundAssetContext.WorkCount > 0
					? m_BackgroundAssetContext.Completed / (float)Math.Max(1, m_BackgroundAssetContext.WorkCount) : 0f;
				Progress = 0.08f + 0.62f * (mainProgress * 0.72f + Mathf.Clamp01(backgroundProgress) * 0.28f);

				double now = ScanTiming.Now;
				if (now - lastStatus >= k_StatusIntervalMs) { lastStatus = now; Status = "Analyzing " + path; }
				int missesBefore = m_Cache.Misses;
				double assetStart = now;
				m_Context.CurrentAssetPath = path;
				m_Context.CurrentDependencyHash = null;
				m_Context.CurrentGuid = null;
				AssetKind kind = (AssetKind)m_Context.WorkKinds[i];
				if (!m_AnalyzersByKind.TryGetValue(kind, out var routed)) routed = m_FallbackAnalyzers;

				try
				{
					// First satisfy the bounded producer. The scheduler caps total scheduled-but-not-materialized
					// work as well as the input queues, so out-of-order completions cannot grow without bound.
					for (int a = 0; a < routed.Count; a++)
					{
						var bg = routed[a] as IBackgroundAssetAnalyzer;
						if (bg == null || !bg.Handles(path) || IsBackgroundScheduled(bg, i)) continue;
						BackgroundAssetWorkItem work;
						if (!TryScheduleBackgroundAsset(i, a, path, bg, out work)) continue;
						bool enqueued = false;
						while (!m_BackgroundAssetScheduler.TryEnqueue(work))
						{
							DrainBackgroundAssetResults(Math.Max(32, configuredBatch));
							CommitBackgroundCacheWrites(256);
							m_BackgroundAssetScheduler.AdaptWorkerTarget();
							if (m_BackgroundCts != null && m_BackgroundCts.IsCancellationRequested) break;
							yield return 0;
						}
						enqueued = !m_BackgroundAssetScheduler.InputCompleted
							&& (m_BackgroundCts == null || !m_BackgroundCts.IsCancellationRequested);
						if (!enqueued)
						{
							m_BackgroundAssetScheduler.ReleasePreparedAssetIfIdle(new AssetId(i), path);
							continue;
						}
						MarkBackgroundScheduled(bg, i);
					}

					// Drain some completed work before touching the main-thread analyzers. This keeps the
					// result queue bounded even on a fast project and reduces its peak lifetime.
					DrainBackgroundAssetResults(Math.Max(32, configuredBatch));
					CommitBackgroundCacheWrites(128);

					for (int a = 0; a < routed.Count; a++)
					{
						var analyzer = routed[a];
						if (!analyzer.Handles(path)) continue;
						if (analyzer is IBackgroundAssetAnalyzer backgroundAnalyzer && IsBackgroundScheduled(backgroundAnalyzer, i)) continue;

						double a0 = ScanTiming.Now;
						AssetEntry entry = null;
						ruleTimingContext = timing;
						if (settings.enableScanPerformanceLogging) { ruleTimingContext.Activate(); ruleTimingContext.BeginAnalyzerTracking(analyzer.GetType().Name); }
						try { entry = analyzer.Analyze(path, m_Context); }
						catch (Exception e) { ReportFailure(analyzer.GetType().Name, path, e); }
						finally
						{
							double analyzerMs = ScanTiming.Now - a0;
							if (settings.enableScanPerformanceLogging) ruleTimingContext.EndAnalyzerTracking(analyzer.GetType().Name, analyzerMs);
							ScanTiming.Deactivate(ruleTimingContext);
						}
						timing.AddAnalyzer(analyzer.GetType().Name, ScanTiming.Now - a0);
						if (entry == null) continue;
						entry.AssetId = i;
						entry.ScanOrder = ((long)i << 32) | (uint)Math.Max(0, a);
						Result.Add(entry);
						m_Context.EntriesByPath[entry.Category + ":" + entry.Path] = entry;
					}
				}
				finally { m_Context.ClearLoadedObjects(path); }

				DrainBackgroundAssetResults(Math.Max(32, configuredBatch));
				CommitBackgroundCacheWrites(128);
				if (m_BackgroundAssetScheduler != null) m_BackgroundAssetScheduler.ReleasePreparedAssetIfIdle(new AssetId(i), path);
				if (m_BackgroundAssetScheduler != null) m_BackgroundAssetScheduler.AdaptWorkerTarget();
				double assetMs = ScanTiming.Now - assetStart;
				if (assetMs >= k_SlowAssetMs) AddSlowAsset(path, assetMs);
				Result.ScannedAssets++;
				loadsSinceSweep += m_Cache.Misses - missesBefore;

				if (total >= k_UnloadThreshold && loadsSinceSweep >= unloadEvery)
				{
					loadsSinceSweep = 0;
					Status = "Releasing loaded assets...";
					lastStatus = ScanTiming.Now;
					double u0 = ScanTiming.Now;
					EditorUtility.UnloadUnusedAssetsImmediate();
					timing.AddStage("release loaded assets", ScanTiming.Now - u0);
				}

				batchCounter++;
				double sliceElapsed = ScanTiming.Now - sliceStart;
				if (batchCounter >= configuredBatch || sliceElapsed >= adaptiveFrameBudget.CurrentMilliseconds)
				{
					adaptiveFrameBudget.Observe(sliceElapsed);
					batchCounter = 0;
					sliceStart = ScanTiming.Now;
					yield return 0;
				}
			}
			timing.AddStage("per-asset main-thread scan", ScanTiming.Now - perAssetStart);

			if (m_BackgroundAssetScheduler != null)
			{
				Status = "Finishing background asset analysis...";
				m_BackgroundAssetScheduler.CompleteInput();
				while (!m_BackgroundAssetScheduler.IsCompleted || m_BackgroundAssetScheduler.ResultQueueDepth > 0 || m_BackgroundAssetStream.Pending.Count > 0)
				{
					DrainBackgroundAssetResults(4096);
					CommitBackgroundCacheWrites(1024);
					float p = m_BackgroundAssetContext != null && m_BackgroundAssetContext.WorkCount > 0
						? m_BackgroundAssetContext.Completed / (float)Math.Max(1, m_BackgroundAssetContext.WorkCount) : 1f;
					Progress = 0.62f + 0.08f * Mathf.Clamp01(p);
					Status = "Finishing background asset analysis... " + m_BackgroundAssetScheduler.CompletedCount + "/" + m_BackgroundAssetScheduler.ScheduledCount;
					yield return 0;
				}
				DrainBackgroundAssetResults(int.MaxValue);
				CommitBackgroundCacheWrites(int.MaxValue);
				m_BackgroundAssetScheduler.DrainWorkerTiming(timing);
				timing.BackgroundAssetWorkItems = (int)Math.Min(int.MaxValue, m_BackgroundAssetScheduler.ScheduledCount);
				timing.BackgroundAssetCompleted = (int)Math.Min(int.MaxValue, m_BackgroundAssetScheduler.CompletedCount);
				timing.BackgroundAssetWallMilliseconds = m_BackgroundAssetScheduler.WallMilliseconds;
				timing.BackgroundAssetQueueCapacity = m_BackgroundAssetScheduler.QueueCapacity;
				timing.BackgroundAssetQueueHighWater = m_BackgroundAssetScheduler.NormalQueueHighWater;
				timing.BackgroundAssetHeavyQueueHighWater = m_BackgroundAssetScheduler.HeavyQueueHighWater;
				timing.BackgroundAssetResultQueueHighWater = m_BackgroundAssetScheduler.ResultQueueHighWater;
				timing.BackgroundAssetOutstandingHighWater = (int)Math.Min(int.MaxValue, m_BackgroundAssetScheduler.OutstandingHighWater);
				timing.BackgroundAssetOrderedPendingHighWater = m_BackgroundAssetStream != null ? m_BackgroundAssetStream.PendingHighWater : 0;
				timing.BackgroundAssetWorkerTargetHighWater = m_BackgroundAssetScheduler.WorkerTargetHighWater;
				timing.BackgroundAssetBackpressureEvents = m_BackgroundAssetScheduler.BackpressureEvents;
				timing.BackgroundCacheWriteHighWater = m_BackgroundAssetContext != null ? Math.Max(m_BackgroundAssetContext.MainThreadCacheWriteHighWater, m_BackgroundAssetContext.WorkerCacheWriteHighWater) : 0;
				timing.BackgroundWorkerTarget = m_BackgroundAssetScheduler.ActiveWorkerTarget;
				timing.BackgroundAssetHeavyWorkItems = m_BackgroundAssetScheduler.HeavyScheduledCount;
				double denominator = Math.Max(1.0, Math.Max(1.0, timing.BackgroundAssetWallMilliseconds) * Math.Max(1, m_BackgroundAssetScheduler.WorkerCount));
				timing.BackgroundAssetWorkerUtilizationPercent = Math.Max(0.0, Math.Min(100.0, m_BackgroundAssetScheduler.WorkerBusyMilliseconds / denominator * 100.0));
				timing.BackgroundAssetExtractionMilliseconds = m_BackgroundAssetExtractionMilliseconds;
				m_BackgroundAssetScheduler.Dispose();
			}
			else
			{
				timing.BackgroundAssetWorkItems = 0;
				timing.BackgroundAssetCompleted = 0;
				timing.BackgroundAssetWallMilliseconds = 0.0;
				timing.BackgroundAssetExtractionMilliseconds = m_BackgroundAssetExtractionMilliseconds;
				CommitBackgroundCacheWrites(int.MaxValue);
			}
			Result.SlowAssets.Sort((a, b) => b.Value.CompareTo(a.Value));

			// --- optional project proof pass --------------------------------------
			// This is intentionally explicit. Proof is not part of an ordinary asset scan and must never
			// start implicitly from a rule asking for evidence.
			float projectPhaseStart = 0.70f;
			float proofPhaseSpan = settings.projectProofPass ? 0.10f : 0f;
			float afterProofProgress = projectPhaseStart + proofPhaseSpan;
			if (settings.projectProofPass)
			{
				double proofStart = ScanTiming.Now;
				Status = "Building project dependency/source proof...";
				Progress = projectPhaseStart;
				yield return k_RepaintNow;

				if (m_Context.ProofIndex == null) m_Context.ProofIndex = new ProjectProofIndex();
				var roots = new List<string>();
				string proofProjectRoot = Directory.GetParent(Application.dataPath)?.FullName;
				if (!string.IsNullOrEmpty(Application.dataPath) && Directory.Exists(Application.dataPath)) roots.Add(Application.dataPath);
				if (!string.IsNullOrEmpty(proofProjectRoot))
				{
					string packages = Path.Combine(proofProjectRoot, "Packages");
					string packageCache = Path.Combine(proofProjectRoot, "Library", "PackageCache");
					if (Directory.Exists(packages)) roots.Add(packages);
					if (Directory.Exists(packageCache)) roots.Add(packageCache);
				}

				var proofTask = m_Context.ProofIndex.StartBuildAsync(roots, m_FileMetadataCts != null ? m_FileMetadataCts.Token : CancellationToken.None);
				while (!proofTask.IsCompleted)
				{
					int candidate = m_Context.ProofIndex.CandidateFiles;
					int processed = m_Context.ProofIndex.FilesProcessed;
					Progress = candidate > 0 ? projectPhaseStart + proofPhaseSpan * Mathf.Clamp01(processed / (float)candidate) : projectPhaseStart;
					Status = candidate > 0
						? "Building project dependency/source proof... " + processed + "/" + candidate
						: "Building project dependency/source proof...";
					yield return 0;
				}
				bool proofReady = m_Context.ProofIndex.CompleteBuild();
				timing.AddStage("project dependency/source proof", ScanTiming.Now - proofStart);
				if (!proofReady)
					Result.AddCaveat("The project dependency/source proof pass did not complete successfully; evidence-gated rules failed closed.");
			}
			else
			{
				m_Context.ProofIndex = null;
			}

			// --- project-wide passes -------------------------------------------------
			// Build the shared reference graph once before any project analyzer that needs it. Duplicate
			// and unused analyzers now observe ctx.References and therefore never rebuild it themselves.
			float projectStart = afterProofProgress;
			float projectReferenceSpan = NeedsProjectWideReferences(settings) ? 0.08f : 0f;
			float projectAnalyzerStart = projectStart + projectReferenceSpan;
			if (NeedsProjectWideReferences(settings))
			{
				double referenceStart = ScanTiming.Now;
				var referenceEnumerator = BuildReferenceIndexForProjectPass(projectStart, projectReferenceSpan);
				try
				{
					while (referenceEnumerator.MoveNext())
					{
						int step = referenceEnumerator.Current;
						if (step == k_RepaintNow) yield return step;
						else yield return 0;
					}
				}
				finally
				{
					if (referenceEnumerator != null) referenceEnumerator.Dispose();
				}
				timing.AddStage("build reference index", ScanTiming.Now - referenceStart);
			}

			var mainProjectAnalyzers = new List<IProjectAnalyzer>();
			var backgroundProjectAnalyzers = new List<IBackgroundProjectAnalyzer>();
			for (int i = 0; i < m_ProjectAnalyzers.Count; i++)
			{
				var analyzer = m_ProjectAnalyzers[i];
				if (analyzer == null || !analyzer.Enabled(settings)) continue;
				if (analyzer is IReferenceAwareBackgroundProjectAnalyzer && m_Context.References == null) { mainProjectAnalyzers.Add(analyzer); continue; }
				if (analyzer is IBackgroundProjectAnalyzer background) backgroundProjectAnalyzers.Add(background);
				else mainProjectAnalyzers.Add(analyzer);
			}

			if (backgroundProjectAnalyzers.Count > 0)
			{
				m_BackgroundProjectContext = new BackgroundProjectContext(settings, m_Context.FileMetadata, m_Context.References, projectRoot);
				m_BackgroundProjectTask = BackgroundProjectScheduler.Start(backgroundProjectAnalyzers, m_BackgroundProjectContext,
					m_BackgroundCts != null ? m_BackgroundCts.Token : CancellationToken.None);
			}
			else
			{
				m_BackgroundProjectContext = null;
				m_BackgroundProjectTask = null;
			}

			int totalProjectUnits = Mathf.Max(1, mainProjectAnalyzers.Count + backgroundProjectAnalyzers.Count);
			float projectSpan = Mathf.Max(0f, 1f - projectAnalyzerStart);
			int unit = 0;
			for (int p = 0; p < mainProjectAnalyzers.Count; p++, unit++)
			{
				var analyzer = mainProjectAnalyzers[p];
				float passShare = projectSpan / totalProjectUnits;
				float passStart = projectAnalyzerStart + passShare * unit;
				Progress = passStart;
				Status = analyzer.DisplayName + "...";

				IEnumerator<AssetEntry> enumerator;
				try { enumerator = analyzer.Analyze(m_Context).GetEnumerator(); }
				catch (Exception e) { ReportFailure(analyzer.DisplayName, "project pass", e); continue; }

				ruleTimingContext = timing;
				double passWorkMs = 0.0;
				if (settings.enableScanPerformanceLogging)
				{
					ruleTimingContext.Activate();
					ruleTimingContext.BeginAnalyzerTracking(analyzer.GetType().Name);
				}
				try
				{
					while (true)
					{
						AssetEntry entry = null;
						bool hasNext = false;
						double stepStart = ScanTiming.Now;
						try { hasNext = enumerator.MoveNext(); if (hasNext) entry = enumerator.Current; }
						catch (Exception e)
						{
							double failedStepMs = ScanTiming.Now - stepStart;
							passWorkMs += failedStepMs;
							timing.AddStage(analyzer.DisplayName, failedStepMs);
							ReportFailure(analyzer.DisplayName, "project pass", e);
							break;
						}
						double moveNextMs = ScanTiming.Now - stepStart;
						passWorkMs += moveNextMs;
						if (!hasNext) { timing.AddStage(analyzer.DisplayName, moveNextMs); break; }
						if (entry != null) Result.Add(entry);
						float within = analyzer.Progress;
						if (within >= 0f) Progress = passStart + passShare * Mathf.Clamp01(within);
						yield return 0;
					}
				}
				finally
				{
					enumerator.Dispose();
					if (settings.enableScanPerformanceLogging)
						ruleTimingContext.EndAnalyzerTracking(analyzer.GetType().Name, passWorkMs);
					ScanTiming.Deactivate(ruleTimingContext);
				}
			}

			if (m_BackgroundProjectTask != null)
			{
				Status = "Finishing background project analysis...";
				while (!m_BackgroundProjectTask.IsCompleted)
				{
					int completed = m_BackgroundProjectContext != null ? m_BackgroundProjectContext.Completed : 0;
					Progress = projectAnalyzerStart + projectSpan * ((mainProjectAnalyzers.Count + completed) / (float)totalProjectUnits);
					Status = "Finishing background project analysis... " + completed + "/" + backgroundProjectAnalyzers.Count;
					yield return 0;
				}
				if (!m_BackgroundProjectTask.IsCanceled && !m_BackgroundProjectTask.IsFaulted) IntegrateBackgroundProjectResult(m_BackgroundProjectTask.Result);
				else if (m_BackgroundProjectTask.IsFaulted) Result.AddCaveat("Background project analysis failed before all results were committed; affected findings may be incomplete.");
				m_BackgroundProjectTask = null;
			}

			if (total >= k_UnloadThreshold && loadsSinceSweep > 0)
			{
				double u0 = ScanTiming.Now;
				EditorUtility.UnloadUnusedAssetsImmediate();
				timing.AddStage("release loaded assets", ScanTiming.Now - u0);
			}

			t0 = ScanTiming.Now;
			// Pruned against every asset in the project rather than against this scan's scope, so a
			// restricted run keeps the measurements a later full run would otherwise have to redo.
			var livePaths = m_Context.ReferenceScopePaths.Count > 0
				? (IReadOnlyCollection<string>)m_Context.ReferenceScopePaths
				: m_Context.AllAssetPaths;
			m_Cache.Prune(livePaths);
			m_Cache.Save();
			timing.AddStage("save cache", ScanTiming.Now - t0);
			timing.CacheHits = m_Cache.Hits;
			timing.CacheMisses = m_Cache.Misses;

			if (m_Failures > k_MaxLoggedFailures)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " " + m_Failures + " assets could not be analyzed; the first "
								 + k_MaxLoggedFailures + " are logged above.");
			}

			RecordCompleteness(scope, settings);
			if (m_Context != null && NeedsProjectWideReferences(settings) && m_Context.References != null)
				Result.CaptureCrossAssetEvidence(m_Context.References, m_Context.AllAssetPaths);
			AssetDatabaseThreadSafeUtility.FlushPersistentSnapshot();

			Result.ReorderDeterministically();
			Result.Recount();
			Progress = 1f;
			if (settings.enableScanPerformanceLogging)
				WriteDetailedTimingReport(settings);
		}

		void AddSlowAsset(string path, double milliseconds)
		{
			var list = Result.SlowAssets;
			int insert = 0;
			while (insert < list.Count && list[insert].Value >= milliseconds) insert++;
			if (insert >= k_MaxSlowAssetsTracked && list.Count >= k_MaxSlowAssetsTracked) return;
			if (insert > list.Count) insert = list.Count;
			list.Insert(insert, new KeyValuePair<string, double>(path, milliseconds));
			if (list.Count > k_MaxSlowAssetsTracked) list.RemoveAt(k_MaxSlowAssetsTracked);
		}

		void WriteDetailedTimingReport(OptimizerSettings settings)
		{
			if (Result == null) return;

			var timing = Result.Timing;
			var lines = new List<string>();
			var culture = System.Globalization.CultureInfo.InvariantCulture;

			// ---------------------------------------------------------------------
			// Formatting helpers
			// ---------------------------------------------------------------------

			string Seconds(double ms)
			{
				return (ms / 1000.0).ToString("0.000", culture) + " s";
			}

			string Milliseconds(double ms)
			{
				return ms.ToString("0.00", culture) + " ms";
			}

			string Percent(double value, double total)
			{
				if (total <= 0.0)
					return "0.0%";

				return (value / total * 100.0).ToString("0.0", culture) + "%";
			}

			double TotalRuleMs(RuleTimingStats rule) => rule.TotalMilliseconds;
			string FormatSample(RuleTimingSample sample) => sample == null ? "<none>" : Milliseconds(sample.TotalMilliseconds) + " " + (string.IsNullOrEmpty(sample.AssetPath) ? "<unknown>" : sample.AssetPath);

			void AddSection(string title)
			{
				lines.Add("============================================================");
				lines.Add(title);
				lines.Add("============================================================");
			}

			void AddKeyValue(string key, string value, int keyWidth = 34)
			{
				lines.Add("  " + key.PadRight(keyWidth) + value);
			}

			// ---------------------------------------------------------------------
			// Calculate aggregate timings
			// ---------------------------------------------------------------------

			double wallMs = ElapsedSeconds * 1000.0;
			double wallSeconds = wallMs / 1000.0;

			double analyzerWorkMs = 0.0;
			foreach (var pair in timing.Analyzers)
				analyzerWorkMs += pair.Value;

			double stageWorkMs = 0.0;
			foreach (var pair in timing.Stages)
				stageWorkMs += pair.Value;

			double perAssetMs = 0.0;
			timing.Stages.TryGetValue("per-asset scan", out perAssetMs);

			int cacheTotal = timing.CacheHits + timing.CacheMisses;

			double cacheHitRate = cacheTotal > 0
				? timing.CacheHits / (double)cacheTotal
				: 0.0;

			int assetCount = Math.Max(0, Result.ScannedAssets);

			int totalAnalyzerCalls = 0;
			foreach (var pair in timing.AnalyzerCalls)
				totalAnalyzerCalls += pair.Value;

			double explicitlyMeasuredOutsidePerAsset =
				Math.Max(0.0, stageWorkMs - perAssetMs);

			double unaccountedVsWall = wallMs - stageWorkMs;

			// ---------------------------------------------------------------------
			// Pre-sort tables
			// ---------------------------------------------------------------------

			var stages = new List<KeyValuePair<string, double>>(timing.Stages);
			stages.Sort((a, b) => b.Value.CompareTo(a.Value));

			var analyzers = new List<KeyValuePair<string, double>>(timing.Analyzers);
			analyzers.Sort((a, b) => b.Value.CompareTo(a.Value));

			var failures = new List<KeyValuePair<string, int>>(Result.FailuresByAnalyzer);
			failures.Sort((a, b) => b.Value.CompareTo(a.Value));

			// ---------------------------------------------------------------------
			// Header
			// ---------------------------------------------------------------------

			lines.Add(ProjectOptimizerConstants.LogPrefix + " Detailed scan performance report");
			lines.Add("Generated".PadRight(18) + ": "
				+ DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", culture));
			lines.Add("Unity version".PadRight(18) + ": " + Application.unityVersion);
			lines.Add("");

			// ---------------------------------------------------------------------
			// Scan summary
			// ---------------------------------------------------------------------

			AddSection("SCAN SUMMARY");
			AddKeyValue("Elapsed wall time", wallSeconds.ToString("0.000", culture) + " s");
			AddKeyValue("Scanned assets", assetCount.ToString(culture));
			AddKeyValue("Analyzer calls", totalAnalyzerCalls.ToString(culture));
			AddKeyValue(
				"Background worker count",
				ScanPerformance.MaximumWorkerCount(settings).ToString(culture)
				+ "  (configured "
				+ settings.scanWorkerCount.ToString(culture)
				+ ")");
			AddKeyValue("Background asset work", timing.BackgroundAssetCompleted.ToString(culture) + "/" + timing.BackgroundAssetWorkItems.ToString(culture));
			AddKeyValue("Background asset wall time", Seconds(timing.BackgroundAssetWallMilliseconds));
			AddKeyValue("Unity fact extraction time", Seconds(timing.BackgroundAssetExtractionMilliseconds));
			AddKeyValue("Scan pacing mode", settings.scanPacingMode.ToString());
			AddKeyValue("Background queue capacity", timing.BackgroundAssetQueueCapacity.ToString(culture));
			AddKeyValue("Background queue high-water", timing.BackgroundAssetQueueHighWater.ToString(culture));
			AddKeyValue("Heavy queue high-water", timing.BackgroundAssetHeavyQueueHighWater.ToString(culture));
			AddKeyValue("Result queue high-water", timing.BackgroundAssetResultQueueHighWater.ToString(culture));
			AddKeyValue("Ordered pending high-water", timing.BackgroundAssetOrderedPendingHighWater.ToString(culture));
			AddKeyValue("Outstanding work high-water", timing.BackgroundAssetOutstandingHighWater.ToString(culture));
			AddKeyValue("Backpressure events", timing.BackgroundAssetBackpressureEvents.ToString(culture));
			AddKeyValue("Worker target (final / peak)", timing.BackgroundWorkerTarget.ToString(culture) + " / " + timing.BackgroundAssetWorkerTargetHighWater.ToString(culture));
			AddKeyValue("Cache-write high-water", timing.BackgroundCacheWriteHighWater.ToString(culture));
			AddKeyValue("Heavy background work", timing.BackgroundAssetHeavyWorkItems.ToString(culture));
			AddKeyValue("Worker utilization", timing.BackgroundAssetWorkerUtilizationPercent.ToString("0.0", culture) + "%");
			AddKeyValue("Background cache writes committed", timing.BackgroundCacheWritesCommitted.ToString(culture));
			AddKeyValue("Background project passes", timing.BackgroundProjectCompleted.ToString(culture) + "/" + timing.BackgroundProjectPasses.ToString(culture));
			AddKeyValue("Background project wall time", Seconds(timing.BackgroundProjectWallMilliseconds));
			AddKeyValue("Scan frame budget", ScanPerformance.EffectiveFrameBudgetMs(settings) + " ms");
			AddKeyValue("Scan batch limit", ScanPerformance.EffectiveBatchSize(settings).ToString(culture));
			AddKeyValue("Profiling mode", ScanBenchmark.EffectiveMode(settings).ToString());
			AddKeyValue("Benchmark identifier", timing.BenchmarkIdentifier);
			AddKeyValue("Project state", AssetDatabaseThreadSafeUtility.ProjectStateDiagnostics());
			if (m_Context.References != null) AddKeyValue("Reference index", m_Context.References.Diagnostics);
			AddKeyValue("Detailed performance logging", settings.enableScanPerformanceLogging ? "enabled" : "disabled");
			lines.Add("");

			// ---------------------------------------------------------------------
			// Cache
			// ---------------------------------------------------------------------

			AddSection("CACHE PERFORMANCE");

			if (cacheTotal > 0)
			{
				AddKeyValue("Cache hits", timing.CacheHits.ToString(culture));
				AddKeyValue("Cache misses", timing.CacheMisses.ToString(culture));
				AddKeyValue("Cache lookups", cacheTotal.ToString(culture));
				AddKeyValue("Cache hit rate", (cacheHitRate * 100.0).ToString("0.00", culture) + "%");
				AddKeyValue("Cache miss rate", ((1.0 - cacheHitRate) * 100.0).ToString("0.00", culture) + "%");
			}
			else
			{
				AddKeyValue("Cache lookups", "none recorded");
			}

			double loadCacheMs = 0.0;
			double saveCacheMs = 0.0;

			timing.Stages.TryGetValue("load cache", out loadCacheMs);
			timing.Stages.TryGetValue("save cache", out saveCacheMs);

			AddKeyValue("Cache load time", Seconds(loadCacheMs));
			AddKeyValue("Cache save time", Seconds(saveCacheMs));

			lines.Add("");

			// ---------------------------------------------------------------------
			// Timing accounting
			// ---------------------------------------------------------------------

			AddSection("TIMING ACCOUNTING");
			AddKeyValue("Wall time", Seconds(wallMs));
			AddKeyValue("Measured stage work", Seconds(stageWorkMs));
			AddKeyValue("Analyzer time", Seconds(analyzerWorkMs) + "  [per-asset analyzers]");

			if (perAssetMs > 0.0)
			{
				double analyzerShare = analyzerWorkMs / perAssetMs;
				double nonAnalyzerPerAsset =
					Math.Max(0.0, perAssetMs - analyzerWorkMs);

				AddKeyValue("Analyzer share of per-asset scan", (analyzerShare * 100.0).ToString("0.0", culture) + "%");
				AddKeyValue(
					"Non-analyzer per-asset overhead",
					Seconds(nonAnalyzerPerAsset)
					+ "  ("
					+ ((1.0 - analyzerShare) * 100.0).ToString("0.0", culture)
					+ "%)");
			}

			AddKeyValue("Measured work outside per-asset scan", Seconds(explicitlyMeasuredOutsidePerAsset));

			if (unaccountedVsWall >= 0.0)
			{
				AddKeyValue("Wall time beyond measured stage work", Seconds(unaccountedVsWall) + "  (" + Percent(unaccountedVsWall, wallMs) + ")");
			}
			else
			{
				AddKeyValue("Timing overlap / instrumentation", Seconds(-unaccountedVsWall) + "  (measured stages exceed wall time)");
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Stages
			// ---------------------------------------------------------------------

			AddSection("STAGES");

			if (stages.Count == 0)
			{
				lines.Add("  No stage timings recorded.");
			}
			else
			{
				int nameWidth = 0;

				for (int i = 0; i < stages.Count; i++)
					nameWidth = Math.Max(nameWidth, stages[i].Key.Length);

				nameWidth = Math.Max(nameWidth, "Stage".Length);

				lines.Add(
					"  "
					+ "Stage".PadRight(nameWidth)
					+ " | "
					+ "Time".PadLeft(12)
					+ " | "
					+ "Share".PadLeft(8));

				lines.Add(
					"  "
					+ new string('-', nameWidth)
					+ "-+-"
					+ new string('-', 12)
					+ "-+-"
					+ new string('-', 8));

				for (int i = 0; i < stages.Count; i++)
				{
					double stageMs = stages[i].Value;

					lines.Add(
						"  "
						+ stages[i].Key.PadRight(nameWidth)
						+ " | "
						+ Seconds(stageMs).PadLeft(12)
						+ " | "
						+ Percent(stageMs, stageWorkMs).PadLeft(8));
				}
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Analyzers
			// ---------------------------------------------------------------------

			AddSection("ANALYZERS");

			if (analyzers.Count == 0)
			{
				lines.Add("  No analyzer timings recorded.");
			}
			else
			{
				int nameWidth = "Analyzer".Length;

				for (int i = 0; i < analyzers.Count; i++)
					nameWidth = Math.Max(nameWidth, analyzers[i].Key.Length);

				lines.Add(
					"  "
					+ "Analyzer".PadRight(nameWidth)
					+ " | "
					+ "Total".PadLeft(12)
					+ " | "
					+ "Calls".PadLeft(8)
					+ " | "
					+ "Avg / Call".PadLeft(12)
					+ " | "
					+ "Share".PadLeft(8));

				lines.Add(
					"  "
					+ new string('-', nameWidth)
					+ "-+-"
					+ new string('-', 12)
					+ "-+-"
					+ new string('-', 8)
					+ "-+-"
					+ new string('-', 12)
					+ "-+-"
					+ new string('-', 8));

				for (int i = 0; i < analyzers.Count; i++)
				{
					string analyzerName = analyzers[i].Key;
					double analyzerMs = analyzers[i].Value;

					timing.AnalyzerCalls.TryGetValue(
						analyzerName,
						out int calls);

					double averageMs = calls > 0
						? analyzerMs / calls
						: 0.0;

					lines.Add(
						"  "
						+ analyzerName.PadRight(nameWidth)
						+ " | "
						+ Seconds(analyzerMs).PadLeft(12)
						+ " | "
						+ calls.ToString(culture).PadLeft(8)
						+ " | "
						+ Milliseconds(averageMs).PadLeft(12)
						+ " | "
						+ Percent(analyzerMs, analyzerWorkMs).PadLeft(8));
				}
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Diagnostic extraction hot spots
			// ---------------------------------------------------------------------

			if (timing.DiagnosticTimings.Count > 0)
			{
				AddSection("DIAGNOSTIC EXTRACTION HOT SPOTS");
				var diagnostics = new List<KeyValuePair<string, double>>(timing.DiagnosticTimings);
				diagnostics.Sort((a, b) => b.Value.CompareTo(a.Value));
				int diagnosticNameWidth = "Diagnostic".Length;
				for (int i = 0; i < diagnostics.Count; i++) diagnosticNameWidth = Math.Max(diagnosticNameWidth, diagnostics[i].Key.Length);
				lines.Add("  " + "Diagnostic".PadRight(diagnosticNameWidth) + " | " + "Total".PadLeft(12));
				lines.Add("  " + new string('-', diagnosticNameWidth) + "-+-" + new string('-', 12));
				for (int i = 0; i < diagnostics.Count; i++)
					lines.Add("  " + diagnostics[i].Key.PadRight(diagnosticNameWidth) + " | " + Seconds(diagnostics[i].Value).PadLeft(12));
				lines.Add("");
			}

			// ---------------------------------------------------------------------
			// Analyzer hot spots
			// ---------------------------------------------------------------------

			AddSection("ANALYZER HOT SPOTS");

			if (analyzers.Count == 0)
			{
				lines.Add("  No analyzer hot spots recorded.");
			}
			else
			{
				int shown = Math.Min(10, analyzers.Count);

				const int totalWidth = 12;
				const int callsWidth = 8;
				const int avgWidth = 12;
				const int perAssetWidth = 18;

				int nameWidth = "Analyzer".Length;

				for (int i = 0; i < shown; i++)
					nameWidth = Math.Max(nameWidth, analyzers[i].Key.Length);

				lines.Add(
					"  "
					+ "Analyzer".PadRight(nameWidth)
					+ " | "
					+ "Total".PadLeft(totalWidth)
					+ " | "
					+ "Calls".PadLeft(callsWidth)
					+ " | "
					+ "Avg / Call".PadLeft(avgWidth)
					+ " | "
					+ "Per Scanned Asset".PadLeft(perAssetWidth));

				lines.Add(
					"  "
					+ new string('-', nameWidth)
					+ "-+-"
					+ new string('-', totalWidth)
					+ "-+-"
					+ new string('-', callsWidth)
					+ "-+-"
					+ new string('-', avgWidth)
					+ "-+-"
					+ new string('-', perAssetWidth));

				for (int i = 0; i < shown; i++)
				{
					string analyzerName = analyzers[i].Key;
					double analyzerMs = analyzers[i].Value;

					timing.AnalyzerCalls.TryGetValue(
						analyzerName,
						out int calls);

					double averageMs = calls > 0
						? analyzerMs / calls
						: 0.0;

					double perAssetEquivalent = assetCount > 0
						? analyzerMs / assetCount
						: 0.0;

					lines.Add(
						"  "
						+ analyzerName.PadRight(nameWidth)
						+ " | "
						+ Seconds(analyzerMs).PadLeft(totalWidth)
						+ " | "
						+ calls.ToString(culture).PadLeft(callsWidth)
						+ " | "
						+ Milliseconds(averageMs).PadLeft(avgWidth)
						+ " | "
						+ Milliseconds(perAssetEquivalent).PadLeft(perAssetWidth));
				}
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Per-rule timing
			// ---------------------------------------------------------------------
			AddSection("PER-RULE TIMING");
			if (!ScanBenchmark.HasDeep(settings)) lines.Add("  Deep per-rule timing is disabled for this run. Use DeepProfiling or Validation when rule samples are required.");
			else if (timing.Rules.Count == 0) lines.Add("  No explicitly instrumented rule evaluations were recorded.");
			else
			{
				// Normalize/merge by RuleId so a rule can never appear twice because of accidental duplicate dictionary keys or legacy instrumentation variants.
				var merged = new Dictionary<string, RuleTimingStats>(StringComparer.Ordinal);
				foreach (var source in timing.Rules.Values)
				{
					string id = (source.RuleId ?? string.Empty).Trim();
					if (!merged.TryGetValue(id, out var dst)) merged[id] = dst = new RuleTimingStats(id, true);
					dst.MergeFrom(source);
				}
				var ruleRows = new List<RuleTimingStats>(merged.Values);
				ruleRows.Sort((a, b) => TotalRuleMs(b).CompareTo(TotalRuleMs(a)));
				lines.Add("  Rule                 Eval  Issues    Inc.Est.   Exclusive      Mean  Median*     P95*     P99*       Max | Highest/lowest estimated times");
				lines.Add("  -------------------  ----  ------  ---------  ---------  --------  --------  --------  --------  -------- | -------------------------------");
				for (int r = 0; r < ruleRows.Count; r++)
				{
					var row = ruleRows[r];
					if (row.Evaluations == 0) continue;
					double total = row.TotalMilliseconds;
					double exclusiveTotal = row.DirectMilliseconds;
					double mean = total / row.Evaluations;
					string extremes = FormatSample(row.Slowest1) + (row.Slowest2 != null ? "; " + FormatSample(row.Slowest2) : "") + " / " + FormatSample(row.Fastest1) + (row.Fastest2 != null ? "; " + FormatSample(row.Fastest2) : "");
					lines.Add("  " + row.RuleId.PadRight(20) + row.Evaluations.ToString(culture).PadLeft(6) + row.Issues.ToString(culture).PadLeft(8) + Seconds(total).PadLeft(11) + Seconds(exclusiveTotal).PadLeft(11) + Milliseconds(mean).PadLeft(10) + Milliseconds(row.Percentile(.50)).PadLeft(10) + Milliseconds(row.Percentile(.95)).PadLeft(10) + Milliseconds(row.Percentile(.99)).PadLeft(10) + Milliseconds(row.MaxMilliseconds).PadLeft(10) + " | " + extremes);
				}

				lines.Add("  Inc.Est. is the exact exclusive rule time plus the estimated shared analyzer cost allocated to this rule.");
				lines.Add("  Median/P95/P99 are logarithmic-bucket estimates of direct rule-scope time; exact slowest/fastest samples are retained in bounded memory.");
				lines.Add("  Detailed timing retention is bounded so enabling performance logging does not scale linearly with million-asset projects.");
			}

			AddSection("RULE TIMING ACCOUNTING");
			if (timing.AnalyzerRuleAccounting.Count == 0)
			{
				lines.Add("  No analyzer/rule accounting recorded.");
			}
			else
			{
				lines.Add("  Analyzer                   Analyzer Time   With Rules    Direct Rule Time   Shared Measured   Shared Allocated   Attributed Rule   Coverage  Residual  Invocations");
				lines.Add("  -------------------------  --------------  ------------  -----------------  ---------------  -----------------  ---------------  --------  --------  -----------");
				foreach (var pair in timing.AnalyzerRuleAccounting)
				{
					var info = pair.Value;
					double attributed = info.RuleMilliseconds + info.AllocatedSharedMilliseconds;
					double attributableAnalyzerMs = info.AnalyzerMillisecondsWithRules;
					double coverage = attributableAnalyzerMs > 0.0 ? attributed / attributableAnalyzerMs : 0.0;
					double allocationResidual = Math.Abs(info.SharedMilliseconds - info.AllocatedSharedMilliseconds);
					lines.Add("  " + info.AnalyzerName.PadRight(25)
						+ Seconds(info.AnalyzerMilliseconds).PadLeft(14)
						+ Seconds(attributableAnalyzerMs).PadLeft(13)
						+ Seconds(info.RuleMilliseconds).PadLeft(19)
						+ Seconds(info.SharedMilliseconds).PadLeft(17)
						+ Seconds(info.AllocatedSharedMilliseconds).PadLeft(19)
						+ Seconds(attributed).PadLeft(17)
						+ (coverage * 100.0).ToString("0.0", culture).PadLeft(10) + "%"
						+ Seconds(allocationResidual).PadLeft(10)
						+ (info.InvocationCount.ToString(culture) + " (" + info.InvocationsWithRules.ToString(culture) + "/" + info.InvocationsWithoutRules.ToString(culture) + ")").PadLeft(12));
				}
				lines.Add("  Direct Rule Time is exact stopwatch time inside explicit rule scopes; nested child-rule time is excluded from parents.");
				lines.Add("  Shared Measured is analyzer work outside those scopes; Shared Allocated is an explicit estimate distributed across the rules that actually ran.");
				lines.Add("  Invocations is total tracked analyzer invocations (with-rules / without-rules); invocations without rules remain unattributed rather than fabricated into rule costs.");
				lines.Add("  Coverage is relative to analyzer time from invocations that actually evaluated at least one rule. Allocation residual should be near zero; nonzero residual indicates accounting drift.");
				lines.Add("  Analyzer accounting includes both per-asset and project-wide analyzer invocations; the ANALYZERS table above remains per-asset only.");
				lines.Add("  For invocations with rules, Attributed Rule Time reconciles to analyzer time within measurement precision; the per-rule Inc.Est. values are estimates, not independently measured CPU time.");
			}

			// ---------------------------------------------------------------------
			// Slowest assets
			// ---------------------------------------------------------------------

			AddSection("SLOWEST ASSETS");

			if (Result.SlowAssets.Count == 0)
			{
				lines.Add("  No assets exceeded the slow-asset threshold.");
			}
			else
			{
				int shown = Math.Min(20, Result.SlowAssets.Count);

				int pathWidth = "Asset".Length;

				for (int i = 0; i < shown; i++)
				{
					pathWidth = Math.Max(
						pathWidth,
						Result.SlowAssets[i].Key.Length);
				}

				lines.Add(
					"  "
					+ "Time".PadLeft(12)
					+ " | "
					+ "Asset".PadRight(pathWidth));

				lines.Add(
					"  "
					+ new string('-', 12)
					+ "-+-"
					+ new string('-', pathWidth));

				for (int i = 0; i < shown; i++)
				{
					double assetMs = Result.SlowAssets[i].Value;
					string assetPath = Result.SlowAssets[i].Key;

					lines.Add(
						"  "
						+ Seconds(assetMs).PadLeft(12)
						+ " | "
						+ assetPath);
				}
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Throughput
			// ---------------------------------------------------------------------

			AddSection("THROUGHPUT");

			if (wallSeconds > 0.0 && assetCount > 0)
			{
				double assetsPerSecond = assetCount / wallSeconds;
				double msPerAsset = wallMs / assetCount;

				AddKeyValue("Assets / second", assetsPerSecond.ToString("0.00", culture));
				AddKeyValue("Wall-time / scanned asset", msPerAsset.ToString("0.000", culture) + " ms");
			}
			else
			{
				lines.Add("  Throughput could not be calculated.");
			}

			if (totalAnalyzerCalls > 0 && wallSeconds > 0.0)
			{
				AddKeyValue("Analyzer calls / second", (totalAnalyzerCalls / wallSeconds).ToString("0.00", culture));

				AddKeyValue("Analyzer calls / scanned asset", (totalAnalyzerCalls / (double)Math.Max(1, assetCount)).ToString("0.00", culture));
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Failures
			// ---------------------------------------------------------------------

			AddSection("FAILURES");

			AddKeyValue("Failed assets", Result.FailedAssets.ToString(culture));

			if (assetCount > 0)
			{
				double failureRate = Result.FailedAssets / (double)assetCount * 100.0;

				AddKeyValue("Failure rate", failureRate.ToString("0.000", culture) + "%");
			}

			if (failures.Count == 0)
			{
				AddKeyValue("Failure by analyzer", "none");
			}
			else
			{
				lines.Add("");
				lines.Add("  Failure by analyzer:");

				int nameWidth = "Analyzer".Length;

				for (int i = 0; i < failures.Count; i++)
					nameWidth = Math.Max(nameWidth, failures[i].Key.Length);

				lines.Add(
					"    "
					+ "#".PadRight(5)
					+ " | "
					+ "Analyzer".PadRight(nameWidth)
					+ " | "
					+ "Failures".PadLeft(8));

				lines.Add(
					"    "
					+ new string('-', 5)
					+ "-+-"
					+ new string('-', nameWidth)
					+ "-+-"
					+ new string('-', 8));

				for (int i = 0; i < failures.Count; i++)
				{
					lines.Add(
						"    "
						+ (i + 1).ToString(culture).PadLeft(5)
						+ " | "
						+ failures[i].Key.PadRight(nameWidth)
						+ " | "
						+ failures[i].Value.ToString(culture).PadLeft(8));
				}
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Result summary
			// ---------------------------------------------------------------------

			AddSection("RESULT SUMMARY");

			AddKeyValue("Critical findings", Result.CriticalCount.ToString(culture));
			AddKeyValue("Warning findings", Result.WarningCount.ToString(culture));
			AddKeyValue("Info findings", Result.InfoCount.ToString(culture));
			AddKeyValue("Total findings", Result.IssueCount.ToString(culture));
			AddKeyValue("Total scanned file size", DisplayUtility.FormatBytes(Result.TotalFileSize));
			AddKeyValue("Potential combined savings", DisplayUtility.FormatBytes(Result.PotentialSavings));
			AddKeyValue("Potential memory savings", DisplayUtility.FormatBytes(Result.PotentialMemorySavings));
			AddKeyValue("Potential disk savings", DisplayUtility.FormatBytes(Result.PotentialDiskSavings));

			lines.Add("");

			// ---------------------------------------------------------------------
			// Project-wide passes
			// ---------------------------------------------------------------------

			AddSection("PROJECT-WIDE PASSES");
			AddKeyValue("Duplicate pass", settings.scanDuplicates ? "enabled" : "disabled");
			AddKeyValue("Unused pass", settings.scanUnused ? "enabled" : "disabled");
			AddKeyValue("Cross-asset findings stale", Result.CrossAssetPassesStale ? "yes" : "no");

			if (Result.CrossAssetPassesStale
				&& !string.IsNullOrEmpty(Result.CrossAssetStaleReason))
			{
				lines.Add("");
				lines.Add("  Cross-asset stale reason:");
				lines.Add("    " + Result.CrossAssetStaleReason);
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Scan completeness
			// ---------------------------------------------------------------------

			AddSection("SCAN COMPLETENESS");
			AddKeyValue("Scan complete", Result.IsComplete ? "yes" : "no");

			AddKeyValue("Scan aborted", Result.Aborted ? "yes" : "no");

			if (Result.CompletenessCaveats.Count == 0)
			{
				AddKeyValue("Completeness caveats", "none");
			}
			else
			{
				AddKeyValue("Completeness caveats", Result.CompletenessCaveats.Count.ToString(culture));

				lines.Add("");

				for (int i = 0; i < Result.CompletenessCaveats.Count; i++)
				{
					lines.Add(
						"  (" + (i + 1).ToString(culture).PadLeft(2)
						+ ") " + Result.CompletenessCaveats[i]);
				}
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Configuration
			// ---------------------------------------------------------------------

			AddSection("SCAN CONFIGURATION");
			AddKeyValue("Scope mode", settings.scopeMode.ToString());
			AddKeyValue("Prefabs", settings.scanPrefabs ? "enabled" : "disabled");
			AddKeyValue("Scenes",
				settings.scanScenes ? "enabled" : "disabled");
			AddKeyValue("Duplicates", settings.scanDuplicates ? "enabled" : "disabled");
			AddKeyValue("Unused assets", settings.scanUnused ? "enabled" : "disabled");
			AddKeyValue("Deep texture scan", settings.deepTextureScan ? "enabled" : "disabled");
			AddKeyValue("Maximum asset size", settings.maxAssetSizeMb.ToString("0.##", culture) + " MB");
			AddKeyValue("Configured worker count", settings.scanWorkerCount.ToString(culture));
			AddKeyValue("Maximum worker count", ScanPerformance.MaximumWorkerCount(settings).ToString(culture));
			AddKeyValue("Effective frame budget", ScanPerformance.EffectiveFrameBudgetMs(settings)
				+ " ms");
			AddKeyValue("Effective batch size", ScanPerformance.EffectiveBatchSize(settings).ToString(culture));

			lines.Add("");

			// ---------------------------------------------------------------------
			// Automatic optimization hints
			// ---------------------------------------------------------------------

			AddSection("AUTOMATIC OPTIMIZATION HINTS");

			bool anyHint = false;

			if (analyzers.Count > 0 && analyzerWorkMs > 0.0)
			{
				var topAnalyzer = analyzers[0];

				double topShare =
					topAnalyzer.Value / analyzerWorkMs;

				if (topShare >= 0.40)
				{
					anyHint = true;

					lines.Add(
						"  HIGH   | "
						+ topAnalyzer.Key
						+ " consumes "
						+ (topShare * 100.0).ToString("0.0", culture)
						+ "% of measured analyzer time.");
				}
			}

			if (perAssetMs > 0.0
				&& analyzerWorkMs > perAssetMs * 0.80)
			{
				anyHint = true;

				lines.Add(
					"  MEDIUM | Most per-asset time is inside analyzers. "
					+ "Analyzer-level optimization is likely more valuable "
					+ "than scanner bookkeeping.");
			}

			if (cacheTotal > 0 && cacheHitRate < 0.80)
			{
				anyHint = true;

				lines.Add(
					"  HIGH   | Cache hit rate is only "
					+ (cacheHitRate * 100.0).ToString("0.0", culture)
					+ "%. Cache reuse/invalidation should be investigated.");
			}

			if (Result.SlowAssets.Count > 0)
			{
				anyHint = true;

				lines.Add(
					"  MEDIUM | "
					+ Result.SlowAssets.Count.ToString(culture)
					+ " asset(s) exceeded the slow-asset threshold. "
					+ "Inspect the slowest-assets section.");
			}

			if (Result.FailedAssets > 0)
			{
				anyHint = true;

				lines.Add(
					"  MEDIUM | "
					+ Result.FailedAssets.ToString(culture)
					+ " asset(s) failed analysis. "
					+ "Performance measurements may therefore be incomplete.");
			}

			if (unaccountedVsWall > wallMs * 0.10)
			{
				anyHint = true;

				lines.Add(
					"  MEDIUM | More than 10% of wall time is outside "
					+ "explicitly measured stages. Additional instrumentation "
					+ "may be useful.");
			}

			if (settings.scanDuplicates && settings.scanUnused)
			{
				anyHint = true;

				lines.Add(
					"  INFO   | Duplicate and unused passes are both enabled. "
					+ "Their project-wide stages should be considered separately "
					+ "when profiling.");
			}

			if (!anyHint)
			{
				lines.Add(
					"  No dominant optimization warning was inferred from this scan.");
			}

			lines.Add("");

			// ---------------------------------------------------------------------
			// Footer
			// ---------------------------------------------------------------------

			AddSection("END OF REPORT");

			string report = string.Join("\n", lines.ToArray());

			Debug.Log(report);

			try
			{
				string folder =
					ProjectOptimizerConstants.ScanTimingsFolder;

				if (!Directory.Exists(folder))
					Directory.CreateDirectory(folder);

				string stamp = DateTime.Now.ToString(
					"yyyyMMdd-HHmmss-fff",
					culture);

				string path = Path.Combine(
					folder,
					"Scan-" + stamp + ".txt");

				File.WriteAllText(path, report);
			}
			catch (Exception e)
			{
				Debug.LogWarning(
					ProjectOptimizerConstants.LogPrefix + " Could not write scan timing report: "
					+ e.Message);
			}
		}

		/// <summary>
		/// Writes down every reason this scan is not a complete picture of the project, so the UI and
		/// the exports can say so rather than letting an empty category read as a clean bill.
		/// </summary>
		void RecordCompleteness(ScanScope scope, OptimizerSettings settings)
		{
			if (Result == null) return;

			RecordAnalysisLimitations(Result);

			if (settings.scopeMode == ScanScopeMode.SelectedFolders)
				Result.AddCaveat("Only the folders you selected were scanned.");

			if (settings.scopeMode == ScanScopeMode.ShippedInBuild)
				Result.AddCaveat("Only assets reachable from the build were scanned. " + ScanScope.ReachabilityCaveat);

			if (scope != null && !scope.BuildReachabilityComplete)
			{
				// Worse than the ordinary scope caveat: this one means the reachable set is missing
				// assets it should contain, so anything reported as unreachable may simply be behind
				// the part of the walk that failed.
				Result.AddCaveat("The build reachability walk did not complete, so assets may be reported as "
								 + "unreachable when they are not. " + scope.ReachabilityIncompletenessSummary());
			}

			if (scope != null)
			{
				if (scope.SkippedByKind > 0)
					Result.AddCaveat(scope.SkippedByKind + " asset(s) were skipped because their type is switched off.");

				if (scope.SkippedTooLarge > 0)
				{
					Result.AddCaveat(scope.SkippedTooLarge + " asset(s) were skipped for being over the "
									 + settings.maxAssetSizeMb + " MB size limit ("
									 + DisplayUtility.FormatBytes(scope.SkippedTooLargeBytes)
									 + " of content nothing looked at).");
				}

				if (scope.SkippedByFolder > 0 && settings.scopeMode == ScanScopeMode.EntireProject)
					Result.AddCaveat(scope.SkippedByFolder + " asset(s) are in excluded folders.");
			}

			if (Result.FailedAssets > 0)
			{
				var parts = new List<string>();
				foreach (var pair in Result.FailuresByAnalyzer) parts.Add(pair.Value + " in " + pair.Key);
				Result.AddCaveat(Result.FailedAssets + " asset(s) could not be analyzed ("
								 + string.Join(", ", parts.ToArray()) + "); they are missing from these results "
								 + "rather than clean.");
			}

			if (m_Context.References != null && !m_Context.References.IsComplete)
			{
				Result.AddCaveat("The reference graph is incomplete, so unused and duplicate findings are weaker "
								 + "than usual. " + m_Context.References.IncompletenessSummary());
			}

			if (m_Context.MaterialUsage != null && m_Context.MaterialUsage.Partial)
			{
				Result.AddCaveat("Material usage counts cover only what this scan walked, so a low count means "
								 + "\"not seen here\" rather than \"not used\".");
			}

			if (!settings.scanPrefabs) Result.AddCaveat("Prefabs were not scanned.");
			if (!settings.scanScenes) Result.AddCaveat("Scenes were not scanned.");
			if (!settings.scanDuplicates) Result.AddCaveat("The duplicate pass did not run.");
			if (!settings.scanUnused) Result.AddCaveat("The unused-asset pass did not run.");
		}

		static void RecordAnalysisLimitations(ScanResult result)
		{
			if (result == null) return;

			try
			{
				var manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "Packages", "manifest.json");
				if (File.Exists(manifestPath))
				{
					var manifest = File.ReadAllText(manifestPath);
					if (manifest.IndexOf("\"" + ProjectOptimizerConstants.AddressablesPackageId + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
						result.AddLimitation("Addressables is installed: static AssetDatabase reachability does not prove catalog/build-bundle reachability, so unused findings should not be treated as proof that an asset is absent from an Addressables build.");
				}
			}
			catch (Exception e)
			{
				result.AddLimitation("The project package manifest could not be inspected for dynamic asset-loading packages: " + e.Message);
			}
		}
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Declares where an analyzer does its expensive work.</summary>
	public enum ScanExecutionMode
	{
		MainThread = 0,
		Hybrid = 1,
		Background = 2
	}

	/// <summary>
	/// An analyzer that can execute from a ThreadPool worker after the scanner has extracted all
	/// Unity-dependent facts it needs on the main thread. The same analyzer instance may receive
	/// concurrent assets, so implementations must keep worker-time state local or thread-safe and
	/// must not call Unity APIs from <see cref="AnalyzeBackground"/>.
	/// </summary>
	public interface IBackgroundAssetAnalyzer : IAssetAnalyzer
	{
		/// <summary>Called on the Unity main thread before background work starts.</summary>
		void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext);

		/// <summary>Runs without Unity APIs. Implementations may use ordinary .NET I/O and CPU work.</summary>
		AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context);
	}

	/// <summary>Allows an analyzer to opt out of background execution for asset kinds it cannot fully materialize into managed data.</summary>
	public interface ISelectiveBackgroundAssetAnalyzer
	{
		bool SupportsBackground(string assetPath);
	}

	/// <summary>
	/// An optional project-level analyzer whose calculation can execute away from the Unity main thread.
	/// The returned entries must be completely materialized and contain no Unity API work in their
	/// construction or finalization path.
	/// </summary>
	public interface IBackgroundProjectAnalyzer : IProjectAnalyzer
	{
		IReadOnlyList<AssetEntry> AnalyzeBackground(BackgroundProjectContext context);
	}

	/// <summary>Marks a background project analyzer that cannot run until a complete project-wide reference graph exists.</summary>
	public interface IReferenceAwareBackgroundProjectAnalyzer : IBackgroundProjectAnalyzer
	{
	}

	/// <summary>Background analyzer whose result needs a small Unity-main-thread completion step.
	/// The background phase must never touch Unity APIs; it returns its immutable/native-independent payload
	/// through <see cref="AssetEntry.BackgroundPayload"/>.
	/// </summary>
	public interface IHybridBackgroundAssetAnalyzer : IBackgroundAssetAnalyzer
	{
		AssetEntry CompleteBackground(AssetEntry entry, ScanContext mainContext);
	}

	/// <summary>
	/// Immutable/safe data exposed to a background asset analyzer. Deliberately contains no UnityEngine
	/// or UnityEditor types so an analyzer cannot accidentally cross the main-thread boundary.
	/// </summary>
	public sealed class BackgroundAssetContext
	{
		readonly ConcurrentDictionary<string, object> m_Facts = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
		readonly ConcurrentDictionary<string, ConcurrentBag<string>> m_FactKeysByAsset = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.Ordinal);
		readonly BlockingCollection<BackgroundCacheWrite> m_MainThreadCacheWrites;
		readonly BlockingCollection<BackgroundCacheWrite> m_WorkerCacheWrites;
		readonly Func<string, string> m_PathByGuid;
		readonly CancellationToken m_CancellationToken;
		int m_MainThreadCacheWriteHighWater;
		int m_WorkerCacheWriteHighWater;
		readonly ConcurrentDictionary<string, string> m_DependencyHashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
		readonly ConcurrentDictionary<string, string> m_Guids = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
		readonly ConcurrentDictionary<string, bool> m_PreflightFlags = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
		int m_Completed;
		int m_WorkCount;
		int m_EnumerationComplete;

		public OptimizerSettings Settings { get; }
		public ScanFileMetadata FileMetadata { get; }
		public string ProjectRoot { get; }
		public int Completed => Volatile.Read(ref m_Completed);
		public int WorkCount => Volatile.Read(ref m_WorkCount);
		public bool EnumerationComplete => Volatile.Read(ref m_EnumerationComplete) != 0;
		public int CacheWriteDepth => m_MainThreadCacheWrites.Count + m_WorkerCacheWrites.Count;
		public int MainThreadCacheWriteHighWater => Volatile.Read(ref m_MainThreadCacheWriteHighWater);
		public int WorkerCacheWriteHighWater => Volatile.Read(ref m_WorkerCacheWriteHighWater);

		internal void SetWorkCount(int count) => Volatile.Write(ref m_WorkCount, Math.Max(0, count));
		internal void MarkScheduled() => Interlocked.Increment(ref m_WorkCount);
		internal void MarkCompleted() => Interlocked.Increment(ref m_Completed);
		internal void MarkEnumerationComplete() => Volatile.Write(ref m_EnumerationComplete, 1);

		public BackgroundAssetContext(OptimizerSettings settings, ScanFileMetadata fileMetadata, string projectRoot, Func<string, string> pathByGuid = null, int cacheWriteCapacity = 1024, CancellationToken cancellationToken = default)
		{
			Settings = settings;
			FileMetadata = fileMetadata;
			ProjectRoot = projectRoot ?? string.Empty;
			m_PathByGuid = pathByGuid;
			m_CancellationToken = cancellationToken;
			int workerCapacity = Math.Max(128, Math.Min(8192, cacheWriteCapacity));
			int mainCapacity = Math.Max(512, Math.Min(32768, workerCapacity * 2));
			m_MainThreadCacheWrites = new BlockingCollection<BackgroundCacheWrite>(mainCapacity);
			m_WorkerCacheWrites = new BlockingCollection<BackgroundCacheWrite>(workerCapacity);
		}

		public string GetPathForGuid(string guid)
		{
			if (string.IsNullOrEmpty(guid) || m_PathByGuid == null) return string.Empty;
			try { return m_PathByGuid(guid) ?? string.Empty; } catch { return string.Empty; }
		}

		public string GetDependencyHash(string path)
			=> path != null && m_DependencyHashes.TryGetValue(path, out var value) ? value : null;

		public string GetGuid(string path)
			=> path != null && m_Guids.TryGetValue(path, out var value) ? value : string.Empty;

		public bool GetPreflightFlag(string path)
			=> path != null && m_PreflightFlags.TryGetValue(path, out var value) && value;

		internal void SetDependencyHash(string path, string hash)
		{ if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(hash)) m_DependencyHashes[path] = hash; }

		internal void SetGuid(string path, string guid)
		{ if (!string.IsNullOrEmpty(path)) m_Guids[path] = guid ?? string.Empty; }

		/// <summary>Seeds common per-asset identity once before any background analyzer-specific preparation runs.</summary>
		internal void PrepareAssetIdentity(string assetPath, ScanContext mainContext)
		{
			if (string.IsNullOrEmpty(assetPath) || mainContext == null) return;
			if (m_DependencyHashes.ContainsKey(assetPath) && m_Guids.ContainsKey(assetPath)) return;
			SetDependencyHash(assetPath, mainContext.GetDependencyHash(assetPath));
			SetGuid(assetPath, mainContext.GetGuid(assetPath));
		}

		internal void SetPreflightFlag(string path, bool value)
		{ if (!string.IsNullOrEmpty(path)) m_PreflightFlags[path] = value; }

		public long GetFileSize(string path)
			=> FileMetadata != null ? FileMetadata.GetFileSize(path) : GetFileInfo(path)?.Length ?? 0L;

		public string ReadText(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			try
			{
				string fullPath = Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
				return File.ReadAllText(fullPath);
			}
			catch { return null; }
		}

		public bool TryGetFact<T>(string cacheKey, out T value) where T : class
		{
			if (!string.IsNullOrEmpty(cacheKey) && m_Facts.TryGetValue(cacheKey, out var cached) && cached is T typed)
			{
				value = typed;
				return true;
			}
			value = null;
			return false;
		}

		public T GetOrMeasure<T>(string assetPath, Func<T> measure) where T : class
			=> GetOrMeasure(assetPath, ScanCache.ScopedKey(assetPath, typeof(T).FullName), measure);

		/// <summary>Measures a background fact with an explicit persistent cache key. This is required when
		/// one fact shape has a scan dimension such as target platform and therefore must not share a row
		/// with a different platform's measurement.</summary>
		public T GetOrMeasure<T>(string assetPath, string cacheKey, Func<T> measure) where T : class
		{
			if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(cacheKey)) return null;
			if (m_Facts.TryGetValue(cacheKey, out var cached) && cached is T typed)
				return typed;

			T value = measure != null ? measure() : null;
			if (value != null)
			{
				m_Facts[cacheKey] = value;
				TrackFactKey(assetPath, cacheKey);
				string hash = GetDependencyHash(assetPath);
				if (!string.IsNullOrEmpty(hash))
					EnqueueCacheWrite(new BackgroundCacheWrite(cacheKey, value, hash));
			}
			return value;
		}

		internal void Seed<T>(string assetPath, T value) where T : class
		{
			if (value == null || string.IsNullOrEmpty(assetPath)) return;
			string key = ScanCache.ScopedKey(assetPath, typeof(T).FullName);
			m_Facts[key] = value;
			TrackFactKey(assetPath, key);
		}

		internal void SeedCached<T>(string cacheKey, T value, bool persist = false) where T : class
		{
			if (value == null || string.IsNullOrEmpty(cacheKey)) return;
			m_Facts[cacheKey] = value;
			string assetPath = ScanCache.BaseAssetPathForRuntime(cacheKey);
			TrackFactKey(assetPath, cacheKey);
			if (persist)
			{
				string hash = GetDependencyHash(assetPath);
				if (!string.IsNullOrEmpty(hash)) EnqueueCacheWrite(new BackgroundCacheWrite(cacheKey, value, hash));
			}
		}

		void TrackFactKey(string assetPath, string cacheKey)
		{
			if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(cacheKey)) return;
			var keys = m_FactKeysByAsset.GetOrAdd(assetPath, _ => new ConcurrentBag<string>());
			keys.Add(cacheKey);
		}

		internal void ReleaseAsset(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return;
			if (m_FactKeysByAsset.TryRemove(assetPath, out var keys))
				while (keys.TryTake(out var key)) m_Facts.TryRemove(key, out _);
			m_DependencyHashes.TryRemove(assetPath, out _);
			m_Guids.TryRemove(assetPath, out _);
			m_PreflightFlags.TryRemove(assetPath, out _);
		}

		void EnqueueCacheWrite(BackgroundCacheWrite write)
		{
			if (string.IsNullOrEmpty(write.Key)) return;
			if (BackgroundRuleExecution.IsBackground)
			{
				try
				{
					m_WorkerCacheWrites.Add(write, m_CancellationToken);
					UpdateHighWater(ref m_WorkerCacheWriteHighWater, m_WorkerCacheWrites.Count);
				}
				catch (OperationCanceledException) when (m_CancellationToken.IsCancellationRequested) { throw; }
			}
			else
			{
				// Main-thread preparation already runs under ProjectScanner's producer backpressure. Keep
				// its queue bounded without ever blocking the Unity thread while it is the only consumer.
				if (!m_MainThreadCacheWrites.TryAdd(write))
					throw new InvalidOperationException("Background cache-write queue is saturated; producer backpressure was not maintained.");
				UpdateHighWater(ref m_MainThreadCacheWriteHighWater, m_MainThreadCacheWrites.Count);
			}
		}

		internal int DrainCacheWrites(List<BackgroundCacheWrite> destination, int maxCount)
		{
			if (destination == null || maxCount <= 0) return 0;
			int count = 0;
			while (count < maxCount)
			{
				if (m_MainThreadCacheWrites.TryTake(out var mainWrite)) { destination.Add(mainWrite); count++; continue; }
				if (m_WorkerCacheWrites.TryTake(out var workerWrite)) { destination.Add(workerWrite); count++; continue; }
				break;
			}
			return count;
		}

		static void UpdateHighWater(ref int target, int current)
		{
			while (true)
			{
				int previous = Volatile.Read(ref target);
				if (current <= previous || Interlocked.CompareExchange(ref target, current, previous) == previous) return;
			}
		}

		static FileInfo GetFileInfo(string path)
		{
			try { return string.IsNullOrEmpty(path) ? null : new FileInfo(path); }
			catch { return null; }
		}
	}

	public readonly struct BackgroundCacheWrite
	{
		public readonly string Key;
		public readonly object Facts;
		public readonly string DependencyHash;
		public BackgroundCacheWrite(string key, object facts, string dependencyHash)
		{
			Key = key;
			Facts = facts;
			DependencyHash = dependencyHash;
		}
	}

	/// <summary>Read-only project data available to a background project analyzer.</summary>
	public sealed class BackgroundProjectContext
	{
		int m_Completed;
		int m_WorkCount;
		public int Completed => Volatile.Read(ref m_Completed);
		public int WorkCount => Volatile.Read(ref m_WorkCount);
		internal void SetWorkCount(int count) => Volatile.Write(ref m_WorkCount, Math.Max(0, count));
		internal void MarkCompleted() => Interlocked.Increment(ref m_Completed);

		public OptimizerSettings Settings { get; }
		public ScanFileMetadata FileMetadata { get; }
		public ReferenceIndex References { get; }
		public string ProjectRoot { get; }

		public BackgroundProjectContext(OptimizerSettings settings, ScanFileMetadata fileMetadata, ReferenceIndex references, string projectRoot)
		{
			Settings = settings;
			FileMetadata = fileMetadata;
			References = references;
			ProjectRoot = projectRoot ?? string.Empty;
		}

		public string GetGuidFromMeta(string assetPath)
		{
			if (FileMetadata != null)
			{
				string prepared = FileMetadata.GetGuid(assetPath);
				if (!string.IsNullOrEmpty(prepared)) return prepared;
			}
			try
			{
				string fullPath = Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar) + ".meta");
				if (!File.Exists(fullPath)) return string.Empty;
				string text = File.ReadAllText(fullPath);
				const string token = "guid:";
				int index = text.IndexOf(token, StringComparison.Ordinal);
				if (index < 0) return string.Empty;
				int start = index + token.Length;
				int end = text.IndexOfAny(new[] { '\r', '\n' }, start);
				if (end < 0) end = text.Length;
				return text.Substring(start, end - start).Trim();
			}
			catch { return string.Empty; }
		}

		public long GetFileSize(string path)
			=> FileMetadata != null ? FileMetadata.GetFileSize(path) : 0L;
	}

	/// <summary>Compact identity for an asset in the current scan. The path remains owned by the scan context.</summary>
	public readonly struct AssetId : IEquatable<AssetId>
	{
		public readonly int Value;
		public AssetId(int value) => Value = value;
		public bool Equals(AssetId other) => Value == other.Value;
		public override bool Equals(object obj) => obj is AssetId other && Equals(other);
		public override int GetHashCode() => Value;
		public static implicit operator int(AssetId id) => id.Value;
		public override string ToString() => Value.ToString();
	}

	public enum BackgroundWorkloadKind
	{
		Mixed = 0,
		IoHeavy = 1,
		CpuHeavy = 2
	}

	/// <summary>Thread-local guard/settings for background rule construction. No Unity singleton lookup is required on workers.</summary>
	internal static class BackgroundRuleExecution
	{
		[ThreadStatic] static OptimizerSettings s_Settings;
		public static OptimizerSettings CurrentSettings => s_Settings;
		public static bool IsBackground => s_Settings != null;
		public static void Enter(OptimizerSettings settings) => s_Settings = settings;
		public static void Exit() => s_Settings = null;
	}

	public readonly struct ScanFailure
	{
		public readonly string Analyzer;
		public readonly string Path;
		public readonly string Message;
		public ScanFailure(string analyzer, string path, string message)
		{
			Analyzer = analyzer;
			Path = path;
			Message = message;
		}
	}

	public readonly struct BackgroundAssetWorkItem
	{
		public readonly AssetId Asset;
		public readonly long Ordinal;
		public readonly string Path;
		public readonly IBackgroundAssetAnalyzer Analyzer;
		public readonly int RouteIndex;
		public readonly bool Heavy;
		public readonly BackgroundWorkloadKind WorkloadKind;

		public BackgroundAssetWorkItem(AssetId asset, long ordinal, string path, IBackgroundAssetAnalyzer analyzer, int routeIndex, bool heavy, BackgroundWorkloadKind workloadKind)
		{
			Asset = asset;
			Ordinal = ordinal;
			Path = path;
			Analyzer = analyzer;
			RouteIndex = routeIndex;
			Heavy = heavy;
			WorkloadKind = workloadKind;
		}
	}

	public readonly struct BackgroundAssetWorkResult
	{
		public readonly BackgroundAssetWorkItem Work;
		public readonly AssetEntry Entry;
		public readonly ScanFailure Failure;
		public readonly bool HasFailure;
		public readonly double ElapsedMilliseconds;

		public BackgroundAssetWorkResult(BackgroundAssetWorkItem work, AssetEntry entry, ScanFailure failure, bool hasFailure, double elapsedMilliseconds)
		{
			Work = work;
			Entry = entry;
			Failure = failure;
			HasFailure = hasFailure;
			ElapsedMilliseconds = elapsedMilliseconds;
		}
	}

	/// <summary>
	/// Live bounded producer/consumer scheduler. Workers start immediately, the Unity thread feeds it
	/// incrementally, and both input and result retention are bounded. There is never one Task per asset.
	/// </summary>
	public sealed class BackgroundAssetScheduler : IDisposable
	{
		readonly BlockingCollection<BackgroundAssetWorkItem> m_NormalQueue;
		readonly BlockingCollection<BackgroundAssetWorkItem> m_HeavyQueue;
		readonly BlockingCollection<BackgroundAssetWorkResult> m_ResultQueue;
		readonly List<Task> m_Workers;
		readonly CancellationToken m_CancellationToken;
		readonly BackgroundAssetContext m_Context;
		readonly ScanTiming[] m_WorkerTimings;
		readonly double m_StartMilliseconds;
		readonly object m_StateGate = new object();
		bool m_InputCompleted;
		bool m_Disposed;
		int m_NormalBeforeHeavy;
		long m_Scheduled;
		long m_Completed;
		long m_HeavyScheduled;
		long m_HeavyCompleted;
		long m_WorkerBusyTicks;
		int m_NormalHighWater;
		int m_HeavyHighWater;
		int m_ResultHighWater;
		int m_WorkerTarget;
		int m_WorkerTargetHighWater;
		int m_ActiveWorkers;
		long m_Materialized;
		long m_OutstandingHighWater;
		long m_BackpressureEvents;
		readonly int m_MaxInFlight;
		readonly Dictionary<AssetId, int> m_AssetOutstanding = new Dictionary<AssetId, int>();
		long m_NextResultOrdinalExpected;

		public Task Completion { get; }
		public int QueueCapacity { get; }
		public int ResultCapacity { get; }
		public int WorkerCount { get; }
		public int ActiveWorkerTarget => Volatile.Read(ref m_WorkerTarget);
		public int WorkerTargetHighWater => Volatile.Read(ref m_WorkerTargetHighWater);
		public int ActiveWorkers => Volatile.Read(ref m_ActiveWorkers);
		public int MaxInFlight => m_MaxInFlight;
		public long ScheduledCount => Interlocked.Read(ref m_Scheduled);
		public long CompletedCount => Interlocked.Read(ref m_Completed);
		public long MaterializedCount => Interlocked.Read(ref m_Materialized);
		public long OutstandingCount => Math.Max(0L, ScheduledCount - MaterializedCount);
		public long OutstandingHighWater => Interlocked.Read(ref m_OutstandingHighWater);
		public long BackpressureEvents => Interlocked.Read(ref m_BackpressureEvents);
		public long HeavyScheduledCount => Interlocked.Read(ref m_HeavyScheduled);
		public long HeavyCompletedCount => Interlocked.Read(ref m_HeavyCompleted);
		public int NormalQueueDepth => m_NormalQueue.Count;
		public int HeavyQueueDepth => m_HeavyQueue.Count;
		public int ResultQueueDepth => m_ResultQueue.Count;
		public int NormalQueueHighWater => Volatile.Read(ref m_NormalHighWater);
		public int HeavyQueueHighWater => Volatile.Read(ref m_HeavyHighWater);
		public int ResultQueueHighWater => Volatile.Read(ref m_ResultHighWater);
		public bool InputCompleted => Volatile.Read(ref m_InputCompleted);
		public bool IsCompleted => Completion != null && Completion.IsCompleted;
		public long NextResultOrdinalExpected => Interlocked.Read(ref m_NextResultOrdinalExpected);
		public double WorkerBusyMilliseconds => Interlocked.Read(ref m_WorkerBusyTicks) * 1000.0 / Stopwatch.Frequency;
		public double WallMilliseconds => Math.Max(0.0, ScanTiming.Now - m_StartMilliseconds);

		BackgroundAssetScheduler(BackgroundAssetContext context, int workerCount, int queueCapacity, int resultCapacity, CancellationToken cancellationToken)
		{
			m_Context = context;
			m_CancellationToken = cancellationToken;
			m_StartMilliseconds = ScanTiming.Now;
			WorkerCount = Math.Max(1, Math.Min(32, workerCount));
			QueueCapacity = Math.Max(64, queueCapacity);
			ResultCapacity = Math.Max(64, resultCapacity);
			m_MaxInFlight = Math.Max(128, Math.Min(8192, QueueCapacity + Math.Max(ResultCapacity / 2, WorkerCount * 8)));
			m_WorkerTarget = ScanPerformance.InitialWorkerTarget(context != null ? context.Settings : null, WorkerCount);
			m_WorkerTargetHighWater = m_WorkerTarget;
			m_NormalQueue = new BlockingCollection<BackgroundAssetWorkItem>(QueueCapacity);
			m_HeavyQueue = new BlockingCollection<BackgroundAssetWorkItem>(Math.Max(16, QueueCapacity / 4));
			m_ResultQueue = new BlockingCollection<BackgroundAssetWorkResult>(ResultCapacity);
			m_Workers = new List<Task>(WorkerCount);
			m_WorkerTimings = new ScanTiming[WorkerCount];

			for (int i = 0; i < WorkerCount; i++)
			{
				int workerIndex = i;
				m_Workers.Add(Task.Run(() => WorkerLoop(workerIndex), CancellationToken.None));
			}

			var allWorkers = Task.WhenAll(m_Workers.ToArray());
			Completion = allWorkers.ContinueWith(t =>
			{
				_ = t.Exception;
				try { m_ResultQueue.CompleteAdding(); } catch (InvalidOperationException) { }
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		public static BackgroundAssetScheduler Start(BackgroundAssetContext context, int workerCount, int queueCapacity, CancellationToken cancellationToken)
		{
			if (context == null) throw new ArgumentNullException(nameof(context));
			return new BackgroundAssetScheduler(context, workerCount, queueCapacity, queueCapacity, cancellationToken);
		}

		public bool TryEnqueue(BackgroundAssetWorkItem item)
		{
			if (m_Disposed || InputCompleted || m_CancellationToken.IsCancellationRequested) return false;
			if (OutstandingCount >= m_MaxInFlight)
			{
				Interlocked.Increment(ref m_BackpressureEvents);
				return false;
			}
			bool added;
			try { added = item.Heavy ? m_HeavyQueue.TryAdd(item) : m_NormalQueue.TryAdd(item); }
			catch (InvalidOperationException) { return false; }
			if (!added)
			{
				if (!m_Disposed && !InputCompleted && !m_CancellationToken.IsCancellationRequested)
					Interlocked.Increment(ref m_BackpressureEvents);
				return false;
			}

			Interlocked.Increment(ref m_Scheduled);
			m_Context.MarkScheduled();
			if (m_AssetOutstanding.TryGetValue(item.Asset, out int assetOutstanding)) m_AssetOutstanding[item.Asset] = assetOutstanding + 1;
			else m_AssetOutstanding[item.Asset] = 1;
			UpdateLongHighWater(ref m_OutstandingHighWater, OutstandingCount);
			if (item.Heavy)
			{
				Interlocked.Increment(ref m_HeavyScheduled);
				UpdateHighWater(ref m_HeavyHighWater, m_HeavyQueue.Count);
			}
			else UpdateHighWater(ref m_NormalHighWater, m_NormalQueue.Count);
			return true;
		}

		public void CompleteInput()
		{
			lock (m_StateGate)
			{
				if (m_InputCompleted) return;
				m_InputCompleted = true;
				try { m_NormalQueue.CompleteAdding(); } catch (InvalidOperationException) { }
				try { m_HeavyQueue.CompleteAdding(); } catch (InvalidOperationException) { }
			}
			m_Context.MarkEnumerationComplete();
			lock (m_StateGate) Monitor.PulseAll(m_StateGate);
		}

		public bool HasOutstandingAsset(AssetId asset)
			=> m_AssetOutstanding.TryGetValue(asset, out int count) && count > 0;

		public void ReleasePreparedAssetIfIdle(AssetId asset, string assetPath)
		{
			if (!HasOutstandingAsset(asset)) m_Context.ReleaseAsset(assetPath);
		}

		public void MarkMaterialized(BackgroundAssetWorkItem work)
		{
			Interlocked.Increment(ref m_Materialized);
			if (m_AssetOutstanding.TryGetValue(work.Asset, out int count))
			{
				if (count <= 1)
				{
					m_AssetOutstanding.Remove(work.Asset);
					m_Context.ReleaseAsset(work.Path);
				}
				else m_AssetOutstanding[work.Asset] = count - 1;
			}
			else m_Context.ReleaseAsset(work.Path);
		}

		public void AdaptWorkerTarget()
		{
			if (m_Disposed || m_CancellationToken.IsCancellationRequested || WorkerCount <= 1) return;
			int current = ActiveWorkerTarget;
			int inputDepth = NormalQueueDepth + HeavyQueueDepth;
			int inputCapacity = QueueCapacity + Math.Max(16, QueueCapacity / 4);
			int resultDepth = ResultQueueDepth;
			int target = current;

			// Result pressure means the Unity/main-thread merge is the bottleneck; reduce worker
			// concurrency before creating more completed-but-unmerged work. Input pressure with a cool
			// result queue means workers are the bottleneck, so add one slot. This is deliberately slow
			// (one slot per adaptation) to avoid oscillation.
			if (resultDepth >= ResultCapacity * 0.80f && inputDepth < inputCapacity * 0.35f) target--;
			else if (inputDepth >= inputCapacity * 0.70f && resultDepth < ResultCapacity * 0.35f) target++;
			if (target != current) SetWorkerTarget(target);
		}

		public void SetWorkerTarget(int target)
		{
			target = Math.Max(1, Math.Min(WorkerCount, target));
			if (target == ActiveWorkerTarget) return;
			lock (m_StateGate)
			{
				Volatile.Write(ref m_WorkerTarget, target);
				UpdateHighWater(ref m_WorkerTargetHighWater, target);
				Monitor.PulseAll(m_StateGate);
			}
		}

		bool WaitForWorkerSlot(int workerIndex)
		{
			lock (m_StateGate)
			{
				while (!m_Disposed && !m_CancellationToken.IsCancellationRequested)
				{
					if (workerIndex < ActiveWorkerTarget) return true;
					if (InputCompleted && NormalQueueDepth == 0 && HeavyQueueDepth == 0) return false;
					Monitor.Wait(m_StateGate, 100);
				}
			}
			return false;
		}

		public bool TryTakeResult(out BackgroundAssetWorkResult result)
		{
			if (m_ResultQueue.TryTake(out result)) return true;
			return false;
		}

		public void AdvanceExpectedOrdinal(long ordinal)
			=> Interlocked.Exchange(ref m_NextResultOrdinalExpected, ordinal + 1);

		public void DrainWorkerTiming(ScanTiming target)
		{
			if (target == null) return;
			for (int i = 0; i < m_WorkerTimings.Length; i++)
			{
				var timing = m_WorkerTimings[i];
				if (timing != null) target.MergeFrom(timing);
			}
		}

		void WorkerLoop(int workerIndex)
		{
			var localTiming = new ScanTiming();
			m_WorkerTimings[workerIndex] = localTiming;
			try
			{
				while (WaitForWorkerSlot(workerIndex) && TryTakeWork(out var work))
				{
					Interlocked.Increment(ref m_ActiveWorkers);
					double start = ScanTiming.Now;
					bool stopWorker = false;
					AssetEntry entry = null;
					ScanFailure failure = default;
					bool hasFailure = false;
					try
					{
						if (m_Context.Settings != null && m_Context.Settings.enableScanPerformanceLogging)
						{
							localTiming.Activate();
							localTiming.BeginAnalyzerTracking(work.Analyzer.GetType().Name);
						}

						BackgroundRuleExecution.Enter(m_Context.Settings);
						try { entry = work.Analyzer.AnalyzeBackground(work.Path, m_Context); }
						finally { BackgroundRuleExecution.Exit(); }
					}
					catch (OperationCanceledException) when (m_CancellationToken.IsCancellationRequested) { stopWorker = true; }
					catch (Exception e)
					{
						failure = new ScanFailure(work.Analyzer.GetType().Name, work.Path, e.Message);
						hasFailure = true;
					}
					finally
					{
						double elapsed = ScanTiming.Now - start;
						if (m_Context.Settings != null && m_Context.Settings.enableScanPerformanceLogging)
						{
							localTiming.EndAnalyzerTracking(work.Analyzer.GetType().Name, elapsed);
							ScanTiming.Deactivate(localTiming);
						}
						localTiming.AddAnalyzer(work.Analyzer.GetType().Name, elapsed);

						Interlocked.Increment(ref m_Completed);
						m_Context.MarkCompleted();
						if (work.Heavy) Interlocked.Increment(ref m_HeavyCompleted);
						Interlocked.Add(ref m_WorkerBusyTicks, Math.Max(0L, (long)((elapsed / 1000.0) * Stopwatch.Frequency)));

						var result = new BackgroundAssetWorkResult(work, entry, failure, hasFailure, elapsed);
						try
						{
							m_ResultQueue.Add(result, m_CancellationToken);
							UpdateHighWater(ref m_ResultHighWater, m_ResultQueue.Count);
						}
						catch (OperationCanceledException) when (m_CancellationToken.IsCancellationRequested) { stopWorker = true; }
						catch (InvalidOperationException) { stopWorker = true; }
					}
					Interlocked.Decrement(ref m_ActiveWorkers);
					if (stopWorker) break;
				}
			}
			finally
			{
				ScanTiming.Deactivate(localTiming);
			}
		}

		bool TryTakeWork(out BackgroundAssetWorkItem work)
		{
			while (!m_CancellationToken.IsCancellationRequested)
			{
				bool preferHeavy = Interlocked.Increment(ref m_NormalBeforeHeavy) >= 8;
				if (preferHeavy && m_HeavyQueue.TryTake(out work))
				{
					Interlocked.Exchange(ref m_NormalBeforeHeavy, 0);
					return true;
				}

				try
				{
					if (m_NormalQueue.TryTake(out work, 10, m_CancellationToken)) return true;
				}
				catch (OperationCanceledException) when (m_CancellationToken.IsCancellationRequested) { break; }

				if (m_HeavyQueue.TryTake(out work))
				{
					Interlocked.Exchange(ref m_NormalBeforeHeavy, 0);
					return true;
				}

				if (InputCompleted && m_NormalQueue.Count == 0 && m_HeavyQueue.Count == 0) break;
			}

			work = default;
			return false;
		}

		public void Cancel()
		{
			CompleteInput();
			while (m_NormalQueue.TryTake(out _)) { }
			while (m_HeavyQueue.TryTake(out _)) { }
			lock (m_StateGate) Monitor.PulseAll(m_StateGate);
		}

		static void UpdateLongHighWater(ref long target, long current)
		{
			while (true)
			{
				long previous = Interlocked.Read(ref target);
				if (current <= previous || Interlocked.CompareExchange(ref target, current, previous) == previous) return;
			}
		}

		static void UpdateHighWater(ref int target, int current)
		{
			while (true)
			{
				int previous = Volatile.Read(ref target);
				if (current <= previous || Interlocked.CompareExchange(ref target, current, previous) == previous) return;
			}
		}

		public void Dispose()
		{
			CompleteInput();
			lock (m_StateGate)
			{
				if (m_Disposed) return;
				m_Disposed = true;
				Monitor.PulseAll(m_StateGate);
			}
		}
	}

	/// <summary>Temporary bounded ordering buffer between unordered worker completion and deterministic main-thread materialization.</summary>
	public sealed class BackgroundAssetStreamState
	{
		public readonly BackgroundAssetScheduler Scheduler;
		public readonly SortedDictionary<long, BackgroundAssetWorkResult> Pending = new SortedDictionary<long, BackgroundAssetWorkResult>();
		public long NextOrdinal;
		public long Materialized;
		public long Scheduled;
		public long Completed;
		public int PendingHighWater;
		public BackgroundAssetStreamState(BackgroundAssetScheduler scheduler) { Scheduler = scheduler; }
	}

	/// <summary>Runs independent project-wide background analyzers concurrently and returns results in declaration order.</summary>
	public static class BackgroundProjectScheduler
	{
		public static Task<BackgroundProjectBatchResult> Start(
			IReadOnlyList<IBackgroundProjectAnalyzer> analyzers,
			BackgroundProjectContext context,
			CancellationToken cancellationToken)
		{
			if (analyzers == null || analyzers.Count == 0)
				return Task.FromResult(new BackgroundProjectBatchResult(0));
			context?.SetWorkCount(analyzers.Count);
			return Task.Run(() => Execute(analyzers, context, cancellationToken), cancellationToken);
		}

		static BackgroundProjectBatchResult Execute(
			IReadOnlyList<IBackgroundProjectAnalyzer> analyzers,
			BackgroundProjectContext context,
			CancellationToken cancellationToken)
		{
			double wallStart = ScanTiming.Now;
			var results = new BackgroundProjectResult[analyzers.Count];
			var failures = new ConcurrentBag<ScanFailure>();
			var workerTimings = new ConcurrentBag<ScanTiming>();
			int completed = 0;
			var tasks = new Task[analyzers.Count];
			for (int i = 0; i < analyzers.Count; i++)
			{
				int index = i;
				tasks[i] = Task.Run(() =>
				{
					var timing = new ScanTiming();
					try
					{
						if (context.Settings != null && context.Settings.enableScanPerformanceLogging)
						{
							timing.Activate();
							timing.BeginAnalyzerTracking(analyzers[index].GetType().Name);
						}
						double start = ScanTiming.Now;
						results[index] = new BackgroundProjectResult(analyzers[index].GetType().Name, analyzers[index].AnalyzeBackground(context));
						double elapsed = ScanTiming.Now - start;
						if (context.Settings != null && context.Settings.enableScanPerformanceLogging)
						{
							timing.EndAnalyzerTracking(analyzers[index].GetType().Name, elapsed);
							ScanTiming.Deactivate(timing);
						}
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
					catch (Exception e)
					{
						failures.Add(new ScanFailure(analyzers[index].GetType().Name, "project pass", e.Message));
					}
					finally
					{
						workerTimings.Add(timing);
						Interlocked.Increment(ref completed);
						context?.MarkCompleted();
					}
				}, cancellationToken);
			}
			try { Task.WaitAll(tasks); }
			catch (AggregateException) when (cancellationToken.IsCancellationRequested) { }
			var merged = new ScanTiming();
			foreach (var timing in workerTimings) merged.MergeFrom(timing);
			return new BackgroundProjectBatchResult(results, new List<ScanFailure>(failures), merged, completed, ScanTiming.Now - wallStart);
		}
	}

	public sealed class BackgroundProjectResult
	{
		public string AnalyzerName { get; }
		public IReadOnlyList<AssetEntry> Entries { get; }
		public BackgroundProjectResult(string analyzerName, IReadOnlyList<AssetEntry> entries)
		{
			AnalyzerName = analyzerName ?? string.Empty;
			Entries = entries ?? Array.Empty<AssetEntry>();
		}
	}

	public sealed class BackgroundProjectBatchResult
	{
		public readonly BackgroundProjectResult[] Results;
		public readonly List<ScanFailure> Failures;
		public readonly ScanTiming Timing;
		public readonly int Completed;
		public readonly int WorkCount;
		public readonly double WallMilliseconds;
		public BackgroundProjectBatchResult(int workCount)
		{
			WorkCount = workCount;
			WallMilliseconds = 0.0;
			Results = new BackgroundProjectResult[workCount];
			Failures = new List<ScanFailure>();
			Timing = new ScanTiming();
		}
		internal BackgroundProjectBatchResult(BackgroundProjectResult[] results, List<ScanFailure> failures, ScanTiming timing, int completed, double wallMilliseconds)
		{
			Results = results;
			Failures = failures;
			Timing = timing;
			Completed = completed;
			WorkCount = results.Length;
			WallMilliseconds = wallMilliseconds;
		}
	}

}

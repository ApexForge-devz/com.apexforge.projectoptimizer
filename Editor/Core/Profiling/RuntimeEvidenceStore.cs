using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace ApexForge.ProjectOptimizer
{
	[Serializable]
	public sealed class RuntimeEvidenceMetric
	{
		public string Name;
		public string Unit;
		public int Samples;
		public double Average;
		public double Max;
		public double Last;
		public double P50;
		public double P95;
		public string Evidence;
		public string Quality;
	}

	[Serializable]
	public sealed class RuntimeEvidenceSnapshot
	{
		public int Version = 6;
		public string UnityVersion;
		public string TimestampUtc;
		public string Platform;
		public string CaptureMode;
		public string Scenario;
		public string BuildId;
		public string CommitId;
		public string ProjectFingerprint;
		public string BuildProfilePath;
		public string BuildProfileGuid;
		public string ContentIdentity;
		public string CaptureCharacteristics;
		public EvidenceState EvidenceState = EvidenceState.Unconfirmed;
		public string SourcePath;
		public bool IsPlayerBuild;
		public double WarmupSeconds;
		public int FrameSamples;
		public bool IsComplete;
		public string EvidenceQuality;
		public string CompletionReason;
		public double DurationSeconds;
		public List<RuntimeEvidenceMetric> Metrics = new List<RuntimeEvidenceMetric>();
		public string Note;
	}

	public static class RuntimeEvidenceStore
	{
		const int k_Version = 6;
		static readonly string k_Path = ProjectOptimizerConstants.RuntimeEvidencePath;
		internal static string EvidencePath => k_Path;

		public static RuntimeEvidenceSnapshot LoadLatest()
		{
			try
			{
				if (!File.Exists(k_Path)) return null;
				var snapshot = JsonUtility.FromJson<RuntimeEvidenceSnapshot>(File.ReadAllText(k_Path));
				if (snapshot == null || snapshot.Version != k_Version) return null;
				snapshot.EvidenceState = EvaluateState(snapshot, out _);
				snapshot.EvidenceQuality = snapshot.EvidenceState.ToString();
				return snapshot;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read runtime evidence: " + e.Message);
				return null;
			}
		}

		public static EvidenceState EvaluateState(RuntimeEvidenceSnapshot snapshot, out string reason)
		{
			return RuntimeEvidenceRecorder.EvaluateState(snapshot, out reason);
		}

		internal static void Save(RuntimeEvidenceSnapshot snapshot)
		{
			if (snapshot == null) return;
			try
			{
				if (!CacheFileUtility.EnsureCacheFolder()) return;
				string temp = k_Path + ".tmp";
				File.WriteAllText(temp, JsonUtility.ToJson(snapshot, true));
				CacheFileUtility.ReplaceFile(temp, k_Path);
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not save runtime evidence: " + e.Message);
			}
		}
	}

	/// <summary>
	/// Low-overhead editor-side runtime evidence capture. It intentionally complements Unity Profiler rather
	/// than replacing it: this produces a small durable baseline that Project Optimizer can compare with
	/// future scans while detailed frame inspection remains in Unity's Profiler tooling.
	/// </summary>
	[InitializeOnLoad]
	public static class RuntimeEvidenceRecorder
	{
		sealed class MetricAccumulator
		{
			public string Name;
			public string Unit;
			public ProfilerRecorder Recorder;
			public bool Valid;
			public int Samples;
			public double Sum;
			public double Max = double.MinValue;
			public double Last;
			public double Scale = 1.0;
			public string Evidence;
			public string Quality;
			public bool UseLastValue;
			public readonly List<double> Values = new List<double>(2048);
		}

		static readonly List<MetricAccumulator> s_Metrics = new List<MetricAccumulator>();
		static bool s_Recording;
		static double s_StartTime;
		static int s_FrameSamples;

		static RuntimeEvidenceRecorder()
		{
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		static void OnPlayModeStateChanged(PlayModeStateChange state)
		{
			if (state == PlayModeStateChange.EnteredPlayMode)
				Start();
			else if (state == PlayModeStateChange.ExitingPlayMode)
		{
			Stop();
			if (OptimizerSettings.Instance.captureMemorySnapshotOnPlayModeExit)
				RuntimeMemorySnapshotUtility.CaptureMemorySnapshot();
		}
		}

		public static void Start()
		{
			if (s_Recording) return;
			DisposeRecorders();
			s_FrameSamples = 0;
			s_StartTime = EditorApplication.timeSinceStartup;
			Add(ProfilerCategory.Internal, "Main Thread", "ms", "Main Thread", 1000000.0, true);
			Add(ProfilerCategory.Internal, "Render Thread", "ms", "Render Thread", 1000000.0, true);
			Add(ProfilerCategory.Memory, "GC Allocated In Frame", "bytes", "GC Allocated In Frame", 1.0, true);
			Add(ProfilerCategory.Memory, "System Used Memory", "bytes", "System Used Memory", 1.0, false);
			Add(ProfilerCategory.Memory, "Gfx Used Memory", "bytes", "Gfx Used Memory", 1.0, false);
			Add(ProfilerCategory.Render, "Draw Calls", "count", "Draw Calls Count", 1.0, true);
			Add(ProfilerCategory.Render, "SetPass Calls", "count", "SetPass Calls Count", 1.0, true);
			s_Recording = true;
			EditorApplication.update += CaptureFrame;
		}

		public static void Stop()
		{
			if (!s_Recording) return;
			s_Recording = false;
			EditorApplication.update -= CaptureFrame;
			var snapshot = new RuntimeEvidenceSnapshot
			{
				Version = 6,
				UnityVersion = Application.unityVersion,
				TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
				Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
				CaptureMode = "Editor Play Mode",
				Scenario = "Automatic Play Mode baseline",
				BuildId = string.Empty,
				CommitId = string.Empty,
				ProjectFingerprint = EvidenceIdentity.ComputeProjectFingerprint(),
				SourcePath = RuntimeEvidenceStore.EvidencePath,
				IsPlayerBuild = false,
				WarmupSeconds = 0.0,
				FrameSamples = s_FrameSamples,
				IsComplete = s_FrameSamples >= 2,
				EvidenceQuality = s_FrameSamples >= 2 ? "Measured" : "InsufficientSamples",
				CompletionReason = s_FrameSamples >= 2 ? "Capture completed with at least two samples." : "Editor Play Mode capture ended before at least two samples were collected.",
				DurationSeconds = Math.Max(0.0, EditorApplication.timeSinceStartup - s_StartTime),
				Note = "Compact counters captured automatically during Editor Play Mode. They are suitable for baselines, but Editor overhead can affect attribution; use a Player build and Unity Profiler for authoritative runtime diagnosis. Memory Profiler snapshots are separate and opt-in. Provenance includes Unity version, active target platform and a project/configuration fingerprint."
			};
			foreach (var metric in s_Metrics)
			{
				if (!metric.Valid || metric.Samples <= 0) continue;
				metric.Values.Sort();
				snapshot.Metrics.Add(new RuntimeEvidenceMetric
				{
					Name = metric.Name,
					Unit = metric.Unit,
					Samples = metric.Samples,
					Average = metric.Sum / metric.Samples,
					Max = metric.Max,
					Last = metric.Last,
					P50 = Percentile(metric.Values, 0.50),
					P95 = Percentile(metric.Values, 0.95),
					Evidence = metric.Evidence,
					Quality = metric.Quality
				});
			}
			RuntimeEvidenceStore.Save(snapshot);
			DisposeRecorders();
			s_Metrics.Clear();
		}

		public static EvidenceState EvaluateState(RuntimeEvidenceSnapshot snapshot, out string reason)
		{
			reason = null;
			if (snapshot == null) { reason = "Runtime evidence is missing."; return EvidenceState.Unconfirmed; }
			if (!snapshot.IsComplete || snapshot.FrameSamples < 2) { reason = "Runtime capture contains fewer than two complete samples."; return EvidenceState.InsufficientSamples; }
			if (!snapshot.IsPlayerBuild || string.IsNullOrEmpty(snapshot.BuildId)) { reason = "Runtime metrics are not tied to a verifiable Player build GUID."; return EvidenceState.Unconfirmed; }
			var latest = BuildReportStore.LoadLatest();
			if (latest == null || latest.summary == null) { reason = "No stored Unity Player BuildReport is available to corroborate the runtime build GUID."; return EvidenceState.Unconfirmed; }
			if (!string.Equals(latest.summary.result, "Succeeded", StringComparison.OrdinalIgnoreCase) || !string.Equals(latest.summary.evidenceQuality, "Complete", StringComparison.OrdinalIgnoreCase)) { reason = "The corroborating Player BuildReport is not a completed successful build with complete evidence."; return EvidenceState.Stale; }
			if (!string.Equals(latest.summary.guid, snapshot.BuildId, StringComparison.OrdinalIgnoreCase)) { reason = "Runtime build GUID does not match the latest stored Player BuildReport."; return EvidenceState.Mismatched; }
			if (!string.IsNullOrEmpty(snapshot.CommitId) || !string.IsNullOrEmpty(latest.summary.commitId))
			{
				if (string.IsNullOrEmpty(snapshot.CommitId) || string.IsNullOrEmpty(latest.summary.commitId)) { reason = "Source commit provenance is present on only one side of the runtime/build identity pair."; return EvidenceState.Unconfirmed; }
				if (!string.Equals(snapshot.CommitId, latest.summary.commitId, StringComparison.OrdinalIgnoreCase)) { reason = "Runtime source commit identity does not match the build report."; return EvidenceState.Mismatched; }
			}
			if (!string.IsNullOrEmpty(snapshot.UnityVersion) && !string.Equals(snapshot.UnityVersion, latest.summary.unityVersion, StringComparison.OrdinalIgnoreCase)) { reason = "Runtime Unity version does not match the build report."; return EvidenceState.Mismatched; }
			if (!string.IsNullOrEmpty(snapshot.Platform) && !string.Equals(snapshot.Platform, latest.summary.platform, StringComparison.OrdinalIgnoreCase) && !string.Equals(snapshot.Platform, "Player", StringComparison.OrdinalIgnoreCase)) { reason = "Runtime platform does not match the build report."; return EvidenceState.Mismatched; }
			if (!string.IsNullOrEmpty(snapshot.BuildProfileGuid) || !string.IsNullOrEmpty(latest.summary.buildProfileGuid))
			{
				if (string.IsNullOrEmpty(snapshot.BuildProfileGuid) || string.IsNullOrEmpty(latest.summary.buildProfileGuid)) { reason = "Build Profile provenance is present on only one side of the runtime/build identity pair."; return EvidenceState.Unconfirmed; }
				if (!string.Equals(snapshot.BuildProfileGuid, latest.summary.buildProfileGuid, StringComparison.OrdinalIgnoreCase)) { reason = "Runtime Build Profile identity does not match the build report."; return EvidenceState.Mismatched; }
			}
			if (!string.IsNullOrEmpty(snapshot.ProjectFingerprint) || !string.IsNullOrEmpty(latest.summary.projectFingerprint))
			{
				if (string.IsNullOrEmpty(snapshot.ProjectFingerprint) || string.IsNullOrEmpty(latest.summary.projectFingerprint)) { reason = "Project/content configuration provenance is present on only one side of the runtime/build identity pair."; return EvidenceState.Unconfirmed; }
				if (!string.Equals(snapshot.ProjectFingerprint, latest.summary.projectFingerprint, StringComparison.OrdinalIgnoreCase)) { reason = "Runtime project fingerprint does not match the build report."; return EvidenceState.Mismatched; }
			}
			if (!string.IsNullOrEmpty(snapshot.ContentIdentity) && !string.IsNullOrEmpty(latest.summary.contentIdentity) && !string.Equals(snapshot.ContentIdentity, latest.summary.contentIdentity, StringComparison.OrdinalIgnoreCase)) { reason = "Runtime content identity does not match the build-time content identity."; return EvidenceState.Mismatched; }
			if (string.IsNullOrEmpty(snapshot.ContentIdentity) || string.IsNullOrEmpty(latest.summary.contentIdentity)) { reason = "Build GUID is corroborated, but complete build-time content identity is unavailable."; return EvidenceState.Unconfirmed; }
			if (string.IsNullOrWhiteSpace(snapshot.CaptureCharacteristics)) { reason = "Runtime capture characteristics are missing; the evidence cannot be uniquely interpreted."; return EvidenceState.Unconfirmed; }
			reason = "Runtime samples match a successful current Player build by GUID, Unity version, target, available provenance and build-time content identity.";
			return EvidenceState.Fresh;
		}

		static string CurrentBuildProfilePath()
		{
			try
			{
				var profile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
				return profile == null ? string.Empty : (AssetDatabase.GetAssetPath(profile) ?? string.Empty);
			}
			catch { return string.Empty; }
		}

		static string CurrentBuildProfileGuid()
		{
			string path = CurrentBuildProfilePath();
			try { return string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path); } catch { return string.Empty; }
		}

		static void Add(ProfilerCategory category, string name, string unit, string recorderName, double scale, bool useLastValue)
		{
			try
			{
				var accumulator = new MetricAccumulator { Name = name, Unit = unit, Scale = scale, UseLastValue = useLastValue, Evidence = "ProfilerRecorder: " + recorderName, Quality = "Measured" };
				accumulator.Recorder = ProfilerRecorder.StartNew(category, recorderName, 32, ProfilerRecorderOptions.StartImmediately);
				accumulator.Valid = accumulator.Recorder.Valid;
				if (!accumulator.Valid) accumulator.Recorder.Dispose();
				s_Metrics.Add(accumulator);
			}
			catch
			{
				// Marker/counter names differ by Unity version and target. Unavailable metrics are omitted.
			}
		}

		static void CaptureFrame()
		{
			if (!s_Recording || !EditorApplication.isPlaying) return;
			s_FrameSamples++;
			for (int i = 0; i < s_Metrics.Count; i++)
			{
				var metric = s_Metrics[i];
				if (!metric.Valid || !metric.Recorder.Valid) continue;
				try
				{
					double value = (metric.UseLastValue ? metric.Recorder.LastValue : metric.Recorder.CurrentValue) / metric.Scale;
					metric.Samples++;
					metric.Sum += value;
					if (metric.Values.Count >= 2048) metric.Values.RemoveAt(0);
					metric.Values.Add(value);
					metric.Max = Math.Max(metric.Max, value);
					metric.Last = value;
				}
				catch
				{
					metric.Valid = false;
				}
			}
		}

		static double Percentile(List<double> sorted, double percentile)
		{
			if (sorted == null || sorted.Count == 0) return 0.0;
			double index = (sorted.Count - 1) * percentile; int lo = (int)Math.Floor(index); int hi = (int)Math.Ceiling(index);
			if (lo == hi) return sorted[lo]; double t = index - lo; return sorted[lo] + (sorted[hi] - sorted[lo]) * t;
		}

		static void DisposeRecorders()
		{
			for (int i = 0; i < s_Metrics.Count; i++)
			{
				try { if (s_Metrics[i].Valid) s_Metrics[i].Recorder.Dispose(); }
				catch { }
			}
		}
	}

	public static class RuntimeMemorySnapshotUtility
	{
		public static void CaptureMemorySnapshot()
		{
			string folder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ProjectOptimizerConstants.MemorySnapshotsFolder));
			Directory.CreateDirectory(folder);
			string finalPath = Path.Combine(folder, "memory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".snap");
			string path = finalPath + ".tmpsnap";
			try
			{
				// Memory Profiler is an optional Unity package. Resolve it at runtime so the optimizer itself
				// does not become un-loadable in projects that do not have com.unity.memoryprofiler installed.
				Type profilerType = Type.GetType("Unity.Profiling.Memory.MemoryProfiler, Unity.MemoryProfiler.Runtime", false)
					?? Type.GetType("Unity.Profiling.Memory.MemoryProfiler", false);
				if (profilerType == null)
				{
					var assemblies = AppDomain.CurrentDomain.GetAssemblies();
					for (int i = 0; i < assemblies.Length; i++)
					{
						try
						{
							profilerType = assemblies[i].GetType("Unity.Profiling.Memory.MemoryProfiler", false);
							if (profilerType != null) break;
						}
						catch { }
					}
				}
				if (profilerType == null)
				{
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Memory Profiler package is not installed. Install com.unity.memoryprofiler to enable snapshot capture.");
					return;
				}

				MethodInfo method = null;
				foreach (var candidate in profilerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
				{
					if (!string.Equals(candidate.Name, "TakeSnapshot", StringComparison.Ordinal)) continue;
					var parameters = candidate.GetParameters();
					if (parameters.Length == 3 && parameters[0].ParameterType == typeof(string)
						&& parameters[1].ParameterType == typeof(Action<string, bool>)
						&& parameters[2].ParameterType.IsEnum)
					{
						method = candidate;
						break;
					}
				}
				if (method == null)
				{
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Installed Memory Profiler package does not expose the supported TakeSnapshot API.");
					return;
				}

				var callback = new Action<string, bool>((resultPath, success) =>
				{
					try
					{
						if (!success)
						{
							Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Memory snapshot capture failed.");
							return;
						}
						string completedPath = string.IsNullOrEmpty(resultPath) ? path : resultPath;
						if (!File.Exists(completedPath))
						{
							Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Memory snapshot reported success but no snapshot file was found.");
							return;
						}
						if (File.Exists(finalPath)) File.Delete(finalPath);
						File.Move(completedPath, finalPath);
						Debug.Log(ProjectOptimizerConstants.LogPrefix + " Memory snapshot written to " + finalPath);
					}
					catch (Exception e)
					{
						Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Memory snapshot completed but could not finalize the file: " + e.Message);
					}
				});
				Type flagsType = method.GetParameters()[2].ParameterType;
				object flags = Enum.Parse(flagsType, "ManagedObjects, NativeObjects, NativeAllocations");
				method.Invoke(null, new object[] { path, callback, flags });
			}
			catch (TargetInvocationException e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Memory snapshot capture is unavailable: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Memory snapshot capture is unavailable: " + e.Message);
			}
		}
	}

}
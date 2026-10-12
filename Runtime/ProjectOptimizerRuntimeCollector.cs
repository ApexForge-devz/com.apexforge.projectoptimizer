using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using Unity.Profiling;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Runtime
{
    [DisallowMultipleComponent]
    public sealed class ProjectOptimizerRuntimeCollector : MonoBehaviour
    {
        [SerializeField] int sampleCapacity = 1800;
        [SerializeField] bool captureOnStart = true;
        [SerializeField] bool persistAcrossScenes = true;
        [SerializeField] string outputFile = "projectoptimizer-runtime.json";
        [SerializeField] float sampleInterval = 0.25f;
        [SerializeField] string scenario = "Runtime baseline";
        [SerializeField] float warmupSeconds;
        readonly List<RuntimeMetricSample> samples = new List<RuntimeMetricSample>();
        readonly FrameTiming[] frameTimings = new FrameTiming[1];
        ProfilerRecorder mainThread, renderThread, cpuTotalFrame, gcAlloc, totalMemory, textureMemory, batches, vertices, visibleSkinnedMeshes;
        float timer;
        float captureStartedAt;
        bool capturing;

        void Awake()
        {
            if (persistAcrossScenes) DontDestroyOnLoad(gameObject);
            if (captureOnStart) StartCapture();
        }
        void Update()
        {
            if (!capturing) return;
            timer += Time.unscaledDeltaTime;
            if (timer < sampleInterval) return;
            timer = 0f;
            CaptureSample();
        }
        void OnDestroy() { StopCapture(); }
        void OnApplicationQuit() { StopCapture(); }

        [ContextMenu("Start Capture")]
        public void StartCapture()
        {
            if (capturing) return;
            samples.Clear();
            timer = 0f;
            captureStartedAt = Time.realtimeSinceStartup;
            sampleCapacity = Mathf.Max(60, sampleCapacity);
            sampleInterval = Mathf.Max(0.05f, sampleInterval);
            mainThread = TryCreate(ProfilerCategory.Internal, "Main Thread", false);
            renderThread = TryCreate(ProfilerCategory.Internal, "Render Thread", false);
            cpuTotalFrame = TryCreate(ProfilerCategory.Render, "CPU Total Frame Time", false);
            gcAlloc = TryCreate(ProfilerCategory.Memory, "GC Allocated In Frame", true);
            totalMemory = TryCreate(ProfilerCategory.Memory, "Total Used Memory", false);
            textureMemory = TryCreate(ProfilerCategory.Memory, "Texture Memory", false);
            batches = TryCreate(ProfilerCategory.Render, "Batches Count", false);
            vertices = TryCreate(ProfilerCategory.Render, "Vertices Count", false);
            visibleSkinnedMeshes = TryCreate(ProfilerCategory.Render, "Visible Skinned Meshes Count", false);
            capturing = true;
        }

        [ContextMenu("Stop Capture and Write Report")]
        public void StopCapture()
        {
            if (!capturing) return;
            CaptureSample();
            capturing = false;
            DisposeRecorder(ref mainThread); DisposeRecorder(ref renderThread); DisposeRecorder(ref cpuTotalFrame);
            DisposeRecorder(ref gcAlloc); DisposeRecorder(ref totalMemory); DisposeRecorder(ref textureMemory);
            DisposeRecorder(ref batches); DisposeRecorder(ref vertices); DisposeRecorder(ref visibleSkinnedMeshes);
            WriteReport();
        }

        void CaptureSample()
        {
            float warmup = Mathf.Max(0f, warmupSeconds);
            if (warmup > 0f && Time.realtimeSinceStartup - captureStartedAt < warmup) return;
            bool frameTimingEnabled = false;
            try { frameTimingEnabled = FrameTimingManager.IsFeatureEnabled(); } catch { }
            if (frameTimingEnabled) { try { FrameTimingManager.CaptureFrameTimings(); } catch { } }
            uint timingCount = 0;
            if (frameTimingEnabled) { try { timingCount = FrameTimingManager.GetLatestTimings(1, frameTimings); } catch { } }
            bool frameTimingValid = timingCount > 0;
            float cpuTotal = frameTimingValid ? (float)frameTimings[0].cpuFrameTime : ValueMs(cpuTotalFrame);
            bool cpuTotalValid = frameTimingValid || cpuTotalFrame.Valid;
            string cpuTotalEvidence = frameTimingValid ? "FrameTimingManager: CPU Frame Time" : cpuTotalFrame.Valid ? "ProfilerRecorder: CPU Total Frame Time" : "Unavailable";
            float mainMs = frameTimingValid ? (float)frameTimings[0].cpuMainThreadFrameTime : ValueMs(mainThread);
            bool mainValid = frameTimingValid || mainThread.Valid;
            string mainEvidence = frameTimingValid ? "FrameTimingManager" : mainThread.Valid ? "ProfilerRecorder" : "Unavailable";
            float renderMs = frameTimingValid ? (float)frameTimings[0].cpuRenderThreadFrameTime : ValueMs(renderThread);
            bool renderValid = frameTimingValid || renderThread.Valid;
            string renderEvidence = frameTimingValid ? "FrameTimingManager" : renderThread.Valid ? "ProfilerRecorder" : "Unavailable";
            float gpuMs = frameTimingValid ? (float)frameTimings[0].gpuFrameTime : 0f;
            bool gpuValid = frameTimingValid;
            string gpuEvidence = frameTimingValid ? "FrameTimingManager" : "Unavailable";
            if (samples.Count >= sampleCapacity) samples.RemoveAt(0);
            samples.Add(new RuntimeMetricSample
            {
                timestamp = Time.realtimeSinceStartupAsDouble,
                frameTimeMs = cpuTotal,
                cpuTotalFrameMs = cpuTotal,
                mainThreadMs = mainMs,
                renderThreadMs = renderMs,
                gpuMs = gpuMs,
                gcBytes = Value(gcAlloc),
                totalUsedMemoryBytes = Value(totalMemory),
                textureBytes = Value(textureMemory),
                batches = Value(batches),
                vertices = Value(vertices),
                visibleSkinnedMeshes = Value(visibleSkinnedMeshes),
                frameTimeValid = cpuTotalValid,
                cpuTotalFrameValid = cpuTotalValid,
                mainThreadValid = mainValid,
                renderThreadValid = renderValid,
                gpuValid = gpuValid,
                gcValid = gcAlloc.Valid,
                totalMemoryValid = totalMemory.Valid,
                textureValid = textureMemory.Valid,
                batchesValid = batches.Valid,
                verticesValid = vertices.Valid,
                visibleSkinnedMeshesValid = visibleSkinnedMeshes.Valid,
                frameTimeEvidence = cpuTotalEvidence,
                cpuTotalEvidence = cpuTotalEvidence,
                frameTimeQuality = frameTimingValid ? "Measured" : cpuTotalFrame.Valid ? "ProfilerCounter" : "Unavailable",
                cpuTotalQuality = frameTimingValid ? "Measured" : cpuTotalFrame.Valid ? "ProfilerCounter" : "Unavailable",
                mainThreadQuality = mainValid ? (frameTimingValid ? "Measured" : "ProfilerCounter") : "Unavailable",
                renderThreadQuality = renderValid ? (frameTimingValid ? "Measured" : "ProfilerCounter") : "Unavailable",
                gpuQuality = gpuValid ? "Measured" : "Unavailable",
                mainThreadEvidence = mainEvidence,
                renderThreadEvidence = renderEvidence,
                gpuEvidence = gpuEvidence,
                metricEvidence = "PerMetric",
                metricEvidenceGc = gcAlloc.Valid ? "ProfilerRecorder" : "Unavailable",
                metricEvidenceMemory = totalMemory.Valid ? "ProfilerRecorder" : "Unavailable",
                metricEvidenceTexture = textureMemory.Valid ? "ProfilerRecorder" : "Unavailable",
                metricEvidenceBatches = batches.Valid ? "ProfilerRecorder" : "Unavailable",
                metricEvidenceVertices = vertices.Valid ? "ProfilerRecorder" : "Unavailable",
                metricEvidenceVisibleSkinned = visibleSkinnedMeshes.Valid ? "ProfilerRecorder" : "Unavailable"
            });
        }

        void WriteReport()
        {
            try
            {
                string name = string.IsNullOrWhiteSpace(outputFile) ? "projectoptimizer-runtime.json" : Path.GetFileName(outputFile.Trim());
                if (string.IsNullOrEmpty(name)) name = "projectoptimizer-runtime.json";
                string path = Path.Combine(Application.persistentDataPath, name);
                var report = new RuntimeCaptureReport
                {
                    version = 6,
                    generatedAtUtc = DateTime.UtcNow.ToString("O"),
                    unityVersion = Application.unityVersion,
                    platform = RuntimePlatformBuildTargetName(),
                    scenario = string.IsNullOrWhiteSpace(scenario) ? "Runtime baseline" : scenario.Trim(),
                    buildId = SafeBuildId(),
                    commitId = ReadCommitId(),
                    captureMode = "Runtime Collector / FrameTimingManager + ProfilerRecorder",
                    isPlayerBuild = !Application.isEditor,
                    projectFingerprint = string.Empty,
                    buildProfilePath = string.Empty,
                    buildProfileGuid = string.Empty,
                    contentIdentity = string.Empty,
                    captureCharacteristics = "FrameTimingManager + ProfilerRecorder; up to " + sampleCapacity.ToString(CultureInfo.InvariantCulture) + " retained samples",
                    warmupSeconds = Mathf.Max(0f, warmupSeconds),
                    warmupCompleted = warmupSeconds <= 0f || Time.realtimeSinceStartup - captureStartedAt >= warmupSeconds,
                    captureCompleted = samples.Count >= 2,
                    completionReason = samples.Count >= 2 && (warmupSeconds <= 0f || Time.realtimeSinceStartup - captureStartedAt >= warmupSeconds)
                        ? "Capture completed with sufficient samples."
                        : "Capture stopped before warm-up completed or before at least two usable samples were collected.",
                    samples = new List<RuntimeMetricSample>(samples)
                };
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
                Debug.Log("[Project Optimizer] Runtime report written to: " + path);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        static string RuntimePlatformBuildTargetName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer: return "StandaloneWindows64";
                case RuntimePlatform.LinuxPlayer: return "StandaloneLinux64";
                case RuntimePlatform.OSXPlayer: return "StandaloneOSX";
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.IPhonePlayer: return "iOS";
                case RuntimePlatform.WebGLPlayer: return "WebGL";
                case RuntimePlatform.WSAPlayerX86:
                case RuntimePlatform.WSAPlayerX64:
                case RuntimePlatform.WSAPlayerARM: return "WSAPlayer";
                default: return Application.platform.ToString();
            }
        }

        static string SafeBuildId()
        {
            try { return Application.buildGUID ?? string.Empty; } catch { return string.Empty; }
        }

        static string ReadCommitId()
        {
            string[] keys = { "GIT_COMMIT", "GITHUB_SHA", "CI_COMMIT_SHA", "BUILD_SOURCEVERSION" };
            for (int i = 0; i < keys.Length; i++)
            {
                try
                {
                    string value = Environment.GetEnvironmentVariable(keys[i]);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch { }
            }
            return string.Empty;
        }

        static ProfilerRecorder TryCreate(ProfilerCategory category, string name, bool sumAllSamplesInFrame)
        {
            try
            {
                var options = ProfilerRecorderOptions.WrapAroundWhenCapacityReached;
                if (sumAllSamplesInFrame) options |= ProfilerRecorderOptions.SumAllSamplesInFrame;
                return ProfilerRecorder.StartNew(category, name, 32, options);
            }
            catch { return default(ProfilerRecorder); }
        }
        static long Value(ProfilerRecorder r) { try { return r.Valid ? r.LastValue : 0L; } catch { return 0L; } }
        static float ValueMs(ProfilerRecorder r) { try { return r.Valid ? r.LastValue / 1000000f : 0f; } catch { return 0f; } }
        static void DisposeRecorder(ref ProfilerRecorder r) { try { if (r.Valid) r.Dispose(); } catch { } r = default(ProfilerRecorder); }
    }
}

using System;
using System.Collections.Generic;

namespace ApexForge.ProjectOptimizer.Runtime
{
    [Serializable]
    public sealed class RuntimeCaptureReport
    {
        public int version = 6;
        public string generatedAtUtc;
        public string unityVersion;
        public string platform;
        public string scenario;
        public string buildId;
        public string commitId;
        public string captureMode;
        public bool isPlayerBuild;
        public string projectFingerprint;
        public string buildProfilePath;
        public string buildProfileGuid;
        public string contentIdentity;
        public string captureCharacteristics;
        public double warmupSeconds;
        public bool warmupCompleted;
        public bool captureCompleted;
        public string completionReason;
        public List<RuntimeMetricSample> samples = new List<RuntimeMetricSample>();
    }

    [Serializable]
    public sealed class RuntimeMetricSample
    {
        public double timestamp;
        public float frameTimeMs;
        public float cpuTotalFrameMs;
        public float mainThreadMs;
        public float renderThreadMs;
        public float gpuMs;
        public long gcBytes;
        public long totalUsedMemoryBytes;
        public long textureBytes;
        public long batches;
        public long vertices;
        public long visibleSkinnedMeshes;
        public bool frameTimeValid,cpuTotalFrameValid,mainThreadValid,renderThreadValid,gpuValid,gcValid,totalMemoryValid,textureValid,batchesValid,verticesValid,visibleSkinnedMeshesValid;
        public string frameTimeEvidence,cpuTotalEvidence,mainThreadEvidence,renderThreadEvidence,gpuEvidence,metricEvidence;
        public string frameTimeQuality,cpuTotalQuality,mainThreadQuality,renderThreadQuality,gpuQuality;
        public string metricEvidenceGc,metricEvidenceMemory,metricEvidenceTexture,metricEvidenceBatches,metricEvidenceVertices,metricEvidenceVisibleSkinned;
    }
}

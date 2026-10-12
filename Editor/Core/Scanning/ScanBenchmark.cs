using System;
using UnityEngine;
namespace ApexForge.ProjectOptimizer
{
    /// <summary>Controls the amount of per-scan diagnostic work performed by the scanner.</summary>
    public enum ScanProfilingMode
    {
        Production = 0,
        AggregateProfiling = 1,
        DeepProfiling = 2,
        Validation = 3
    }

    public static class ScanBenchmark
    {
        public static ScanProfilingMode EffectiveMode(OptimizerSettings settings)
        {
            if (settings == null || !settings.enableScanPerformanceLogging)
                return ScanProfilingMode.Production;
            return settings.scanProfilingMode;
        }

        public static bool HasAggregate(OptimizerSettings settings)
            => EffectiveMode(settings) >= ScanProfilingMode.AggregateProfiling;

        public static bool HasDeep(OptimizerSettings settings)
            => EffectiveMode(settings) == ScanProfilingMode.DeepProfiling || EffectiveMode(settings) == ScanProfilingMode.Validation;

        public static bool IsValidation(OptimizerSettings settings)
            => EffectiveMode(settings) == ScanProfilingMode.Validation;

        /// <summary>Stable descriptor used to compare runs with the same scanner configuration.</summary>
        public static string BuildIdentifier(OptimizerSettings settings, int assetCount, string analyzerSignature)
        {
            string project = ProjectIdentity();
            string scope = settings != null ? settings.scopeMode.ToString() : "?";
            string platform = settings != null ? settings.targetPlatform.ToString() : "?";
            string toggles = settings == null
                ? "?"
                : (settings.scanPrefabs ? "P1" : "P0")
                  + (settings.scanScenes ? "S1" : "S0")
                  + (settings.scanDuplicates ? "D1" : "D0")
                  + (settings.scanUnused ? "U1" : "U0")
                  + (settings.deepTextureScan ? "T1" : "T0")
                  + (settings.projectProofPass ? "R1" : "R0");
            return "POB1|tool=" + OptimizerVersion.Schema
                   + "|unity=" + (Application.unityVersion ?? "?")
                   + "|project=" + project
                   + "|scope=" + scope
                   + "|platform=" + platform
                   + "|toggles=" + toggles
                   + "|assets=" + Math.Max(0, assetCount)
                   + "|profiling=" + EffectiveMode(settings)
                   + "|analyzers=" + (analyzerSignature ?? "?");
        }

        static string ProjectIdentity()
        {
            unchecked
            {
                string path = Application.dataPath ?? string.Empty;
                ulong acc = 1469598103934665603UL;
                for (int i = 0; i < path.Length; i++)
                {
                    acc ^= char.ToLowerInvariant(path[i]);
                    acc *= 1099511628211UL;
                }
                return acc.ToString("x16");
            }
        }
    }
}

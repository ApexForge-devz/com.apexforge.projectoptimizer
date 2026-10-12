using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    [Serializable]
    public sealed class AddressablesAnalyzeRuleEvidence
    {
        public string TypeName;
        public string DisplayName;
        public bool Available;
        public bool Attempted;
        public bool Succeeded;
        public int ResultCount;
        public long ElapsedMilliseconds;
        public string Error;
    }

    [Serializable]
    public sealed class AddressablesAnalyzeEvidenceSnapshot
    {
        public int Version = 3;
        public string TimestampUtc;
        public string UnityVersion;
        public string ProjectFingerprint;
        public string ContentIdentity;
        public string Platform;
        public int ResultCount;
        public int WarningCount;
        public int ErrorCount;
        public int InfoCount;
        public int ExpectedRuleCount;
        public string RuleScope;
        public int AttemptedRuleCount;
        public int SuccessfulRuleCount;
        public int FailedRuleCount;
        public int UnavailableRuleCount;
        public bool IsComplete;
        public List<AddressablesAnalyzeRuleEvidence> Rules = new List<AddressablesAnalyzeRuleEvidence>();
        public List<string> ResultNames = new List<string>();
        public string FreshnessReason;
        public EvidenceState EvidenceState = EvidenceState.Unconfirmed;

        public bool IsFresh => EvidenceState == EvidenceState.Fresh;
    }

    /// <summary>Runs the read-only Addressables Analyze rule pass through reflection so Addressables remains optional.</summary>
    public static class AddressablesAnalyzeEvidenceStore
    {
        const int k_Version = 3;
        static readonly string k_Path = ProjectOptimizerConstants.AddressablesAnalyzePath;
        static readonly string[] k_RuleTypes =
        {
            "UnityEditor.AddressableAssets.Build.AnalyzeRules.CheckBundleDupeDependencies",
            "UnityEditor.AddressableAssets.Build.AnalyzeRules.CheckResourcesDupeDependencies",
            "UnityEditor.AddressableAssets.Build.AnalyzeRules.CheckSceneDupeDependencies"
        };

        public static AddressablesAnalyzeEvidenceSnapshot LoadLatest()
        {
            try
            {
                if (!File.Exists(k_Path)) return null;
                var value = JsonUtility.FromJson<AddressablesAnalyzeEvidenceSnapshot>(File.ReadAllText(k_Path));
                if (value == null || value.Version != k_Version) return null;
                value.EvidenceState = EvaluateFreshness(value, out var reason);
                value.FreshnessReason = reason;
                return value;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read Addressables Analyze evidence: " + e.Message);
                return null;
            }
        }

        public static void CaptureLatest()
        {
            AddressablesAnalyzeEvidenceSnapshot snapshot = null;
            try
            {
                if (!AddressablesUtility.IsInstalled)
                {
                    Feedback.Warn("Addressables is not installed in this project.");
                    return;
                }

                Type settingsType = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
                object settings = settingsType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (settings == null)
                {
                    Feedback.Warn("Addressables settings are not available yet. Wait for editor startup/imports to finish and retry.");
                    return;
                }

                snapshot = new AddressablesAnalyzeEvidenceSnapshot
                {
                    TimestampUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    UnityVersion = Application.unityVersion,
                    ProjectFingerprint = EvidenceIdentity.ComputeProjectFingerprint(),
                    ContentIdentity = ComputeContentIdentity(settings),
                    Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    ExpectedRuleCount = k_RuleTypes.Length,
                    RuleScope = string.Join("; ", k_RuleTypes)
                };

                for (int i = 0; i < k_RuleTypes.Length; i++) RunRule(k_RuleTypes[i], settings, snapshot);
                FinaliseSnapshot(snapshot);
                Save(snapshot);
                Feedback.Success("Addressables Analyze evidence captured: " + snapshot.ResultCount.ToString("N0") + " result(s) across " + snapshot.AttemptedRuleCount + "/" + snapshot.ExpectedRuleCount + " attempted rule(s).");
            }
            catch (Exception e)
            {
                if (snapshot != null)
                {
                    snapshot.IsComplete = false;
                    snapshot.EvidenceState = EvidenceState.Stale;
                    snapshot.FreshnessReason = "Addressables Analyze capture failed: " + e.Message;
                    Save(snapshot);
                }
                UnityEngine.Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Addressables Analyze evidence failed: " + e.Message);
                Feedback.Warn("Addressables Analyze evidence could not be captured: " + e.Message);
            }
        }

        static void FinaliseSnapshot(AddressablesAnalyzeEvidenceSnapshot snapshot)
        {
            snapshot.IsComplete = snapshot.AttemptedRuleCount == snapshot.ExpectedRuleCount
                && snapshot.SuccessfulRuleCount == snapshot.ExpectedRuleCount
                && snapshot.FailedRuleCount == 0
                && snapshot.UnavailableRuleCount == 0;
            snapshot.EvidenceState = snapshot.IsComplete ? EvidenceState.Fresh : EvidenceState.Stale;
            snapshot.FreshnessReason = snapshot.IsComplete
                ? "Captured the complete configured Addressables Analyze rule set for the recorded content identity."
                : "Addressables Analyze capture is incomplete: " + snapshot.SuccessfulRuleCount + "/" + snapshot.ExpectedRuleCount + " rules completed successfully; partial evidence is not Fresh.";
        }

        static void Save(AddressablesAnalyzeEvidenceSnapshot snapshot)
        {
            try
            {
                CacheFileUtility.EnsureCacheFolder();
                string temp = k_Path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(snapshot, true));
                CacheFileUtility.ReplaceFile(temp, k_Path);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not persist Addressables Analyze evidence: " + e.Message);
            }
        }

        static void RunRule(string ruleTypeName, object settings, AddressablesAnalyzeEvidenceSnapshot snapshot)
        {
            var evidence = new AddressablesAnalyzeRuleEvidence { TypeName = ruleTypeName, DisplayName = ruleTypeName };
            snapshot.Rules.Add(evidence);
            Type type = FindType(ruleTypeName);
            if (type == null)
            {
                evidence.Error = "The configured Analyze rule type is unavailable in the installed Addressables package.";
                snapshot.UnavailableRuleCount++;
                return;
            }

            evidence.Available = true;
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                object rule = Activator.CreateInstance(type);
                evidence.DisplayName = ReadString(rule, "ruleName");
                var method = type.GetMethod("RefreshAnalysis", BindingFlags.Public | BindingFlags.Instance);
                if (method == null)
                {
                    evidence.Error = "RefreshAnalysis(AddressableAssetSettings) is unavailable on this rule type.";
                    snapshot.FailedRuleCount++;
                    return;
                }

                evidence.Attempted = true;
                snapshot.AttemptedRuleCount++;
                object raw = method.Invoke(rule, new[] { settings });
                if (!(raw is IEnumerable results))
                {
                    evidence.Error = "RefreshAnalysis did not return an enumerable AnalyzeResult collection.";
                    snapshot.FailedRuleCount++;
                    return;
                }

                evidence.Succeeded = true;
                foreach (object result in results)
                {
                    if (result == null) continue;
                    evidence.ResultCount++;
                    snapshot.ResultCount++;
                    string name = ReadString(result, "resultName");
                    if (!string.IsNullOrEmpty(name) && snapshot.ResultNames.Count < 100) snapshot.ResultNames.Add(name);
                    string severity = ReadMember(result, "severity")?.ToString() ?? string.Empty;
                    if (severity.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) snapshot.ErrorCount++;
                    else if (severity.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) snapshot.WarningCount++;
                    else snapshot.InfoCount++;
                }
                snapshot.SuccessfulRuleCount++;
            }
            catch (Exception e)
            {
                evidence.Error = e is TargetInvocationException && e.InnerException != null ? e.InnerException.Message : e.Message;
                if (evidence.Attempted) snapshot.FailedRuleCount++;
                else snapshot.UnavailableRuleCount++;
            }
            finally
            {
                timer.Stop();
                evidence.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            }
        }

        static EvidenceState EvaluateFreshness(AddressablesAnalyzeEvidenceSnapshot value, out string reason)
        {
            reason = null;
            if (value == null) { reason = "Addressables Analyze evidence is missing."; return EvidenceState.Unconfirmed; }
            if (!value.IsComplete || value.SuccessfulRuleCount != value.ExpectedRuleCount || value.FailedRuleCount != 0 || value.UnavailableRuleCount != 0)
            { reason = "Addressables Analyze evidence is partial or failed; all required rules must complete before use as Fresh evidence."; return EvidenceState.Stale; }
            if (!string.Equals(value.UnityVersion, Application.unityVersion, StringComparison.OrdinalIgnoreCase))
            { reason = "Addressables Analyze evidence was captured by a different Unity version."; return EvidenceState.Mismatched; }
            string platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!string.Equals(value.Platform, platform, StringComparison.OrdinalIgnoreCase))
            { reason = "Addressables Analyze evidence targets " + value.Platform + " while the current target is " + platform + "."; return EvidenceState.Mismatched; }
            string currentProject = EvidenceIdentity.ComputeProjectFingerprint();
            if (string.IsNullOrEmpty(value.ProjectFingerprint) || string.IsNullOrEmpty(currentProject))
            { reason = "Project identity could not be confirmed."; return EvidenceState.Unconfirmed; }
            if (!string.Equals(value.ProjectFingerprint, currentProject, StringComparison.OrdinalIgnoreCase))
            { reason = "Addressables configuration identity changed since analysis."; return EvidenceState.Mismatched; }
            string currentContent = ComputeContentIdentityFromCurrentProject();
            if (string.IsNullOrEmpty(value.ContentIdentity) || string.IsNullOrEmpty(currentContent))
            { reason = "Addressable content/dependency identity could not be recomputed."; return EvidenceState.Unconfirmed; }
            if (!string.Equals(value.ContentIdentity, currentContent, StringComparison.OrdinalIgnoreCase))
            { reason = "Addressable content or dependency identity changed since Analyze execution."; return EvidenceState.Mismatched; }
            if (!DateTimeOffset.TryParse(value.TimestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
            { reason = "Addressables Analyze evidence timestamp is invalid."; return EvidenceState.Unconfirmed; }
            if (DateTimeOffset.UtcNow - timestamp.ToUniversalTime() > TimeSpan.FromDays(7))
            { reason = "Addressables Analyze evidence exceeds the seven-day freshness window."; return EvidenceState.Stale; }
            reason = "Addressables Analyze completed for the current Unity target, configuration and content/dependency identity.";
            return EvidenceState.Fresh;
        }

        static string ComputeContentIdentity(object settings)
        {
            var tokens = new List<string>();
            if (settings is UnityEngine.Object settingsObject)
            {
                string settingsPath = AssetDatabase.GetAssetPath(settingsObject);
                if (!string.IsNullOrEmpty(settingsPath)) tokens.Add("settings:" + settingsPath + "|" + DependencyHash(settingsPath));
            }
            foreach (object group in Enumerate(ReadMember(settings, "groups")))
            {
                if (group is UnityEngine.Object groupObject)
                {
                    string groupPath = AssetDatabase.GetAssetPath(groupObject);
                    if (!string.IsNullOrEmpty(groupPath)) tokens.Add("group:" + groupPath + "|" + DependencyHash(groupPath));
                }
                foreach (object entry in Enumerate(ReadMember(group, "entries")))
                {
                    string assetPath = ReadString(entry, "AssetPath", "assetPath");
                    if (string.IsNullOrEmpty(assetPath)) continue;
                    string[] dependencies;
                    try { dependencies = AssetDatabase.GetDependencies(assetPath, true); }
                    catch { dependencies = Array.Empty<string>(); }
                    tokens.Add("asset:" + assetPath + "|" + DependencyHash(assetPath));
                    for (int i = 0; i < dependencies.Length; i++)
                    {
                        string dependency = dependencies[i];
                        if (!string.IsNullOrEmpty(dependency)) tokens.Add("dep:" + dependency + "|" + DependencyHash(dependency));
                    }
                }
            }
            return HashTokens(tokens);
        }

        static string ComputeContentIdentityFromCurrentProject()
        {
            try
            {
                Type settingsType = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
                object settings = settingsType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                return settings == null ? string.Empty : ComputeContentIdentity(settings);
            }
            catch { return string.Empty; }
        }

        static string DependencyHash(string assetPath)
        {
            try
            {
                MethodInfo method = typeof(AssetDatabase).GetMethod("GetAssetDependencyHash", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method != null)
                {
                    object value = method.Invoke(null, new object[] { assetPath });
                    string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch { }
            return EvidenceContract.ComputeContentIdentity(new[] { assetPath });
        }

        static string HashTokens(List<string> tokens)
        {
            tokens.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder(tokens.Count * 80);
                for (int i = 0; i < tokens.Count; i++) text.Append(tokens[i]).Append('\n');
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        static IEnumerable Enumerate(object value)
        {
            if (value is IEnumerable enumerable) return enumerable;
            return Array.Empty<object>();
        }

        static string ReadString(object instance, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                object value = ReadMember(instance, names[i]);
                if (value != null) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            return string.Empty;
        }

        static object ReadMember(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name)) return null;
            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(instance, null);
            return type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var type = assembly.GetType(fullName, false); if (type != null) return type; } catch { }
            }
            return null;
        }
    }
}

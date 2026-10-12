using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Marks an external analyzer so Project Optimizer can discover it without modifying the scanner.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ProjectOptimizerAnalyzerAttribute : Attribute { }

    /// <summary>Discovers optional third-party analyzers through Unity's TypeCache.</summary>
    public static class AnalyzerRegistry
    {
        static List<Type> s_AssetTypes;
        static List<Type> s_ProjectTypes;
        static string s_Signature;

        public static IReadOnlyList<Type> AssetAnalyzerTypes { get { Ensure(); return s_AssetTypes; } }
        public static IReadOnlyList<Type> ProjectAnalyzerTypes { get { Ensure(); return s_ProjectTypes; } }
        public static string Signature { get { Ensure(); return s_Signature; } }

        public static void Rebuild()
        {
            s_AssetTypes = null;
            s_ProjectTypes = null;
            s_Signature = null;
        }

        static void Ensure()
        {
            if (s_AssetTypes != null) return;
            s_AssetTypes = new List<Type>();
            s_ProjectTypes = new List<Type>();
            try
            {
                foreach (var type in TypeCache.GetTypesWithAttribute<ProjectOptimizerAnalyzerAttribute>())
                {
                    if (type == null || type.IsAbstract || type.IsGenericType || type.GetConstructor(Type.EmptyTypes) == null) continue;
                    bool asset = typeof(IAssetAnalyzer).IsAssignableFrom(type);
                    bool project = typeof(IProjectAnalyzer).IsAssignableFrom(type);
                    if (asset) s_AssetTypes.Add(type);
                    if (project) s_ProjectTypes.Add(type);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Analyzer discovery failed: " + e.Message);
            }
            s_AssetTypes.Sort((a,b) => string.CompareOrdinal(a.FullName, b.FullName));
            s_ProjectTypes.Sort((a,b) => string.CompareOrdinal(a.FullName, b.FullName));
            string raw = string.Join(";", s_AssetTypes.Concat(s_ProjectTypes).Select(t => t.AssemblyQualifiedName ?? t.FullName));
            using (var md5 = MD5.Create()) s_Signature = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", string.Empty);
        }

        public static IEnumerable<IAssetAnalyzer> CreateAssetAnalyzers()
        {
            foreach (var type in AssetAnalyzerTypes)
            {
                object value = null;
                try { value = Activator.CreateInstance(type); } catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not create analyzer " + type.FullName + ": " + e.Message); }
                if (value is IAssetAnalyzer analyzer) yield return analyzer;
            }
        }

        public static IEnumerable<IProjectAnalyzer> CreateProjectAnalyzers()
        {
            foreach (var type in ProjectAnalyzerTypes)
            {
                object value = null;
                try { value = Activator.CreateInstance(type); } catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not create analyzer " + type.FullName + ": " + e.Message); }
                if (value is IProjectAnalyzer analyzer) yield return analyzer;
            }
        }
    }
}

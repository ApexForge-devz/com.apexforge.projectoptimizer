using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	[ProjectOptimizerAnalyzer]
	public sealed class AssemblyDefinitionAnalyzer : IBackgroundProjectAnalyzer
	{
		const int k_BroadReferenceWarning = 12;
		static AssemblyDefinitionAnalyzer()
		{
			Rules.Register(id:"ASM001", title:"Assembly has broad compilation coupling", category:AssetCategory.ProjectSetting, why:"A large direct dependency surface increases the set of code that can be affected by a change and can lengthen editor recompilation cycles.", fix:"Split responsibilities and keep low-level assemblies independent of higher-level systems where practical.", severity:IssueSeverity.Info, fixKind:FixKind.None, confidence:OptimizationConfidence.HighConfidence, risk:FixRisk.Unspecified, backupRequired:false);
			Rules.Register(id:"ASM002", title:"Assembly dependency cycle", category:AssetCategory.ProjectSetting, why:"Cycles in assembly references create unnecessary architectural coupling and can make compilation boundaries harder to maintain.", fix:"Break the cycle by moving shared contracts to a lower-level assembly.", severity:IssueSeverity.Warning, fixKind:FixKind.None, confidence:OptimizationConfidence.Proven, risk:FixRisk.Unspecified, backupRequired:false);
		}

		public string DisplayName => "Assembly dependency analysis";
		public bool Enabled(OptimizerSettings s) => s == null || s.scanProjectSettings;
		public float Progress { get; private set; }

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			var nodes = ReadAllNodes(Application.dataPath, resolveGuids:true);
			var map = nodes.ToDictionary(x => x.name, StringComparer.OrdinalIgnoreCase);
			var byGuid = nodes.Where(x => !string.IsNullOrEmpty(x.guid)).ToDictionary(x => x.guid, StringComparer.OrdinalIgnoreCase);
			var reverse = BuildReverse(map, byGuid);
			int total = nodes.Count;
			for (int i = 0; i < total; i++)
			{
				var node = nodes[i];
				var e = BuildEntry(node, map, byGuid, reverse, node.guid, FileUtility.FileSizeOf(node.assetPath));
				if (e != null) yield return e;
				Progress = total == 0 ? 1f : (i + 1f) / total;
				if ((i & 15) == 0) yield return null;
			}
			Progress = 1f;
		}

		public IReadOnlyList<AssetEntry> AnalyzeBackground(BackgroundProjectContext context)
		{
			var nodes = ReadAllNodes(context.ProjectRoot, resolveGuids:false);
			var map = nodes.ToDictionary(x => x.name, StringComparer.OrdinalIgnoreCase);
			var byGuid = nodes.Where(x => !string.IsNullOrEmpty(x.guid)).ToDictionary(x => x.guid, StringComparer.OrdinalIgnoreCase);
			var reverse = BuildReverse(map, byGuid);
			var results = new List<AssetEntry>(nodes.Count);
			for (int i = 0; i < nodes.Count; i++)
			{
				var node = nodes[i];
				var e = BuildEntry(node, map, byGuid, reverse, context.GetGuidFromMeta(node.assetPath), context.GetFileSize(node.assetPath));
				if (e != null) results.Add(e);
			}
			Progress = 1f;
			return results;
		}

		static List<Node> ReadAllNodes(string projectRoot, bool resolveGuids)
		{
			string assetsRoot = Path.Combine(projectRoot ?? string.Empty, "Assets");
			if (!Directory.Exists(assetsRoot)) return new List<Node>();
			string[] files;
			try { files = Directory.GetFiles(assetsRoot, "*.asmdef", SearchOption.AllDirectories); }
			catch { return new List<Node>(); }

			var result = new List<Node>(files.Length);
			for (int i = 0; i < files.Length; i++)
			{
				var node = Read(files[i], projectRoot, resolveGuids);
				if (node != null && !string.IsNullOrEmpty(node.name)) result.Add(node);
			}
			result.Sort((a,b) => string.Compare(a.name,b.name,StringComparison.OrdinalIgnoreCase));
			return result;
		}

		static Node Read(string full, string projectRoot, bool resolveGuid)
		{
			try
			{
				string json = File.ReadAllText(full);
				string name = ReadJsonString(json, "name");
				if (string.IsNullOrEmpty(name)) return null;
				string relative = full.Substring(Path.Combine(projectRoot, "Assets").Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\','/');
				string assetPath = "Assets/" + relative;
				return new Node
				{
					name = name,
					relative = relative,
					assetPath = assetPath,
					guid = resolveGuid ? AssetDatabase.AssetPathToGUID(assetPath) : string.Empty,
					references = ReadJsonStringArray(json, "references"),
					autoReferenced = ReadJsonBool(json, "autoReferenced", true)
				};
			}
			catch { return null; }
		}

		static string ReadJsonString(string json, string property)
		{
			if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property)) return null;
			string token = "\"" + property + "\"";
			int i = json.IndexOf(token, StringComparison.Ordinal);
			if (i < 0) return null;
			i = json.IndexOf(':', i + token.Length);
			if (i < 0) return null;
			while (++i < json.Length && char.IsWhiteSpace(json[i])) { }
			if (i >= json.Length || json[i] != '"') return null;
			i++;
			var value = new System.Text.StringBuilder();
			bool escaped = false;
			for (; i < json.Length; i++)
			{
				char c = json[i];
				if (escaped)
				{
					value.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : c == 't' ? '\t' : c);
					escaped = false;
					continue;
				}
				if (c == '\\') { escaped = true; continue; }
				if (c == '"') break;
				value.Append(c);
			}
			return value.ToString();
		}

		static bool ReadJsonBool(string json, string property, bool defaultValue)
		{
			string token = "\"" + property + "\"";
			int i = json != null ? json.IndexOf(token, StringComparison.Ordinal) : -1;
			if (i < 0) return defaultValue;
			i = json.IndexOf(':', i + token.Length);
			if (i < 0) return defaultValue;
			while (++i < json.Length && char.IsWhiteSpace(json[i])) { }
			if (json.IndexOf("true", i, StringComparison.OrdinalIgnoreCase) == i) return true;
			if (json.IndexOf("false", i, StringComparison.OrdinalIgnoreCase) == i) return false;
			return defaultValue;
		}

		static string[] ReadJsonStringArray(string json, string property)
		{
			string token = "\"" + property + "\"";
			int i = json != null ? json.IndexOf(token, StringComparison.Ordinal) : -1;
			if (i < 0) return Array.Empty<string>();
			i = json.IndexOf(':', i + token.Length);
			if (i < 0) return Array.Empty<string>();
			i++;
			while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
			if (i >= json.Length || json[i] != '[') return Array.Empty<string>();
			var values = new List<string>();
			i++;
			while (i < json.Length)
			{
				while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
				if (i >= json.Length || json[i] == ']') break;
				if (json[i] != '"') break;
				int start = i;
				bool escaped = false;
				for (i++; i < json.Length; i++)
				{
					char c = json[i];
					if (escaped) { escaped = false; continue; }
					if (c == '\\') { escaped = true; continue; }
					if (c == '"') { i++; break; }
				}
				string raw = json.Substring(start, Math.Max(0, i - start));
				values.Add(UnquoteJson(raw));
			}
			return values.ToArray();
		}

		static string UnquoteJson(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return string.Empty;
			int start = raw[0] == '"' ? 1 : 0;
			int end = raw.Length > start && raw[raw.Length - 1] == '"' ? raw.Length - 1 : raw.Length;
			var value = new System.Text.StringBuilder(end - start);
			bool escaped = false;
			for (int i = start; i < end; i++)
			{
				char c = raw[i];
				if (escaped) { value.Append(c); escaped = false; }
				else if (c == '\\') escaped = true;
				else value.Append(c);
			}
			return value.ToString();
		}

		static Dictionary<string,int> BuildReverse(Dictionary<string,Node> map, Dictionary<string,Node> byGuid)
		{
			var reverse = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
			foreach (var node in map.Values)
				foreach (var reference in node.references ?? Array.Empty<string>())
				{
					var target = Resolve(reference, map, byGuid);
					if (target == null) continue;
					reverse.TryGetValue(target.name, out int count);
					reverse[target.name] = count + 1;
				}
			return reverse;
		}

		static AssetEntry BuildEntry(Node node, Dictionary<string,Node> map, Dictionary<string,Node> byGuid, Dictionary<string,int> reverse, string guid, long fileSize)
		{
			if (node == null) return null;
			var e = new AssetEntry
			{
				Guid = guid ?? string.Empty,
				Path = node.assetPath,
				Name = node.name,
				Directory = AssetPathUtility.Directoryish(node.assetPath),
				Extension = ".asmdef",
				Category = AssetCategory.ProjectSetting,
				TypeLabel = "Assembly Definition",
				FileSize = fileSize,
				RuntimeSize = -1
			};
			e.Set("references", (long)node.references.Length);
			e.Set("autoReferenced", node.autoReferenced ? "Yes" : "No");
			reverse.TryGetValue(node.name, out int fanIn);
			e.Set("fanIn", (long)fanIn);
			using (RuleTiming.Begin("ASM001", e.Path))
				if (node.references.Length > k_BroadReferenceWarning)
					e.AddIssue("ASM001", node.references.Length + " direct references", "Reduce unnecessary direct dependencies");
			using (RuleTiming.Begin("ASM002", e.Path))
				if (HasCycle(node.name, map, byGuid))
					e.AddIssue("ASM002", "Dependency cycle detected", "Move shared contracts to a lower-level assembly");
			e.Finalise();
			return e;
		}

		sealed class Node
		{
			public string name, relative, assetPath, guid;
			public string[] references;
			public bool autoReferenced;
		}
		[Serializable] sealed class AsmDef
		{
			public string name;
			public string[] references;
			public bool autoReferenced = true;
		}
		static Node Resolve(string reference, Dictionary<string,Node> map, Dictionary<string,Node> byGuid)
		{
			if (string.IsNullOrEmpty(reference)) return null;
			if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
			{
				string guid = reference.Substring(5);
				return byGuid.TryGetValue(guid, out var n) ? n : null;
			}
			return map.TryGetValue(reference, out var node) ? node : null;
		}
		static bool HasCycle(string start, Dictionary<string,Node> map, Dictionary<string,Node> byGuid)
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			return Dfs(start, start, map, byGuid, seen);
		}
		static bool Dfs(string current, string target, Dictionary<string,Node> map, Dictionary<string,Node> byGuid, HashSet<string> seen)
		{
			if (!map.TryGetValue(current, out var n) || n.references == null) return false;
			foreach (var r in n.references)
			{
				var dependency = Resolve(r, map, byGuid);
				if (dependency == null) continue;
				if (string.Equals(dependency.name, target, StringComparison.OrdinalIgnoreCase)) return true;
				if (seen.Add(dependency.name) && Dfs(dependency.name, target, map, byGuid, seen)) return true;
			}
			return false;
		}
	}
}

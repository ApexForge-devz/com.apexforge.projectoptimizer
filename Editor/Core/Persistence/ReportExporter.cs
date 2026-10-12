using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Writes a scan out as CSV, JSON, Markdown or SARIF for review outside the editor.</summary>
	public static class ReportExporter
	{
		public enum Format
		{
			Csv,
			Json,
			Markdown,
			Sarif
		}

		public static void ExportWithDialog(ScanResult result, Format format)
		{
			if (result == null) return;

			string extension = format == Format.Csv ? "csv" : format == Format.Json || format == Format.Sarif ? "json" : "md";
			string defaultName = $"project-optimizer-report-{Application.productName}-{DateTime.Now.ToString("yyyyMMdd-HHmm")}.{extension}";
			string path = EditorUtility.SaveFilePanel("Export optimization report", string.Empty, defaultName, extension);
			if (string.IsNullOrEmpty(path)) return;

			try
			{
				EditorUtility.DisplayProgressBar(ProjectOptimizerConstants.ProductName, "Writing " + Path.GetFileName(path) + "...", 0.5f);
				File.WriteAllText(path, Build(result, format));
			}
			catch (Exception e)
			{
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Export failed: " + e);
				Feedback.Error("Could not write the report: " + e.Message);
				return;
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}

			Debug.Log(ProjectOptimizerConstants.LogPrefix + " Report written to " + path);

			// Offered rather than done: opening a file browser unasked pulls focus out of the editor,
			// which is not what someone exporting a second format wants.
			Feedback.Success("Report saved as " + Path.GetFileName(path),
				"Show in folder", () => EditorUtility.RevealInFinder(path));
		}

		public static string Build(ScanResult result, Format format)
		{
			switch (format)
			{
				case Format.Json: return BuildJson(result);
				case Format.Markdown: return BuildMarkdown(result);
				case Format.Sarif: return BuildSarif(result);
				default: return BuildCsv(result);
			}
		}

		static IEnumerable<KeyValuePair<AssetEntry, OptimizationIssue>> Issues(ScanResult result)
		{
			foreach (var entry in result.Entries)
				foreach (var issue in entry.Issues)
					yield return new KeyValuePair<AssetEntry, OptimizationIssue>(entry, issue);
		}

		static string BuildCsv(ScanResult result)
		{
			var sb = new StringBuilder();
			// Confidence and savings basis are columns of their own. A row saying "Critical, 4 MB"
			// reads as a measurement; the same row next to "heuristic" and "unknown" reads as what
			// it is, and anyone filtering the export can act on the difference.
			sb.AppendLine("Severity,Confidence,Rule,Category,Asset,Path,Issue,Current,Recommended,"
						  + "EstimatedSavingsBytes,SavingsBasis,QualityLevel,AutoFixable,FixKind,"
						  + "ReviewOptionCount,Destructive,Verified,Stale");
			foreach (var pair in Issues(result))
			{
				var e = pair.Key;
				var i = pair.Value;
				sb.Append(Csv(DisplayUtility.SeverityLabel(i.Severity))).Append(',')
				  .Append(Csv(DisplayUtility.ConfidenceLabel(i.Confidence))).Append(',')
				  .Append(Csv(i.RuleId)).Append(',')
				  .Append(Csv(DisplayUtility.CategoryLabel(e.Category))).Append(',')
				  .Append(Csv(e.Name)).Append(',')
				  .Append(Csv(e.Path)).Append(',')
				  .Append(Csv(i.Title)).Append(',')
				  .Append(Csv(i.Current)).Append(',')
				  .Append(Csv(i.Recommended)).Append(',')
				  .Append(i.EstimatedSavings.ToString(CultureInfo.InvariantCulture)).Append(',')
				  .Append(Csv(i.Basis.ToString())).Append(',')
				  .Append(Csv(QualityLevelOf(i))).Append(',')
				  .Append(i.AutoFixable ? "yes" : "no").Append(',')
				  .Append(Csv(i.FixKind.ToString())).Append(',')
				  .Append(i.Options.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
				  .Append(i.Destructive ? "yes" : "no").Append(',')
				  .Append(i.Verify != null ? "yes" : "no").Append(',')
				  .Append(i.Stale ? "yes" : "no")
				  .AppendLine();
			}
			return sb.ToString();
		}

		/// <summary>
		/// "Android/2 (Medium)" for a finding read from one quality level, empty for anything that
		/// is not level-specific. Exported so a reader can reproduce the reading rather than having
		/// to guess which level produced it.
		/// </summary>
		static string QualityLevelOf(OptimizationIssue issue)
		{
			if (issue.QualityLevelIndex < 0) return string.Empty;

			var text = (issue.TargetPlatform ?? "?") + "/" + issue.QualityLevelIndex;
			if (!string.IsNullOrEmpty(issue.QualityLevelName)) text += " (" + issue.QualityLevelName + ")";
			return text;
		}

		static string Csv(string value)
		{
			if (string.IsNullOrEmpty(value)) return string.Empty;
			if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
			return "\"" + value.Replace("\"", "\"\"") + "\"";
		}

		static string BuildJson(ScanResult result)
		{
			var sb = new StringBuilder();
			sb.AppendLine("{");
			sb.Append("  \"scannedAt\": \"").Append(result.ScanTime.ToString("o")).AppendLine("\",");
			sb.Append("  \"durationSeconds\": ").Append(result.DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(",");
			sb.Append("  \"scannedAssets\": ").Append(result.ScannedAssets).AppendLine(",");
			sb.Append("  \"critical\": ").Append(result.CriticalCount).AppendLine(",");
			sb.Append("  \"warning\": ").Append(result.WarningCount).AppendLine(",");
			sb.Append("  \"info\": ").Append(result.InfoCount).AppendLine(",");
			sb.Append("  \"estimatedSavingsBytes\": ").Append(result.PotentialSavings).AppendLine(",");
			sb.Append("  \"toolSchema\": ").Append(OptimizerVersion.Schema).AppendLine(",");

			// Exported so a report read later cannot be mistaken for a complete picture of the
			// project. An empty category means "nothing found here" only if this list is empty too.
			sb.Append("  \"complete\": ").Append(result.IsComplete ? "true" : "false").AppendLine(",");
			sb.AppendLine("  \"completenessCaveats\": [");
			for (int i = 0; i < result.CompletenessCaveats.Count; i++)
			{
				sb.Append("    \"").Append(Json(result.CompletenessCaveats[i])).Append("\"");
				sb.AppendLine(i < result.CompletenessCaveats.Count - 1 ? "," : string.Empty);
			}
			sb.AppendLine("  ],");
			sb.AppendLine("  \"analysisLimitations\": [");
			for (int i = 0; i < result.AnalysisLimitations.Count; i++)
			{
				sb.Append("    \"").Append(Json(result.AnalysisLimitations[i])).Append("\"");
				sb.AppendLine(i < result.AnalysisLimitations.Count - 1 ? "," : string.Empty);
			}
			sb.AppendLine("  ],");
			sb.AppendLine("  \"issues\": [");

			bool first = true;
			foreach (var pair in Issues(result))
			{
				if (!first) sb.AppendLine(",");
				first = false;
				var e = pair.Key;
				var i = pair.Value;
				sb.Append("    { ");
				sb.Append("\"severity\": \"").Append(DisplayUtility.SeverityLabel(i.Severity)).Append("\", ");
				sb.Append("\"confidence\": \"").Append(i.Confidence).Append("\", ");
				sb.Append("\"rule\": \"").Append(Json(i.RuleId)).Append("\", ");
				sb.Append("\"category\": \"").Append(Json(DisplayUtility.CategoryLabel(e.Category))).Append("\", ");
				sb.Append("\"path\": \"").Append(Json(e.Path)).Append("\", ");
				sb.Append("\"title\": \"").Append(Json(i.Title)).Append("\", ");
				sb.Append("\"current\": \"").Append(Json(i.Current)).Append("\", ");
				sb.Append("\"recommended\": \"").Append(Json(i.Recommended)).Append("\", ");
				sb.Append("\"estimatedSavingsBytes\": ").Append(i.EstimatedSavings).Append(", ");
				sb.Append("\"savingsBasis\": \"").Append(i.Basis).Append("\", ");
				if (i.QualityLevelIndex >= 0)
				{
					sb.Append("\"qualityLevel\": \"").Append(Json(QualityLevelOf(i))).Append("\", ");
				}
				sb.Append("\"autoFixable\": ").Append(i.AutoFixable ? "true" : "false").Append(", ");
				sb.Append("\"fixKind\": \"").Append(i.FixKind).Append("\", ");
				sb.Append("\"reviewOptionCount\": ").Append(i.Options.Count).Append(", ");
				sb.Append("\"destructive\": ").Append(i.Destructive ? "true" : "false").Append(", ");
				sb.Append("\"verified\": ").Append(i.Verify != null ? "true" : "false").Append(", ");
				sb.Append("\"stale\": ").Append(i.Stale ? "true" : "false");
				sb.Append(" }");
			}

			sb.AppendLine();
			sb.AppendLine("  ]");
			sb.AppendLine("}");
			return sb.ToString();
		}

		static string Json(string value)
		{
			if (string.IsNullOrEmpty(value)) return string.Empty;
			var sb = new StringBuilder(value.Length + 8);
			foreach (char c in value)
			{
				switch (c)
				{
					case '\\': sb.Append("\\\\"); break;
					case '"': sb.Append("\\\""); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					case '\b': sb.Append("\\b"); break;
					case '\f': sb.Append("\\f"); break;
					default:
						if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						else sb.Append(c);
						break;
				}
			}
			return sb.ToString();
		}

		/// <summary>Builds SARIF 2.1.0 so optimization findings can be consumed by CI/code-scanning tooling.</summary>
		public static string BuildSarif(ScanResult result)
		{
			if (result == null) return "{}";

			var ruleTitles = new Dictionary<string, string>(StringComparer.Ordinal);
			var results = new StringBuilder();
			bool firstResult = true;
			foreach (var pair in Issues(result))
			{
				var entry = pair.Key;
				var issue = pair.Value;
				if (issue == null || string.IsNullOrEmpty(issue.RuleId) || issue.Stale) continue;
				if (!ruleTitles.ContainsKey(issue.RuleId)) ruleTitles[issue.RuleId] = issue.Title ?? issue.RuleId;

				if (!firstResult) results.AppendLine(",");
				firstResult = false;
				string level = issue.Severity == IssueSeverity.Critical ? "error" : issue.Severity == IssueSeverity.Warning ? "warning" : "note";
				string message = issue.Title ?? issue.RuleId;
				if (!string.IsNullOrEmpty(issue.Current)) message += ": " + issue.Current;
				if (!string.IsNullOrEmpty(issue.Recommended)) message += ". Recommended: " + issue.Recommended;

				results.Append("      { \"ruleId\": \"")
					.Append(Json(issue.RuleId)).Append("\", \"level\": \"").Append(level)
					.Append("\", \"message\": { \"text\": \"").Append(Json(message)).Append("\" }");
				if (!string.IsNullOrEmpty(entry.Path))
				{
					results.Append(", \"locations\": [ { \"physicalLocation\": { \"artifactLocation\": { \"uri\": \"")
						.Append(Json(entry.Path.Replace('\\', '/'))).Append("\"");
					if (issue.SourceLine > 0)
						results.Append(", \"region\": { \"startLine\": ").Append(issue.SourceLine.ToString(CultureInfo.InvariantCulture)).Append(" }");
					results.Append(" } } } ]");
				}
				results.Append(" }");
			}

			var rules = new StringBuilder();
			bool firstRule = true;
			var rulePairs = new List<KeyValuePair<string, string>>(ruleTitles);
			rulePairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
			foreach (var pair in rulePairs)
			{
				if (!firstRule) rules.AppendLine(",");
				firstRule = false;
				rules.Append("        { \"id\": \"").Append(Json(pair.Key))
					.Append("\", \"shortDescription\": { \"text\": \"").Append(Json(pair.Value)).Append("\" } }");
			}

			var sb = new StringBuilder();
			sb.AppendLine("{");
			sb.AppendLine("  \"version\": \"2.1.0\",");
			sb.AppendLine("  \"$schema\": \"https://json.schemastore.org/sarif-2.1.0.json\",");
			sb.AppendLine("  \"runs\": [ {");
			sb.AppendLine("    \"tool\": { \"driver\": {");
			sb.Append("      \"name\": \"").Append(ProjectOptimizerConstants.ProductName).AppendLine("\",");
			sb.Append("      \"version\": \"").Append(Json(OptimizerVersion.Display)).AppendLine("\",");
			sb.AppendLine("      \"rules\": [");
			sb.Append(rules).AppendLine();
			sb.AppendLine("      ]");
			sb.AppendLine("    } },");
			sb.AppendLine("    \"results\": [");
			sb.Append(results).AppendLine();
			sb.AppendLine("    ],");
			sb.AppendLine("    \"properties\": {");
			sb.Append("      \"complete\": ").Append(result.IsComplete ? "true" : "false").AppendLine(",");
			sb.Append("      \"analysisLimitationCount\": ").Append(result.AnalysisLimitations.Count).AppendLine(",");
			sb.Append("      \"toolSchema\": ").Append(OptimizerVersion.Schema).AppendLine();
			sb.AppendLine("    }");
			sb.AppendLine("  } ]");
			sb.AppendLine("}");
			return sb.ToString();
		}

		static string BuildMarkdown(ScanResult result)
		{
			var sb = new StringBuilder();
			sb.AppendLine("# " + ProjectOptimizerConstants.ProductName + " report");
			sb.AppendLine();
			sb.Append("- Scanned ").Append(result.ScannedAssets).Append(" assets in ")
			  .Append(result.DurationSeconds.ToString("0.0")).AppendLine(" s");
			sb.Append("- ").Append(result.CriticalCount).Append(" critical, ")
			  .Append(result.WarningCount).Append(" warnings, ")
			  .Append(result.InfoCount).AppendLine(" informational");
			sb.Append("- Estimated recoverable: ").AppendLine(DisplayUtility.FormatBytes(result.PotentialSavings));
			sb.AppendLine();

			foreach (var category in result.ByCategory.Keys)
			{
				var entries = result.ByCategory[category];
				int issueCount = 0;
				foreach (var entry in entries) issueCount += entry.Issues.Count;
				if (issueCount == 0) continue;

				sb.Append("## ").AppendLine(DisplayUtility.CategoryLabel(category));
				sb.AppendLine();
				sb.AppendLine("| Severity | Rule | Fix | Asset | Current | Recommended | Est. saving |");
				sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
				foreach (var entry in entries)
				{
					foreach (var issue in entry.Issues)
					{
						sb.Append("| ").Append(DisplayUtility.SeverityLabel(issue.Severity))
						  .Append(" | ").Append(issue.RuleId)
						  .Append(" | ").Append(issue.Stale ? "Stale" : issue.FixKind.ToString())
						  .Append(" | ").Append(entry.Path)
						  .Append(" | ").Append(issue.Current)
						  .Append(" | ").Append(issue.Recommended)
						  .Append(" | ").Append(issue.EstimatedSavings > 0 ? DisplayUtility.FormatBytes(issue.EstimatedSavings) : "-")
						  .AppendLine(" |");
					}
				}
				sb.AppendLine();
			}
			return sb.ToString();
		}
	}
}

using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Batch-mode entry point for CI. Usage: -executeMethod ApexForge.ProjectOptimizer.ProjectOptimizerCi.Run
	/// Optional: -poMaxCritical N, -poMaxWarnings N, -poRequireBaseline, -poOutput PATH.
	/// </summary>
	public static class ProjectOptimizerCi
	{
		static ProjectScanner s_Scanner;
		static int s_MaxCritical = -1;
		static int s_MaxWarnings = -1;
		static bool s_RequireBaseline;
		static string s_OutputDirectory;

		public static void Run()
		{
			if (!Application.isBatchMode)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " CI runner is intended for Unity batch mode.");
				return;
			}

			s_MaxCritical = IntArg("-poMaxCritical", -1);
			s_MaxWarnings = IntArg("-poMaxWarnings", -1);
			s_RequireBaseline = HasArg("-poRequireBaseline");
			s_OutputDirectory = StringArg("-poOutput", ProjectOptimizerConstants.CiFolder);
			if (!Path.IsPathRooted(s_OutputDirectory)) s_OutputDirectory = Path.Combine(Directory.GetCurrentDirectory(), s_OutputDirectory);
			Directory.CreateDirectory(s_OutputDirectory);

			s_Scanner = new ProjectScanner();
			s_Scanner.Completed += OnCompleted;
			if (!s_Scanner.Begin(OptimizerSettings.Instance))
			{
				WriteFailure("The scan could not start. " + s_Scanner.Status);
				EditorApplication.Exit(2);
				return;
			}
			EditorApplication.update += Tick;
		}

		static void Tick()
		{
			if (s_Scanner == null) return;
			if (s_Scanner.IsRunning) s_Scanner.Tick(50.0);
		}

		static void OnCompleted(ScanResult result)
		{
			EditorApplication.update -= Tick;
			try
			{
				var ruleAudit = RuleAudit.Run(result);
				var integrityAudit = ProjectOptimizerIntegrityAudit.Run();
				File.WriteAllText(Path.Combine(s_OutputDirectory, "ProjectOptimizerReport.json"), ReportExporter.Build(result, ReportExporter.Format.Json));
				File.WriteAllText(Path.Combine(s_OutputDirectory, "ProjectOptimizerReport.md"), ReportExporter.Build(result, ReportExporter.Format.Markdown));
				File.WriteAllText(Path.Combine(s_OutputDirectory, "ProjectOptimizerReport.sarif"), ReportExporter.Build(result, ReportExporter.Format.Sarif));
				File.WriteAllText(Path.Combine(s_OutputDirectory, "ProjectOptimizerRuleAudit.txt"), RuleAudit.Inventory() + Environment.NewLine + string.Join(Environment.NewLine, integrityAudit.ToArray()));
				int ruleAuditErrors = 0;
				int integrityErrors = integrityAudit.Count;

				for (int i = 0; i < ruleAudit.Count; i++) if (ruleAudit[i].IsError) ruleAuditErrors++;
				var baselineComparison = s_RequireBaseline ? OptimizationBaselineStore.Compare(result) : null;
				bool baselineReady = !s_RequireBaseline || (baselineComparison != null && baselineComparison.HasBaseline && baselineComparison.Compatible);
				bool pass = !result.Aborted
					&& result.IsComplete
					&& ruleAuditErrors == 0
					&& integrityErrors == 0
					&& (s_MaxCritical < 0 || result.CriticalCount <= s_MaxCritical)
					&& (s_MaxWarnings < 0 || result.WarningCount <= s_MaxWarnings)
					&& baselineReady;

				if (result.Aborted) Debug.LogError(ProjectOptimizerConstants.LogPrefix + " CI scan aborted before completion.");
				if (!result.IsComplete) Debug.LogError(ProjectOptimizerConstants.LogPrefix + " CI scan is incomplete: " + string.Join("; ", result.CompletenessCaveats.ToArray()));
				if (s_MaxCritical >= 0 && result.CriticalCount > s_MaxCritical)
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Critical budget exceeded: " + result.CriticalCount + " > " + s_MaxCritical);
				if (s_MaxWarnings >= 0 && result.WarningCount > s_MaxWarnings)
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Warning budget exceeded: " + result.WarningCount + " > " + s_MaxWarnings);
				if (s_RequireBaseline && !baselineReady)
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " A compatible complete optimization baseline is required. " + (baselineComparison == null ? "None is stored." : baselineComparison.CompatibilityNote));

				if (integrityErrors > 0)
				{
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Integrity audit failed with " + integrityErrors + " error(s); CI will not pass.");
					for (int i = 0; i < integrityAudit.Count; i++) Debug.LogError(ProjectOptimizerConstants.LogPrefix + " " + integrityAudit[i]);
				}

				if (ruleAuditErrors > 0)
				{
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Rule/fix audit failed with " + ruleAuditErrors + " error(s); CI will not pass with malformed executable rules.");
					for (int i = 0; i < ruleAudit.Count; i++) if (ruleAudit[i].IsError) Debug.LogError(ruleAudit[i].ToString());
				}

				Debug.Log((pass ? ProjectOptimizerConstants.LogPrefix + " CI PASS" : ProjectOptimizerConstants.LogPrefix + " CI FAIL")
					+ " | " + result.CriticalCount + " critical, " + result.WarningCount + " warnings, " + result.InfoCount + " info"
					+ " | complete=" + result.IsComplete
					+ " | ruleAuditErrors=" + ruleAuditErrors
					+ " | integrityErrors=" + integrityErrors
					+ " | reports=" + s_OutputDirectory);
				EditorApplication.Exit(pass ? 0 : 1);
			}
			catch (Exception e)
			{
				WriteFailure("Could not write CI reports: " + e);
				EditorApplication.Exit(3);
			}
		}

		static void WriteFailure(string message)
		{
			try
			{
				if (!string.IsNullOrEmpty(s_OutputDirectory)) Directory.CreateDirectory(s_OutputDirectory);
				File.WriteAllText(Path.Combine(s_OutputDirectory ?? Directory.GetCurrentDirectory(), "ProjectOptimizerFailure.txt"),
					message + Environment.NewLine + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
			}
			catch { }
			Debug.LogError(ProjectOptimizerConstants.LogPrefix + " " + message);
		}

		static bool HasArg(string name)
		{
			var args = Environment.GetCommandLineArgs();
			for (int i = 0; i < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		static int IntArg(string name, int fallback)
		{
			string value = StringArg(name, null);
			return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
		}

		static string StringArg(string name, string fallback)
		{
			var args = Environment.GetCommandLineArgs();
			for (int i = 0; i < args.Length - 1; i++)
				if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
			return fallback;
		}
	}
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Stable project/configuration identity and freshness checks for external evidence.</summary>
	public static class EvidenceIdentity
	{
		public enum Freshness
		{
			Fresh,
			Stale,
			InvalidTimestamp,
			DifferentUnityVersion,
			DifferentProject,
			DifferentPlatform,
			PlatformIdentityUnavailable,
			ProjectIdentityUnavailable
		}

		public static string ComputeProjectFingerprint()
		{
			try
			{
				string root = Directory.GetCurrentDirectory();
				var paths = new System.Collections.Generic.List<string>();
				string projectSettings = Path.Combine(root, "ProjectSettings");
				if (Directory.Exists(projectSettings)) paths.AddRange(Directory.GetFiles(projectSettings, "*", SearchOption.TopDirectoryOnly));
				string addressablesConfig = Path.Combine(root, "Assets", "AddressableAssetsData");
				if (Directory.Exists(addressablesConfig)) paths.AddRange(Directory.GetFiles(addressablesConfig, "*", SearchOption.AllDirectories));
				paths.Add(Path.Combine(root, "Packages", "manifest.json"));
				paths.Add(Path.Combine(root, "Packages", "packages-lock.json"));
				paths.Sort(StringComparer.OrdinalIgnoreCase);
				using (var hash = SHA256.Create())
				using (var stream = new MemoryStream())
				{
					for (int i = 0; i < paths.Count; i++)
					{
						string path = paths[i];
						string relative = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
						byte[] rel = Encoding.UTF8.GetBytes(relative.Replace('\\', '/') + "\n");
						stream.Write(rel, 0, rel.Length);
						if (File.Exists(path))
						{
							byte[] content = File.ReadAllBytes(path);
							stream.Write(content, 0, content.Length);
						}
						byte[] sep = Encoding.UTF8.GetBytes("\n--\n");
						stream.Write(sep, 0, sep.Length);
					}
					byte[] digest = hash.ComputeHash(stream.ToArray());
					return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
				}
			}
			catch { return string.Empty; }
		}

		public static Freshness Evaluate(string timestampUtc, string unityVersion, string projectFingerprint, TimeSpan maxAge, out string reason)
			=> Evaluate(timestampUtc, unityVersion, projectFingerprint, null, null, maxAge, out reason);

		public static Freshness Evaluate(string timestampUtc, string unityVersion, string projectFingerprint, string evidencePlatform, string currentPlatform, TimeSpan maxAge, out string reason)
		{
			reason = null;
			if (!DateTimeOffset.TryParse(timestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
			{ reason = "Evidence timestamp is missing or invalid."; return Freshness.InvalidTimestamp; }
			string currentUnity = Application.unityVersion ?? string.Empty;
			if (!string.IsNullOrEmpty(unityVersion) && !string.Equals(unityVersion, currentUnity, StringComparison.OrdinalIgnoreCase))
			{ reason = "Captured by Unity " + unityVersion + "; current editor is " + currentUnity + "."; return Freshness.DifferentUnityVersion; }
			if (!string.IsNullOrEmpty(evidencePlatform) && !string.Equals(evidencePlatform, "Player", StringComparison.OrdinalIgnoreCase))
			{
				if (string.IsNullOrEmpty(currentPlatform))
				{ reason = "Evidence contains a target platform but the current active target is unavailable, so target identity cannot be confirmed."; return Freshness.PlatformIdentityUnavailable; }
				if (!string.Equals(evidencePlatform, currentPlatform, StringComparison.OrdinalIgnoreCase))
				{ reason = "Captured for target " + evidencePlatform + "; current active target is " + currentPlatform + "."; return Freshness.DifferentPlatform; }
			}
			string currentFingerprint = ComputeProjectFingerprint();
			if (!string.IsNullOrEmpty(projectFingerprint) && !string.IsNullOrEmpty(currentFingerprint) && !string.Equals(projectFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
			{ reason = "Evidence was captured from a different project/configuration fingerprint."; return Freshness.DifferentProject; }
			bool projectIdentityAvailable = !string.IsNullOrEmpty(projectFingerprint) && !string.IsNullOrEmpty(currentFingerprint);
			TimeSpan age = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
			if (age < TimeSpan.Zero) age = TimeSpan.Zero;
			if (age > maxAge)
			{ reason = "Evidence is " + age.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " hours old; freshness limit is " + maxAge.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " hours."; return Freshness.Stale; }
			if (!projectIdentityAvailable)
			{ reason = "Evidence timestamp and Unity version are current, but the source did not provide a project fingerprint; project identity is not independently confirmed."; return Freshness.ProjectIdentityUnavailable; }
			reason = "Evidence is current for this Unity version and project within the configured freshness window.";
			return Freshness.Fresh;
		}
	}
}

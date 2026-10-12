using System;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Holds the editor still for the length of a scan.
	/// <para>
	/// A scan is a long-lived object living in a static field, driven from EditorApplication.update.
	/// Two ordinary editor behaviours destroy it outright:
	/// </para>
	/// <list type="bullet">
	/// <item>Unity auto-refreshes the asset database whenever it goes idle or regains focus. On a
	/// large project that means importing whatever is pending - the "Importing Assets" bar - in the
	/// middle of the walk, which both stalls the scan and changes the very thing being measured.</item>
	/// <item>If any script is among those imports, Unity recompiles and reloads the domain. A domain
	/// reload wipes every managed object, so the scan simply vanishes part-way through, which reads
	/// from the outside as "it stopped by itself".</item>
	/// </list>
	/// <para>
	/// So for the duration of a scan both are suspended. Neither is cancelled - edits and imports
	/// queue up and are applied the moment the scan ends.
	/// </para>
	/// <para>
	/// Both calls are reference counted inside Unity, which makes a leaked lock genuinely harmful:
	/// an editor left with assemblies locked never compiles again until it is restarted. Every path
	/// out of a scan therefore releases, and a watchdog releases anyway if the scan stops
	/// heartbeating - a scan that dies in a way we did not anticipate must not take the editor with
	/// it.
	/// </para>
	/// </summary>
	public static class ScanGuard
	{
		/// <summary>How long without a heartbeat before the guard assumes the scan is gone.</summary>
		const double k_WatchdogSeconds = 90.0;

		// Ownership is tracked per lock, not as one "held" flag.
		//
		// The two calls below are independent, reference-counted editor locks, and the previous
		// single flag was only set after both succeeded. If the first succeeded and the second threw,
		// the flag was still false, so the recovery path released nothing - and the assembly lock
		// stayed held for the rest of the editor session. Unity never compiles again after that, and
		// nothing in the UI explains why, so the only way out is a restart.
		//
		// Each lock is now recorded the instant it is taken, and released only if it was.
		static bool s_ReloadLocked;
		static bool s_AutoRefreshDisallowed;
		static bool s_CallbacksAttached;
		static double s_LastHeartbeat;

		/// <summary>True while this class holds either editor lock.</summary>
		public static bool IsHeld => s_ReloadLocked || s_AutoRefreshDisallowed;

		/// <summary>True only when both locks are held, which is what a healthy scan looks like.</summary>
		public static bool IsFullyHeld => s_ReloadLocked && s_AutoRefreshDisallowed;

		/// <summary>
		/// Raised when the watchdog decides the scan is gone. The scanner subscribes so it can stop:
		/// releasing the locks without stopping the work is the exact state the guard exists to
		/// prevent - assemblies could reload and the asset database could refresh underneath a scan
		/// that is still walking it.
		/// </summary>
		public static event Action WatchdogExpired;

		/// <summary>
		/// Takes both editor locks, and reports whether it got them.
		/// <para>
		/// Fails closed. A scan needs assembly reloading locked <em>and</em> auto-refresh suspended;
		/// with either missing, Unity can recompile or reimport part-way through and the scan is
		/// measuring a project that is changing underneath it. So if one lock cannot be taken, the
		/// other is released again and the caller is told not to start.
		/// </para>
		/// <para>
		/// Idempotent: each lock is taken at most once, so Unity's own reference counts stay balanced
		/// however many times this is called.
		/// </para>
		/// </summary>
		/// <returns>True only when both locks are held.</returns>
		public static bool TryAcquire()
		{
			// Each lock is taken in its own try. One failing must not stop the other from being
			// taken, and must not leave the one that succeeded unrecorded - that was the original
			// leak. What is new is that a partial result is no longer allowed to stand.
			if (!s_ReloadLocked)
			{
				try
				{
					EditorApplication.LockReloadAssemblies();
					s_ReloadLocked = true;
				}
				catch (Exception e)
				{
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not lock assembly reloading for the scan: " + e.Message);
				}
			}

			if (!s_AutoRefreshDisallowed)
			{
				try
				{
					AssetDatabase.DisallowAutoRefresh();
					s_AutoRefreshDisallowed = true;
				}
				catch (Exception e)
				{
					Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not suspend auto-refresh for the scan: " + e.Message);
				}
			}

			if (!IsFullyHeld)
			{
				// Give back whichever half succeeded and refuse. Running with one lock is not a
				// degraded scan, it is an unsound one.
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " The scan cannot start: the editor locks it needs are not "
							   + "available (assembly reload lock: " + (s_ReloadLocked ? "held" : "FAILED")
							   + ", auto-refresh suspension: " + (s_AutoRefreshDisallowed ? "held" : "FAILED")
							   + "). Nothing was scanned.");
				Release();
				return false;
			}

			s_LastHeartbeat = EditorApplication.timeSinceStartup;
			AttachCallbacks();
			return true;
		}

		/// <summary>
		/// Releases whichever locks this class actually holds. Idempotent, and safe to call after a
		/// partial or failed <see cref="TryAcquire"/> - which is the case that used to strand the
		/// editor.
		/// </summary>
		public static void Release()
		{
			DetachCallbacks();

			if (s_AutoRefreshDisallowed)
			{
				// Cleared first, so a throwing call cannot leave the flag set and have a later
				// Release try to unbalance the counter a second time.
				s_AutoRefreshDisallowed = false;
				try { AssetDatabase.AllowAutoRefresh(); }
				catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " AllowAutoRefresh failed: " + e.Message); }
			}

			if (s_ReloadLocked)
			{
				s_ReloadLocked = false;
				try { EditorApplication.UnlockReloadAssemblies(); }
				catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " UnlockReloadAssemblies failed: " + e.Message); }
			}
		}

		static void AttachCallbacks()
		{
			if (s_CallbacksAttached) return;
			s_CallbacksAttached = true;
			EditorApplication.update += Watchdog;
			EditorApplication.quitting += ReleaseOnQuit;
		}

		static void DetachCallbacks()
		{
			if (!s_CallbacksAttached) return;
			s_CallbacksAttached = false;
			EditorApplication.update -= Watchdog;
			EditorApplication.quitting -= ReleaseOnQuit;
		}

		/// <summary>Called by the scanner every tick, to say it is still alive.</summary>
		public static void Heartbeat()
		{
			s_LastHeartbeat = EditorApplication.timeSinceStartup;
		}

		static void Watchdog()
		{
			if (!IsHeld) return;
			if (EditorApplication.timeSinceStartup - s_LastHeartbeat < k_WatchdogSeconds) return;

			Debug.LogError(
				ProjectOptimizerConstants.LogPrefix + " The scan stopped reporting for " + (int)k_WatchdogSeconds
				+ " s and has been aborted. Script compilation and asset refresh are back on, and any "
				+ "results from that scan are incomplete.");

			// Order matters. The scan is told to stop first, while the locks are still held, so it
			// cannot take another step against an asset database that is about to start refreshing
			// again. Releasing first and hoping the scan noticed is what made the old warning's
			// claim of "abandoned" untrue.
			var expired = WatchdogExpired;
			if (expired != null)
			{
				try { expired(); }
				catch (Exception e)
				{
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " A watchdog listener threw while aborting the scan: " + e.Message);
				}
			}

			Release();
		}

		static void ReleaseOnQuit() => Release();
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Probes whether THIS machine satisfies a <see cref="HostRequirement"/> - the shared implementation behind
	/// the hard capability gate (§10: capability wins over <see cref="RunLocation"/>). Two callers, two roles:
	/// the Build Panel AUTO-DISABLES a local-context action whose requirements are unmet (prevention, with the
	/// unmet requirement shown as the reason), and <c>BuildRunner</c> FAILS a still-enabled action with the same
	/// reason just before executing it (enforcement, fail-early). Probes are cheap - one short-lived
	/// where/which/adb per uncached value, with an in-process PATH scan as fallback - and results are cached;
	/// the panel clears the cache on refresh so a newly installed tool or freshly plugged-in device is noticed.
	/// </summary>
	public static class HostProbe
	{
		#region Fields

		// Reason-or-null per "kind:value". A null VALUE is a cached "satisfied", so lookups must use
		// TryGetValue (an indexer default would re-probe every satisfied requirement).
		private static readonly Dictionary<string, string> _cache =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region Public Methods

		/// <summary>
		/// Human-readable reason for the FIRST unmet requirement in <paramref name="requirements"/>, or null
		/// when this machine satisfies all of them.
		/// </summary>
		public static string FindUnmet(IEnumerable<HostRequirement> requirements)
		{
			if (requirements == null) return null;

			foreach (HostRequirement requirement in requirements)
			{
				string reason = Check(requirement);
				if (reason != null) return reason;
			}

			return null;
		}

		/// <summary>
		/// The reason this machine does NOT satisfy <paramref name="requirement"/>, or null when it does.
		/// Results are cached until <see cref="InvalidateCache"/>.
		/// </summary>
		public static string Check(HostRequirement requirement)
		{
			string key = requirement.Kind + ":" + (requirement.Value ?? string.Empty);
			if (_cache.TryGetValue(key, out string cached)) return cached;

			string reason = Probe(requirement);
			_cache[key] = reason;
			return reason;
		}

		/// <summary>
		/// Drops every cached probe result. The Build Panel calls this on refresh, so the environment is probed
		/// at most once per panel refresh (not per IMGUI repaint) yet tools/devices appearing between refreshes
		/// are still picked up.
		/// </summary>
		public static void InvalidateCache()
		{
			_cache.Clear();
		}

		#endregion

		#region Private Methods

		private static string Probe(HostRequirement requirement)
		{
			switch (requirement.Kind)
			{
				case HostRequirement.HostKind.OperatingSystem:
					return CurrentOsMatches(requirement.Value)
						? null
						: "requires OS '" + requirement.Value + "' (host is " + Application.platform + ")";
				case HostRequirement.HostKind.Tool:
					return ToolAvailable(requirement.Value)
						? null
						: "requires tool '" + requirement.Value + "' on PATH";
				case HostRequirement.HostKind.Device:
					return ProbeDevice(requirement.Value);
				default:
					return null;
			}
		}

		private static bool CurrentOsMatches(string os)
		{
			if (string.IsNullOrEmpty(os)) return true;

			switch (os.Trim().ToLowerInvariant())
			{
				case "macos":
				case "osx":
				case "mac":
					return IsMac;
				case "windows":
				case "win":
					return IsWindows;
				case "linux":
					return IsLinux;
				default:
					return false;
			}
		}

		private static bool IsMac => Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer;
		private static bool IsWindows => Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;
		private static bool IsLinux => Application.platform == RuntimePlatform.LinuxEditor || Application.platform == RuntimePlatform.LinuxPlayer;

		private static bool ToolAvailable(string tool)
		{
			if (string.IsNullOrEmpty(tool)) return true;

			// where/which resolve exactly what a shell launch resolves (PATHEXT, execute bits, aliases),
			// which a bare File.Exists scan can miss - and ActionProcess is the same helper the actions
			// themselves shell out with, so probe and execution agree on lookup semantics.
			try
			{
				ActionProcess.ToolResult result = ActionProcess
					.RunAsync(IsWindows ? "where" : "which", new[] { tool }, null, null, 1)
					.GetAwaiter().GetResult();
				return result.Succeeded;
			}
			catch (Exception)
			{
				// where/which itself failed to launch (stripped-down host) - fall back to a manual PATH
				// scan rather than reporting every tool as missing.
				return ToolOnPathScan(tool);
			}
		}

		/// <summary>Fallback PATH + PATHEXT file scan for hosts where where/which cannot be launched.</summary>
		private static bool ToolOnPathScan(string tool)
		{
			string path = Environment.GetEnvironmentVariable("PATH") ?? "";

			List<string> extensions = new List<string> { "" };
			if (IsWindows)
			{
				string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD";
				foreach (string ext in pathExt.Split(';'))
				{
					if (!string.IsNullOrEmpty(ext)) extensions.Add(ext);
				}
			}

			foreach (string directory in path.Split(Path.PathSeparator))
			{
				string trimmed = directory.Trim();
				if (string.IsNullOrEmpty(trimmed)) continue;

				foreach (string ext in extensions)
				{
					try
					{
						if (File.Exists(Path.Combine(trimmed, tool + ext))) return true;
					}
					catch (Exception)
					{
						// Malformed PATH entry - ignore and keep scanning.
					}
				}
			}

			return false;
		}

		private static string ProbeDevice(string device)
		{
			// Only Android is probeable today ('adb devices' is the action's own transport). Other device
			// kinds (e.g. a future "ios") are treated as satisfied rather than blocking an action whose
			// device presence we cannot verify - the action itself still fails with the tool's real error.
			if (!string.Equals((device ?? string.Empty).Trim(), "android", StringComparison.OrdinalIgnoreCase)) return null;

			try
			{
				ActionProcess.ToolResult result = ActionProcess
					.RunAsync("adb", new[] { "devices" }, null, null, 1)
					.GetAwaiter().GetResult();
				if (!result.Succeeded)
				{
					return "requires a connected android device ('adb devices' failed, exit " + result.ExitCode + ")";
				}

				return HasConnectedAdbDevice(result.StdOut)
					? null
					: "requires a connected android device ('adb devices' lists none)";
			}
			catch (Exception)
			{
				// adb itself is not launchable; AdbInstall also declares Tool("adb"), but a Device-only
				// declarer still gets an actionable reason.
				return "requires a connected android device (adb is not available to probe)";
			}
		}

		private static bool HasConnectedAdbDevice(string stdOut)
		{
			// 'adb devices' output: a "List of devices attached" header, then one "<serial>\t<state>" line
			// per device. Only state "device" is usable - unauthorized/offline devices cannot install.
			foreach (string line in (stdOut ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
			{
				string trimmed = line.Trim();
				if (trimmed.Length == 0) continue;
				if (trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;

				string[] parts = trimmed.Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2 && parts[parts.Length - 1] == "device") return true;
			}

			return false;
		}

		#endregion
	}
}

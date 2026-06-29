using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Ateo.Build
{
	/// <summary>
	/// Shared shelling-out + artifact-locating helpers for the post-build action catalog (§10). Every action runs
	/// its tool (xcodebuild/fastlane/steamcmd/adb/bundletool/xcrun) through <see cref="RunAsync"/>, which captures
	/// stdout + stderr without deadlocking (both streams drained while the process runs), and finds its input
	/// artifact in the evolving <see cref="BuildContext.ArtifactPaths"/> / <see cref="BuildContext.BuildFolder"/>
	/// via <see cref="FindArtifact"/>. Pure <see cref="System.Diagnostics.Process"/> + <see cref="System.IO"/> - no
	/// UnityEditor dependency, so it lives in the runtime-safe Data assembly and runs server-side and locally alike.
	/// </summary>
	internal static class ActionProcess
	{
		#region Public Methods

		/// <summary>
		/// Runs <paramref name="fileName"/> with <paramref name="args"/> (each argument quoted for the Windows
		/// command line), optionally in <paramref name="workingDirectory"/> and with extra <paramref name="environment"/>
		/// overlaid on the inherited environment. Captures stdout + stderr and the exit code. A launch failure throws
		/// (the executable is missing / not runnable); a non-zero exit is reported through <see cref="ToolResult"/>.
		/// </summary>
		public static Task<ToolResult> RunAsync(string fileName, IReadOnlyList<string> args,
			string workingDirectory = null, IReadOnlyDictionary<string, string> environment = null)
		{
			return Task.Run(() =>
			{
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = fileName,
					Arguments = BuildArguments(args),
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};

				if (!string.IsNullOrEmpty(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;

				if (environment != null)
				{
					foreach (KeyValuePair<string, string> pair in environment)
					{
						startInfo.Environment[pair.Key] = pair.Value ?? string.Empty;
					}
				}

				using (Process process = new Process { StartInfo = startInfo })
				{
					try
					{
						process.Start();
					}
					catch (Exception exception)
					{
						throw new Exception("Failed to launch '" + fileName + "': " + exception.Message, exception);
					}

					Task<string> readOut = process.StandardOutput.ReadToEndAsync();
					Task<string> readErr = process.StandardError.ReadToEndAsync();
					process.WaitForExit();
					string stdOut = readOut.GetAwaiter().GetResult();
					string stdErr = readErr.GetAwaiter().GetResult();

					return new ToolResult(process.ExitCode, stdOut, stdErr);
				}
			});
		}

		/// <summary>
		/// The input artifact for an action: the first path in <see cref="BuildContext.ArtifactPaths"/> ending in one
		/// of <paramref name="suffixes"/> (case-insensitive), else a filesystem entry under
		/// <see cref="BuildContext.BuildFolder"/> with that suffix (covers folder artifacts like <c>.app</c> /
		/// <c>.xcodeproj</c>). Returns null when none matches.
		/// </summary>
		public static string FindArtifact(BuildContext ctx, params string[] suffixes)
		{
			if (ctx?.ArtifactPaths != null)
			{
				foreach (string path in ctx.ArtifactPaths)
				{
					if (HasSuffix(path, suffixes)) return path;
				}
			}

			string folder = ctx?.BuildFolder;
			if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
			{
				foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
				{
					if (HasSuffix(entry, suffixes)) return entry;
				}
			}

			return null;
		}

		/// <summary>The primary built artifact - the first entry in <see cref="BuildContext.ArtifactPaths"/>, else the build folder.</summary>
		public static string PrimaryArtifact(BuildContext ctx)
		{
			if (ctx?.ArtifactPaths != null)
			{
				foreach (string path in ctx.ArtifactPaths)
				{
					if (!string.IsNullOrEmpty(path)) return path;
				}
			}

			return ctx?.BuildFolder;
		}

		/// <summary>The most-recently-written file matching <paramref name="searchPattern"/> anywhere under <paramref name="root"/>, or null.</summary>
		public static string FindNewestFile(string root, string searchPattern)
		{
			if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

			string best = null;
			DateTime bestTime = DateTime.MinValue;
			foreach (string file in Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories))
			{
				DateTime written = File.GetLastWriteTimeUtc(file);
				if (best == null || written > bestTime)
				{
					best = file;
					bestTime = written;
				}
			}

			return best;
		}

		#endregion

		#region Private Methods

		private static bool HasSuffix(string path, string[] suffixes)
		{
			if (string.IsNullOrEmpty(path) || suffixes == null) return false;

			foreach (string suffix in suffixes)
			{
				if (!string.IsNullOrEmpty(suffix) && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
			}

			return false;
		}

		/// <summary>Joins + quotes arguments for the Windows command line (handles spaces / quotes / backslashes).</summary>
		private static string BuildArguments(IReadOnlyList<string> args)
		{
			if (args == null) return string.Empty;

			StringBuilder builder = new StringBuilder();
			foreach (string arg in args)
			{
				if (builder.Length > 0) builder.Append(' ');
				builder.Append(QuoteArgument(arg));
			}

			return builder.ToString();
		}

		private static string QuoteArgument(string arg)
		{
			if (string.IsNullOrEmpty(arg)) return "\"\"";
			if (arg.IndexOfAny(new[] { ' ', '\t', '"', '\\' }) < 0) return arg;

			StringBuilder builder = new StringBuilder();
			builder.Append('"');
			int backslashes = 0;
			foreach (char c in arg)
			{
				if (c == '\\')
				{
					backslashes++;
					continue;
				}

				if (c == '"')
				{
					builder.Append('\\', backslashes * 2 + 1);
					builder.Append('"');
					backslashes = 0;
					continue;
				}

				builder.Append('\\', backslashes);
				backslashes = 0;
				builder.Append(c);
			}

			builder.Append('\\', backslashes * 2);
			builder.Append('"');
			return builder.ToString();
		}

		#endregion

		#region Nested Types

		/// <summary>Captured result of one tool invocation: exit code + stdout + stderr, with a <see cref="Tail"/> for error messages.</summary>
		internal readonly struct ToolResult
		{
			public readonly int ExitCode;
			public readonly string StdOut;
			public readonly string StdErr;

			public ToolResult(int exitCode, string stdOut, string stdErr)
			{
				ExitCode = exitCode;
				StdOut = stdOut ?? string.Empty;
				StdErr = stdErr ?? string.Empty;
			}

			/// <summary>True when the tool exited 0.</summary>
			public bool Succeeded => ExitCode == 0;

			/// <summary>The last <paramref name="lines"/> lines of combined stdout+stderr - a compact tail for failure messages.</summary>
			public string Tail(int lines)
			{
				string combined = ((StdOut ?? string.Empty) + "\n" + (StdErr ?? string.Empty)).Trim();
				if (string.IsNullOrEmpty(combined)) return "(no output)";

				string[] all = combined.Replace("\r\n", "\n").Split('\n');
				int start = Math.Max(0, all.Length - lines);
				return string.Join("\n", all, start, all.Length - start);
			}
		}

		#endregion
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The real <see cref="IOpCli"/>: shells out to the 1Password <c>op</c> CLI via
	/// <see cref="System.Diagnostics.Process"/> (no UnityEditor dependency, so it lives in the runtime-safe Data
	/// assembly and runs server-side and locally alike). The executable is resolved from the <c>OP_CLI_PATH</c>
	/// environment variable, falling back to the winget install path, then to a bare <c>op</c> on PATH. Every
	/// invocation passes <c>--account &lt;shorthand&gt;</c>; a non-zero exit throws with the captured stderr.
	/// Sign-in is NEVER attempted here - a session is assumed to exist (desktop app unlocked locally, or a
	/// non-interactive <c>op signin</c> bootstrap on the agent; see build-plugin-architecture.md §11.3).
	/// </summary>
	public sealed class OpCli : IOpCli
	{
		#region Constants

		/// <summary>Env var that overrides the <c>op.exe</c> location.</summary>
		public const string CliPathEnvVar = "OP_CLI_PATH";

		/// <summary>Default winget install location of <c>op.exe</c> (per-user, not on PATH) - anchored to the
		/// current user's local app-data root so it resolves on any machine, not just the build server.</summary>
		public static readonly string DefaultCliPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			@"Microsoft\WinGet\Packages\AgileBits.1Password.CLI_Microsoft.Winget.Source_8wekyb3d8bbwe\op.exe");

		#endregion

		#region Public Methods

		/// <summary>The resolved <c>op</c> executable: <c>OP_CLI_PATH</c> if set and present, else the winget default if present, else bare "op".</summary>
		public static string ResolveCliPath()
		{
			string fromEnv = Environment.GetEnvironmentVariable(CliPathEnvVar);
			if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
			if (File.Exists(DefaultCliPath)) return DefaultCliPath;

			return "op";
		}

		/// <summary>
		/// True if the op CLI can be located WITHOUT running it: an explicit/winget path that exists, or an
		/// <c>op</c>/<c>op.exe</c> on PATH. A cheap presence check for the panel to gate 1Password-backed
		/// projects (op is the client↔server channel; nothing works if it's missing).
		/// </summary>
		public static bool IsAvailable()
		{
			if (ResolveCliPath() != "op") return true; // a concrete path was found (File.Exists already verified)

			string path = Environment.GetEnvironmentVariable("PATH") ?? "";
			foreach (string dir in path.Split(Path.PathSeparator))
			{
				if (string.IsNullOrWhiteSpace(dir)) continue;

				try
				{
					if (File.Exists(Path.Combine(dir, "op.exe")) || File.Exists(Path.Combine(dir, "op"))) return true;
				}
				catch { /* skip a malformed PATH entry */ }
			}

			return false;
		}

		public async Task<string> ReadAsync(string opRef, string account)
		{
			OpResult result = await RunAsync(new[] { "read", opRef }, account);
			if (result.ExitCode != 0) throw OpFailure("read", result);

			// op appends a trailing newline to a field read; trim it (NOT for documents - see ReadDocumentAsync).
			return Encoding.UTF8.GetString(result.StdOut).TrimEnd('\r', '\n');
		}

		public async Task<byte[]> ReadDocumentAsync(string opRef, string account)
		{
			OpResult result = await RunAsync(new[] { "read", opRef }, account);
			if (result.ExitCode != 0) throw OpFailure("read (document)", result);

			// Document bytes are returned verbatim - no newline trimming (a file may legitimately end without one).
			return result.StdOut;
		}

		public async Task<bool> ItemFieldExistsAsync(string vault, string item, string field, string account)
		{
			OpResult result = await RunAsync(
				new[] { "item", "get", item, "--vault", vault, "--fields", "label=" + field }, account);

			// Exit 0 with non-empty output = the field is present; any error (no item / no field) = absent.
			return result.ExitCode == 0 && result.StdOut.Length > 0;
		}

		public async Task<bool> ItemExistsAsync(string vault, string item, string account)
		{
			OpResult result = await RunAsync(new[] { "item", "get", item, "--vault", vault, "--format", "json" }, account);
			return result.ExitCode == 0;
		}

		public async Task<IReadOnlyDictionary<string, string>> GetItemFieldsAsync(string vault, string item, string account)
		{
			OpResult result = await RunAsync(new[] { "item", "get", item, "--vault", vault, "--format", "json" }, account);
			if (result.ExitCode != 0) throw OpFailure("item get", result);

			OpItemJson parsed = JsonUtility.FromJson<OpItemJson>(Encoding.UTF8.GetString(result.StdOut));
			Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
			if (parsed != null && parsed.fields != null)
			{
				foreach (OpFieldJson dto in parsed.fields)
				{
					if (dto == null || string.IsNullOrEmpty(dto.label) || dto.label == "notesPlain") continue;
					if (!fields.ContainsKey(dto.label)) fields[dto.label] = dto.value ?? string.Empty;
				}
			}

			return fields;
		}

		public async Task<IReadOnlyList<string>> ListItemTitlesAsync(string vault, string account)
		{
			OpResult result = await RunAsync(new[] { "item", "list", "--vault", vault, "--format", "json" }, account);
			if (result.ExitCode != 0) throw OpFailure("item list", result);

			// 'op item list' emits a top-level JSON ARRAY, which JsonUtility cannot parse directly - wrap it in an
			// object so the same JsonUtility DTO pattern as GetItemFieldsAsync applies.
			string json = "{\"items\":" + Encoding.UTF8.GetString(result.StdOut) + "}";
			OpItemListJson parsed = JsonUtility.FromJson<OpItemListJson>(json);

			List<string> titles = new List<string>();
			if (parsed != null && parsed.items != null)
			{
				foreach (OpItemSummaryJson dto in parsed.items)
				{
					if (dto == null || string.IsNullOrEmpty(dto.title)) continue;
					titles.Add(dto.title);
				}
			}

			return titles;
		}

		public async Task CreateOrEditItemAsync(string vault, string item, string field, SecretValue value, string account)
		{
			if (value == null) throw new ArgumentNullException(nameof(value));

			if (value.IsFile)
			{
				await CreateOrEditDocumentAsync(vault, item, value.FileBytes ?? Array.Empty<byte>(), account);
				return;
			}

			// Every String field is written as plain 'text' BY DECISION (dev1): values are protected by vault
			// access, not field masking - concealed 'password' fields broke field-type consistency and confused
			// the field picker. File values stay documents (see CreateOrEditDocumentAsync).
			string assignment = field + "[text]=" + (value.StringValue ?? string.Empty);

			if (await ItemExistsAsync(vault, item, account))
			{
				OpResult edit = await RunAsync(new[] { "item", "edit", item, "--vault", vault, assignment }, account);
				if (edit.ExitCode != 0) throw OpFailure("item edit", edit);
			}
			else
			{
				OpResult create = await RunAsync(
					new[] { "item", "create", "--category", "Password", "--title", item, "--vault", vault, assignment },
					account);
				if (create.ExitCode != 0) throw OpFailure("item create", create);
			}
		}

		#endregion

		#region Private Methods

		private async Task CreateOrEditDocumentAsync(string vault, string item, byte[] bytes, string account)
		{
			// op handles documents by file path, so stage the bytes to a transient file and wipe it afterward.
			string tempFile = Path.Combine(Path.GetTempPath(), "ateo-op-" + Guid.NewGuid().ToString("N"));
			try
			{
				File.WriteAllBytes(tempFile, bytes);

				// --file-name pins the stored document's internal file name to the item name; without it, op would
				// persist the random staging name above as the document's file name.
				bool exists = await ItemExistsAsync(vault, item, account);
				OpResult result = exists
					? await RunAsync(new[] { "document", "edit", item, tempFile, "--vault", vault, "--file-name", item }, account)
					: await RunAsync(new[] { "document", "create", tempFile, "--title", item, "--vault", vault, "--file-name", item }, account);

				if (result.ExitCode != 0) throw OpFailure(exists ? "document edit" : "document create", result);
			}
			finally
			{
				try
				{
					if (File.Exists(tempFile)) File.Delete(tempFile);
				}
				catch (Exception)
				{
					// Best-effort wipe; a leftover temp file is non-fatal.
				}
			}
		}

		/// <summary>
		/// Runs <c>op &lt;args&gt; --account &lt;shorthand&gt;</c> and captures raw stdout + stderr without
		/// deadlocking (both streams are drained on background tasks while the process runs).
		/// </summary>
		private static Task<OpResult> RunAsync(IReadOnlyList<string> args, string account)
		{
			return Task.Run(() =>
			{
				List<string> allArgs = new List<string>(args);
				if (!string.IsNullOrEmpty(account))
				{
					allArgs.Add("--account");
					allArgs.Add(account);
				}

				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = ResolveCliPath(),
					Arguments = BuildArguments(allArgs),
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};

				using (Process process = new Process { StartInfo = startInfo })
				{
					try
					{
						process.Start();
					}
					catch (Exception exception)
					{
						throw new Exception("Failed to launch op CLI ('" + startInfo.FileName + "'): " + exception.Message, exception);
					}

					// NOTE: do NOT redirect/close stdin here - 'op item create' treats a redirected stdin as a piped
					// JSON template and fails ("invalid JSON in piped input"). op fails fast without a session anyway,
					// and the WaitForExit timeout below is the hang backstop.
					// Drain stdout as raw bytes (documents may be binary) and stderr as text, concurrently.
					MemoryStream stdOut = new MemoryStream();
					Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdOut);
					Task<string> readErr = process.StandardError.ReadToEndAsync();

					if (!process.WaitForExit(30000))
					{
						try { process.Kill(); } catch (Exception) { /* best effort */ }
						return new OpResult(-1, Array.Empty<byte>(), "op CLI did not finish within 30s and was killed.");
					}

					copyOut.GetAwaiter().GetResult();
					string stdErr = readErr.GetAwaiter().GetResult();

					return new OpResult(process.ExitCode, stdOut.ToArray(), stdErr);
				}
			});
		}

		private static Exception OpFailure(string operation, OpResult result)
		{
			string stderr = string.IsNullOrWhiteSpace(result.StdErr) ? "(no stderr)" : result.StdErr.Trim();
			return new Exception("op " + operation + " failed (exit " + result.ExitCode + "): " + stderr);
		}

		/// <summary>Quotes each argument for the Windows command line (handles spaces, e.g. the "Build Server" vault).</summary>
		private static string BuildArguments(IReadOnlyList<string> args)
		{
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

		[Serializable] private sealed class OpItemJson { public OpFieldJson[] fields; }
		[Serializable] private sealed class OpFieldJson { public string label; public string value; }

		// 'op item list' array entries, wrapped for JsonUtility (see ListItemTitlesAsync) - only the title matters.
		[Serializable] private sealed class OpItemListJson { public OpItemSummaryJson[] items; }
		[Serializable] private sealed class OpItemSummaryJson { public string title; }

		/// <summary>Captured result of one <c>op</c> invocation.</summary>
		private readonly struct OpResult
		{
			public readonly int ExitCode;
			public readonly byte[] StdOut;
			public readonly string StdErr;

			public OpResult(int exitCode, byte[] stdOut, string stdErr)
			{
				ExitCode = exitCode;
				StdOut = stdOut;
				StdErr = stdErr;
			}
		}

		#endregion
	}
}

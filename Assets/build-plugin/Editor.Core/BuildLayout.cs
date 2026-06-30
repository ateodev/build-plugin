using System;
using System.IO;
using System.Text;

namespace Ateo.Build
{
	/// <summary>
	/// The single on-disk build layout (§12.2). One folder per build <i>identity</i> under a gitignored
	/// <c>&lt;root&gt;/Builds/</c>, shared by <b>local output</b> and <b>unarchived downloads</b> so a downloaded
	/// build is byte-identical on disk to a locally-produced one and the two correlate to a single history row:
	/// <list type="bullet">
	/// <item><c>Builds/&lt;definition&gt;/&lt;version&gt;_&lt;buildNumber&gt;/</c> for iOS / Android (store build number is part of identity)</item>
	/// <item><c>Builds/&lt;definition&gt;/&lt;version&gt;/</c> for everything else</item>
	/// </list>
	/// Both <see cref="BuildRunner"/> (local builds) and the Build Panel (downloads) resolve the destination through
	/// here, so the path can never drift between the two producers.
	/// </summary>
	public static class BuildLayout
	{
		public const string BuildsFolder = "Builds";

		/// <summary>iOS / Android fold the store build number into identity; other targets have none.</summary>
		public static bool HasBuildNumber(BuildDefinition definition)
		{
			return definition != null &&
				(definition.Platform == BuildPlatform.Android || definition.Platform == BuildPlatform.iOS);
		}

		/// <summary>
		/// The identity folder name for a build: <c>&lt;version&gt;</c>, or <c>&lt;version&gt;_&lt;buildNumber&gt;</c>
		/// for iOS / Android, optionally suffixed with a free-text build name (<c>&lt;version&gt;-&lt;buildName&gt;</c>,
		/// e.g. <c>1.0-test-locomotion-4</c>) so a dev can take many builds at the same version without overwriting.
		/// The build name uses <c>-</c> (distinct from the <c>_</c> that joins the mobile build number) and is
		/// sanitized via <see cref="SanitizeName"/>. A blank version yields <c>unknown</c> so a path is always well-formed.
		/// </summary>
		public static string FolderName(BuildDefinition definition, string version, int buildNumber, string buildName = null)
		{
			string sanitized = Sanitize(string.IsNullOrEmpty(version) ? "unknown" : version);
			string baseName = HasBuildNumber(definition) ? sanitized + "_" + buildNumber : sanitized;
			string name = SanitizeName(buildName);
			return string.IsNullOrEmpty(name) ? baseName : baseName + "-" + name;
		}

		/// <summary>The absolute build directory under <paramref name="root"/> for the given identity.</summary>
		public static string BuildDirectory(string root, BuildDefinition definition, string version, int buildNumber, string buildName = null)
		{
			return Path.Combine(root, BuildsFolder, definition.DefinitionName, FolderName(definition, version, buildNumber, buildName));
		}

		/// <summary>
		/// Sanitize a free-text build name into a filesystem-safe identity suffix (§12.2): drop every character
		/// illegal in a file name, collapse internal whitespace runs to a single <c>-</c>, coalesce repeated dashes,
		/// and trim stray separators. Blank in → empty out (no suffix). Shared by the panel (the folder it expects)
		/// and <see cref="BuildRunner"/> (the folder it writes), so both sides derive the exact same path from the
		/// same raw input.
		/// </summary>
		public static string SanitizeName(string value)
		{
			if (string.IsNullOrWhiteSpace(value)) return "";

			char[] illegal = Path.GetInvalidFileNameChars();
			StringBuilder builder = new StringBuilder(value.Length);
			foreach (char c in value)
			{
				if (Array.IndexOf(illegal, c) >= 0) continue;        // strip filesystem-illegal chars
				builder.Append(char.IsWhiteSpace(c) ? '-' : c);       // whitespace -> '-'
			}

			string result = builder.ToString();
			while (result.Contains("--")) result = result.Replace("--", "-");
			return result.Trim('-', '.', ' ');
		}

		/// <summary>The <c>Builds/&lt;definition&gt;/</c> directory that holds all of a definition's build folders.</summary>
		public static string DefinitionDirectory(string root, BuildDefinition definition)
		{
			return Path.Combine(root, BuildsFolder, definition.DefinitionName);
		}

		private static string Sanitize(string value)
		{
			foreach (char invalid in Path.GetInvalidFileNameChars())
			{
				value = value.Replace(invalid, '_');
			}

			return value.Trim();
		}
	}
}

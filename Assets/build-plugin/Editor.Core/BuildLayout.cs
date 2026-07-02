using System;
using System.IO;
using System.Text;

namespace Ateo.Build
{
	/// <summary>
	/// The single on-disk build layout (§12.2). One folder per build <i>identity</i> under a gitignored
	/// <c>&lt;root&gt;/Builds/</c>, shared by <b>local output</b> and <b>unarchived downloads</b> so a downloaded
	/// build is byte-identical on disk to a locally-produced one and the two correlate to a single history row.
	/// The identity folder is <c>&lt;version&gt;[_&lt;buildNumber&gt;][-&lt;buildName&gt;]</c>; what sits ABOVE
	/// it follows the platform-exactly-once rule (definition names are bare, so the path must state the
	/// platform exactly once):
	/// <list type="bullet">
	/// <item>LOCAL (project root, which names no platform): <c>Builds/&lt;token&gt;/&lt;name&gt;/&lt;identity&gt;/</c> - <see cref="DefinitionDirectory"/></item>
	/// <item>SERVER (checkout root, which already names the platform via <c>.../&lt;target&gt;</c>): <c>Builds/&lt;name&gt;/&lt;identity&gt;/</c> - <see cref="ServerDefinitionDirectory"/></item>
	/// </list>
	/// Both <see cref="BuildRunner"/> (build output) and the Build Panel (downloads) resolve destinations through
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

		/// <summary>
		/// The LOCAL layout path of one build's identity folder RELATIVE to the project root
		/// (<c>Builds/&lt;token&gt;/&lt;name&gt;/&lt;identity&gt;/</c>, forward slashes, trailing slash) - e.g.
		/// <c>Builds/Linux/Test/1.0_4-my-cool-build/</c>. Composed from the exact same
		/// <see cref="DefinitionDirectory"/> + <see cref="FolderName"/> pieces the build output and downloads
		/// resolve through, so a UI preview of "where the next build lands" can never drift from the real path.
		/// </summary>
		public static string LocalRelativePath(BuildDefinition definition, string version, int buildNumber, string buildName = null)
		{
			string path = Path.Combine(DefinitionDirectory("", definition), FolderName(definition, version, buildNumber, buildName));
			return path.Replace('\\', '/') + "/";
		}

		/// <summary>
		/// LOCAL layout: the <c>Builds/&lt;token&gt;/&lt;name&gt;/</c> directory (under the Unity project root)
		/// that holds all of a definition's build folders. A project root names no platform, so the token
		/// segment states it here - and keeps same-named definitions on different platforms apart.
		/// </summary>
		public static string DefinitionDirectory(string root, BuildDefinition definition)
		{
			return Path.Combine(root, BuildsFolder, definition.Platform.ToServerToken(), definition.DefinitionName);
		}

		/// <summary>
		/// SERVER layout: the <c>Builds/&lt;name&gt;/</c> directory (under the TeamCity checkout root) that
		/// holds all of a definition's build folders. The checkout dir already carries the platform
		/// (<c>&lt;team&gt;/&lt;project&gt;/&lt;target&gt;</c>), so a token segment here would state it twice.
		/// </summary>
		public static string ServerDefinitionDirectory(string root, BuildDefinition definition)
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

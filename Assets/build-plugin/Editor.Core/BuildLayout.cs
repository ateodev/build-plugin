using System.IO;

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
		/// for iOS / Android. A blank version yields <c>unknown</c> so a path is always well-formed.
		/// </summary>
		public static string FolderName(BuildDefinition definition, string version, int buildNumber)
		{
			string sanitized = Sanitize(string.IsNullOrEmpty(version) ? "unknown" : version);
			return HasBuildNumber(definition) ? sanitized + "_" + buildNumber : sanitized;
		}

		/// <summary>The absolute build directory under <paramref name="root"/> for the given identity.</summary>
		public static string BuildDirectory(string root, BuildDefinition definition, string version, int buildNumber)
		{
			return Path.Combine(root, BuildsFolder, definition.DefinitionName, FolderName(definition, version, buildNumber));
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

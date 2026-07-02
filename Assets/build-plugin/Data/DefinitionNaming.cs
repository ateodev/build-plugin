using System;
using System.Text;

namespace Ateo.Build
{
	/// <summary>
	/// Naming/location authority for build-definition assets. A definition's stored name is the BARE user
	/// name (<c>Test</c>) - it never contains the platform. The platform is expressed by WHERE the asset
	/// lives (<c>Assets/BuildConfigs/&lt;token&gt;/&lt;name&gt;.asset</c>, see <see cref="FolderFor"/>), and
	/// per-platform name uniqueness is enforced by the filesystem of that folder. Machine identity is the
	/// asset's .meta GUID. Standalone contexts that need a self-describing label (Slack, TeamCity history,
	/// the <c>unitybuild.definition</c> parameter) compose the display label <c>&lt;token&gt; - &lt;name&gt;</c>
	/// ON DEMAND via <see cref="ComposeDisplayLabel"/> - it is never stored, so name and platform can never
	/// drift apart in data. Pure string logic - zero UI/editor knowledge lives here.
	/// </summary>
	public static class DefinitionNaming
	{
		#region Fields

		/// <summary>Root project folder for all definition assets; each platform owns one subfolder
		/// (<see cref="FolderFor"/>).</summary>
		public const string RootFolder = "Assets/BuildConfigs";

		/// <summary>The token/name separator of the composed DISPLAY label (space-hyphen-space). Public so
		/// the wizard's preview and the CLI's composed-input fallback agree on the exact form.</summary>
		public const string Separator = " - ";

		// The Windows-reserved set: the bare name IS the asset file name and a Builds/ path segment on every
		// build host, so the strictest platform's rules apply everywhere.
		private static readonly char[] IllegalCharacters = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

		#endregion

		#region Public Methods

		/// <summary>
		/// The on-demand display label for standalone, human-facing contexts (Slack, TeamCity history, the
		/// <c>unitybuild.definition</c> parameter): <c>&lt;token&gt; - &lt;name&gt;</c>, e.g. <c>Linux - Test</c>.
		/// Composed at the point of use and NEVER stored - the stored name stays bare.
		/// </summary>
		public static string ComposeDisplayLabel(string platformToken, string name)
		{
			return platformToken + Separator + name;
		}

		/// <summary>
		/// The project folder that holds one platform's definitions: <c>Assets/BuildConfigs/&lt;token&gt;</c>.
		/// The folder IS the platform statement on disk, and its file listing IS the per-platform uniqueness
		/// scope (the wizard checks file-exists here instead of keeping a registry).
		/// </summary>
		public static string FolderFor(string platformToken)
		{
			return RootFolder + "/" + platformToken;
		}

		/// <summary>True for characters a definition name may never contain (filesystem-reserved). Exposed so
		/// the wizard's live input filter blocks exactly the set this class strips - one definition of "illegal".</summary>
		public static bool IsIllegalCharacter(char character)
		{
			return Array.IndexOf(IllegalCharacters, character) >= 0;
		}

		/// <summary>
		/// Normalize a typed name: drop filesystem-illegal + control characters, collapse repeated spaces, trim.
		/// The full pass exists for input the live keystroke filter never saw (paste, programmatic values);
		/// empty output means "nothing valid was typed" and callers must block creation on it.
		/// </summary>
		public static string SanitizeName(string value)
		{
			if (string.IsNullOrEmpty(value)) return "";

			StringBuilder builder = new StringBuilder(value.Length);
			bool lastWasSpace = false;
			foreach (char character in value)
			{
				if (IsIllegalCharacter(character) || char.IsControl(character)) continue;

				bool isSpace = character == ' ';
				if (isSpace && lastWasSpace) continue;

				builder.Append(character);
				lastWasSpace = isSpace;
			}

			return builder.ToString().Trim();
		}

		#endregion
	}
}

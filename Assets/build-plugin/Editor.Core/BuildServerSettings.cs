using UnityEditor;

namespace Ateo.Build
{
	/// <summary>
	/// Per-user build-server settings, stored in <see cref="EditorPrefs"/> (machine-local, never committed).
	/// The access token is the user's own permission-scoped TeamCity token. The base URL comes from the
	/// committed <see cref="ProjectConfig"/>; only secrets/local prefs live here.
	/// </summary>
	public static class BuildServerSettings
	{
		#region Fields

		private const string TokenPref = "Ateo.Build.Token";

		#endregion

		#region Properties

		/// <summary>The user's TeamCity access token (Bearer). Never an admin token, never committed.</summary>
		public static string Token
		{
			get => EditorPrefs.GetString(TokenPref, "");
			set => EditorPrefs.SetString(TokenPref, value ?? "");
		}

		#endregion
	}
}

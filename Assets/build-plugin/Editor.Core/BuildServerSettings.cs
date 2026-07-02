using UnityEditor;

namespace Ateo.Build
{
	/// <summary>
	/// Per-user build-server settings, stored in <see cref="EditorPrefs"/> (machine-local, never committed).
	/// This is where ENVIRONMENT facts live - values that depend on the machine, not the project: the user's
	/// permission-scoped TeamCity token and the server base URL (localhost on the build-server box, the
	/// public host on a dev machine). Machine-independent project facts belong in <see cref="ProjectConfig"/>.
	/// </summary>
	public static class BuildServerSettings
	{
		#region Fields

		private const string TokenPref = "Ateo.Build.Token";
		private const string ServerBaseUrlPref = "Ateo.Build.ServerBaseUrl";

		// Canonical server as the unset default: the panel must work out-of-the-box on a dev machine,
		// where an empty URL would just be a dead prompt.
		private const string DefaultServerBaseUrl = "https://build.ateonet.work";

		#endregion

		#region Properties

		/// <summary>The user's TeamCity access token (Bearer). Never an admin token, never committed.</summary>
		public static string Token
		{
			get => EditorPrefs.GetString(TokenPref, "");
			set => EditorPrefs.SetString(TokenPref, value ?? "");
		}

		/// <summary>
		/// TeamCity base URL the plugin talks to, e.g. <c>https://build.ateonet.work</c>. Per-machine because
		/// the right value differs by environment (e.g. <c>http://localhost:8111</c> on the build-server box).
		/// </summary>
		public static string ServerBaseUrl
		{
			get
			{
				string value = EditorPrefs.GetString(ServerBaseUrlPref, "");
				return string.IsNullOrEmpty(value) ? DefaultServerBaseUrl : value;
			}
			set => EditorPrefs.SetString(ServerBaseUrlPref, value ?? "");
		}

		#endregion
	}
}

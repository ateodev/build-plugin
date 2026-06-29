using UnityEngine;

namespace Ateo.Build
{
	/// <summary>How this project's source is version-controlled (documentation + the in-editor panel's use).</summary>
	public enum VcsKind
	{
		Git,
		Plastic
	}

	/// <summary>
	/// Project-wide build configuration: settings shared by every <see cref="BuildDefinition"/> in this
	/// project. Committed under Assets/BuildConfigs/. Holds NON-SECRET, project-level facts only. The
	/// authoritative repo URL + credentials live agent-side on the build server (fixed at onboarding, keyed
	/// by the game token); the values here are for the plugin's local use, the panel, and human reference.
	/// </summary>
	[CreateAssetMenu(menuName = "Build/Project Config", fileName = "BuildProjectConfig", order = 1)]
	public sealed class ProjectConfig : ScriptableObject
	{
		#region Fields

		[SerializeField, Tooltip("Game token - the JOIN KEY. Must equal the build server's agent-side record " +
			"token; the server resolves repo, credentials, signing secrets, license and checkout dir from it.")]
		private string _gameToken;

		[SerializeField, Tooltip("How the project's source is version-controlled (reference; the authoritative copy is agent-side).")]
		private VcsKind _vcs = VcsKind.Git;

		[SerializeField, Tooltip("Repo URL (git remote or Plastic repo spec). Documentation + local use only.")]
		private string _repoUrl;

		[SerializeField, Tooltip("Trust-boundary team this project belongs to (resolves which TeamCity subtree/executors).")]
		private string _teamId;

		[SerializeField, Tooltip("TeamCity base URL the in-editor panel talks to, e.g. https://build.ateonet.work")]
		private string _serverBaseUrl = "https://build.ateonet.work";

		[SerializeField, Tooltip("Pinned Unity version. Empty = read from ProjectSettings/ProjectVersion.txt.")]
		private string _unityVersion;

		[SerializeField, Tooltip("Slack channel id this project's build notifications post to.")]
		private string _slackChannelId;

		[SerializeField, Tooltip("Name of the Unity license to build with (matched agent-side to <name>.ulf). Default \"ateo\".")]
		private string _unityLicenseName = "ateo";

		[SerializeField, Tooltip("Name of the reusable checkout credential (in the credential registry) the server uses for this repo.")]
		private string _vcsCredentialName;

		[SerializeField, Tooltip("Secret-provider scheme that resolves this project's references (e.g. \"op\" for 1Password). Default \"op\".")]
		private string _secretProviderScheme = "op";

		[SerializeField, Tooltip("Non-secret provider config (e.g. 1Password vault/account name, or the OpenBao server URL).")]
		private string _secretProviderConfig;

		#endregion

		#region Properties

		public string GameToken => _gameToken;
		public VcsKind Vcs => _vcs;
		public string RepoUrl => _repoUrl;
		public string TeamId => _teamId;
		public string ServerBaseUrl => _serverBaseUrl;
		public string UnityVersion => _unityVersion;
		public string SlackChannelId => _slackChannelId;
		public string UnityLicenseName => _unityLicenseName;
		public string VcsCredentialName => _vcsCredentialName;
		public string SecretProviderScheme => _secretProviderScheme;
		public string SecretProviderConfig => _secretProviderConfig;

		#endregion
	}
}

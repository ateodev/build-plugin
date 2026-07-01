using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

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
	/// by the project key); the values here are for the plugin's local use, the panel, and human reference.
	/// </summary>
	[CreateAssetMenu(menuName = "Build/Project Config", fileName = "BuildProjectConfig", order = 1)]
	public sealed class ProjectConfig : ScriptableObject
	{
		#region Fields

		[SerializeField, FormerlySerializedAs("_gameToken"), Tooltip("Project key - the JOIN KEY (lowercase, a-z 0-9 and '-'). " +
			"The server resolves repo, credentials, signing secrets, license and checkout dir from it via the provider.")]
		private string _projectKey;

		[SerializeField, Tooltip("How the project's source is version-controlled (reference; the authoritative copy is agent-side).")]
		private VcsKind _vcs = VcsKind.Git;

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

		[SerializeField, Tooltip("Values-free secret registry: maps each code-declared logical key to a scheme-tagged provider " +
			"reference (never a value). Reconciled by the wizard against the actions' declared RequiredSecrets.")]
		private List<SecretDeclaration> _secretRegistry = new();

		#endregion

		#region Properties

		public string ProjectKey => _projectKey;
		public VcsKind Vcs => _vcs;
		public string TeamId => _teamId;
		public string ServerBaseUrl => _serverBaseUrl;
		public string UnityVersion => _unityVersion;
		public string SlackChannelId => _slackChannelId;
		public string UnityLicenseName => _unityLicenseName;

		/// <summary>The committed values-free secret registry (logical key -> scheme-tagged reference; never values).</summary>
		public IReadOnlyList<SecretDeclaration> SecretRegistry => _secretRegistry;

		#endregion

		#region Public Methods

		/// <summary>
		/// Looks up the registry entry for a code-declared logical key (the join from a
		/// <see cref="SecretRequirement.Key"/> to its committed provider reference). Returns null when the key is
		/// declared in code but not yet registered - the caller turns that into an actionable failure.
		/// </summary>
		public SecretDeclaration FindSecret(string logicalKey)
		{
			if (string.IsNullOrEmpty(logicalKey) || _secretRegistry == null) return null;

			foreach (SecretDeclaration declaration in _secretRegistry)
			{
				if (declaration != null && declaration.LogicalKey == logicalKey) return declaration;
			}

			return null;
		}

		#endregion
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ateo.Build
{
	/// <summary>
	/// The first-run project-setup wizard (§13.2), a floating <see cref="OdinEditorWindow"/> the Build Panel
	/// offers when no <see cref="ProjectConfig"/> asset exists. Heavy on auto-detection (game slug from the
	/// product name, repo URL from <c>git remote</c>, Unity version from the editor), it gathers the per-game
	/// onboarding facts - slug, team, server URL, secrets-provider config, VCS + a reusable checkout credential
	/// (the credential registry §13.3), Slack channel, Unity license (§13.4) - then <c>Validate</c>s the server
	/// connection + provider auth and writes <c>Assets/BuildConfigs/ProjectConfig.asset</c>. Afterward, editing
	/// lives in the Settings view.
	///
	/// Live integrations (TeamCity, the <c>op</c>/<c>cm</c>/<c>ssh-keygen</c> CLIs) DEGRADE GRACEFULLY: a missing
	/// tool / signed-out provider logs and continues; the asset is still creatable.
	/// </summary>
	public sealed class ProjectSetupWizard : OdinEditorWindow
	{
		#region Open

		/// <summary>Open the wizard as a floating utility window, bound to the panel that will pick up the new ProjectConfig.</summary>
		public static void Open(BuildPanel owner)
		{
			ProjectSetupWizard window = CreateInstance<ProjectSetupWizard>();
			window.titleContent = new GUIContent("Project Setup");
			window.minSize = new Vector2(560, 620);
			window._owner = owner;
			window.AutoDetect();
			window.ShowUtility();
		}

		#endregion

		#region Fields

		[NonSerialized] private BuildPanel _owner;
		[NonSerialized] private string _validation = "";
		[NonSerialized] private string _publicKey = "";
		[NonSerialized] private List<string> _licenses = new List<string> { "ateo" };

		// --- Identity -----------------------------------------------------------------------------------------

		[BoxGroup("Project"), PropertyOrder(0)]
		[InfoBox("Give this slug to your build admin - the build server's agent-side record must use the same string " +
			"(it's the join key that resolves repo, credentials, signing and license).", InfoMessageType.Info)]
		[SerializeField, LabelText("Game slug"), Tooltip("Unique game token. Suggested from the product name; editable.")]
		private string _gameSlug = "";

		[BoxGroup("Project"), PropertyOrder(1)]
		[SerializeField, Tooltip("Trust-boundary team this project belongs to (resolves the TeamCity subtree/executors).")]
		private string _teamId = "";

		[BoxGroup("Project"), PropertyOrder(2)]
		[SerializeField, LabelText("Server URL"), Tooltip("TeamCity base URL the panel talks to.")]
		private string _serverBaseUrl = "https://build.ateonet.work";

		[BoxGroup("Project"), PropertyOrder(3)]
		[SerializeField, Tooltip("Slack channel id this project's build notifications post to.")]
		private string _slackChannelId = "";

		// --- Secrets provider ---------------------------------------------------------------------------------

		[BoxGroup("Secrets provider"), PropertyOrder(10)]
		[SerializeField, LabelText("Scheme"), Tooltip("Secret-provider scheme that resolves this project's references (default \"op\" for 1Password).")]
		private string _secretProviderScheme = "op";

		[BoxGroup("Secrets provider"), PropertyOrder(11)]
		[SerializeField, LabelText("Vault / config"), Tooltip("Non-secret provider config (1Password vault name, or the OpenBao server URL). Default \"Build Server\".")]
		private string _secretProviderConfig = "Build Server";

		[BoxGroup("Secrets provider"), PropertyOrder(12)]
		[SerializeField, LabelText("Account"), Tooltip("1Password account shorthand used for validation / license lookup (environment-local, not committed). Default \"ateoteam\".")]
		private string _secretProviderAccount = "ateoteam";

		// --- VCS ----------------------------------------------------------------------------------------------

		[BoxGroup("Version control"), PropertyOrder(20)]
		[SerializeField, LabelText("VCS type")]
		private VcsKind _vcs = VcsKind.Git;

		[BoxGroup("Version control"), PropertyOrder(21)]
		[SerializeField, LabelText("Repo URL"), Tooltip("git remote or Plastic repo spec. Auto-detected from 'git remote get-url origin' when available.")]
		private string _repoUrl = "";

		[BoxGroup("Version control/Checkout credential"), PropertyOrder(22), EnumToggleButtons, HideLabel]
		[SerializeField, Tooltip("Reuse an existing named credential from the registry, or add a new one.")]
		private CredentialMode _credentialMode = CredentialMode.SelectExisting;

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(IsSelectExisting)), PropertyOrder(23)]
		[SerializeField, LabelText("Credential name"), Tooltip("Name of an existing reusable checkout credential in the vault registry.")]
		private string _credentialName = "";

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(IsAddNew)), PropertyOrder(24)]
		[InfoBox("Name every credential by convention <host>-<account/scope> - e.g. team-github, uvcs-ci-bot, " +
			"clientx-deploy. The credential is reusable: one entry serves every repo it can reach.", InfoMessageType.None)]
		[SerializeField, LabelText("New credential name")]
		private string _newCredentialName = "";

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(IsAddNew)), PropertyOrder(25)]
		[SerializeField, LabelText("Type")]
		private CredentialType _credentialType = CredentialType.GitDeployKey;

		// Git key types.
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowGitKey)), PropertyOrder(26)]
		[SerializeField, ReadOnly, LabelText("Public key"), TextArea(2, 4),
			Tooltip("Generated public key - add it to your repo host as a deploy key (deploy key = least privilege).")]
		private string _publicKeyDisplay = "";

		// Plastic user/pass.
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowPlastic)), PropertyOrder(27)]
		[SerializeField, LabelText("Username")] private string _plasticUser = "";
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowPlastic)), PropertyOrder(28)]
		[SerializeField, LabelText("Password")] private string _plasticPassword = "";

		// UVCS PAT.
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcs)), PropertyOrder(29)]
		[InfoBox("UVCS checkout needs a 'cm accesstoken' PAT from a BOT Unity account (~180-day expiry; UGS service " +
			"accounts do NOT work). 'Generate now' mints it after confirming the authed account; 'Paste' shows the " +
			"commands to run as the bot.", InfoMessageType.None)]
		[SerializeField, LabelText("PAT source")] private PatMode _patMode = PatMode.Paste;
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcs)), PropertyOrder(30)]
		[SerializeField, LabelText("Org (org@cloud)")] private string _uvcsOrg = "";
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcsPaste)), PropertyOrder(31)]
		[SerializeField, LabelText("Bot username")] private string _uvcsBotUser = "";
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcsPaste)), PropertyOrder(32)]
		[SerializeField, LabelText("PAT"), Tooltip("Paste the PAT minted by the bot ('cm accesstoken create … <org>@cloud').")]
		private string _uvcsPat = "";

		// --- Unity --------------------------------------------------------------------------------------------

		[BoxGroup("Unity"), PropertyOrder(40)]
		[ValueDropdown(nameof(LicenseOptions), AppendNextDrawer = true)]
		[SerializeField, LabelText("License"), Tooltip("Unity license name (matched agent-side to <name>.ulf). Read from the unity-licenses 1Password item when available; default \"ateo\".")]
		private string _unityLicenseName = "ateo";

		[BoxGroup("Unity"), PropertyOrder(41)]
		[SerializeField, LabelText("Unity version"), Tooltip("Default Unity version. Auto-detected from the running editor.")]
		private string _unityVersion = "";

		#endregion

		#region Predicates (Odin ShowIf)

		private bool IsSelectExisting => _credentialMode == CredentialMode.SelectExisting;
		private bool IsAddNew => _credentialMode == CredentialMode.AddNew;
		private bool ShowGitKey => IsAddNew && (_credentialType == CredentialType.GitDeployKey || _credentialType == CredentialType.GitSshKey);
		private bool ShowPlastic => IsAddNew && _credentialType == CredentialType.PlasticUserpass;
		private bool ShowUvcs => IsAddNew && _credentialType == CredentialType.UvcsPat;
		private bool ShowUvcsGenerate => ShowUvcs && _patMode == PatMode.GenerateNow;
		private bool ShowUvcsPaste => ShowUvcs && _patMode == PatMode.Paste;

		#endregion

		#region Credential buttons

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowGitKey)), PropertyOrder(33)]
		[Button("Generate SSH keypair")]
		private void GenerateSshKeypair()
		{
			string name = string.IsNullOrEmpty(_newCredentialName) ? "checkout-key" : _newCredentialName;
			string temp = Path.Combine(Path.GetTempPath(), "ateo-" + Guid.NewGuid().ToString("N"));

			try
			{
				int exit = WizardShell.Run("ssh-keygen", "-t ed25519 -N \"\" -C " + Quote(name) + " -f " + Quote(temp),
					BuildPanel.ProjectRoot, out _, out string stderr);
				if (exit != 0)
				{
					_validation = "ssh-keygen failed (" + exit + "): " + stderr + " - degrade: add a key manually.";
					return;
				}

				_publicKey = File.ReadAllText(temp + ".pub");
				_publicKeyDisplay = _publicKey;

				byte[] privateKey = File.ReadAllBytes(temp);
				Provision(name, "private_key", SecretValue.OfFile(privateKey), SecretKind.File, name + " checkout private key");
				_validation = "Keypair generated. Add the public key above to your repo host as a deploy key.";
			}
			catch (Exception exception)
			{
				_validation = "ssh-keygen unavailable (" + exception.Message + ") - degrade: add a key manually (TODO).";
				Debug.LogWarning("[Project Setup] " + _validation);
			}
			finally
			{
				TryDelete(temp);
				TryDelete(temp + ".pub");
			}
		}

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcsGenerate)), PropertyOrder(34)]
		[Button("Show authed account")]
		private void ShowAuthedAccount()
		{
			try
			{
				WizardShell.Run("cm", "whoami", BuildPanel.ProjectRoot, out string who, out string stderr);
				_validation = string.IsNullOrWhiteSpace(who) ? ("cm whoami: " + stderr) : ("Authed as: " + who.Trim());
			}
			catch (Exception exception)
			{
				_validation = "cm unavailable (" + exception.Message + ") - degrade: paste a PAT instead.";
				Debug.LogWarning("[Project Setup] " + _validation);
			}
		}

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcsGenerate)), PropertyOrder(35)]
		[Button("Generate PAT (cm accesstoken create)")]
		private void GenerateUvcsPat()
		{
			if (string.IsNullOrWhiteSpace(_uvcsOrg))
			{
				_validation = "Enter the org (org@cloud) before generating a PAT.";
				return;
			}

			try
			{
				int exit = WizardShell.Run("cm", "accesstoken create " + Quote(_uvcsOrg + "@cloud"),
					BuildPanel.ProjectRoot, out string token, out string stderr);
				if (exit != 0 || string.IsNullOrWhiteSpace(token))
				{
					_validation = "cm accesstoken create failed (" + exit + "): " + stderr + " - degrade: paste a PAT.";
					return;
				}

				string name = string.IsNullOrEmpty(_newCredentialName) ? "uvcs-ci-bot" : _newCredentialName;
				Provision(name, "token", SecretValue.OfString(token.Trim()), SecretKind.String, name + " UVCS PAT");
				_validation = "PAT minted and stored as a secret.";
			}
			catch (Exception exception)
			{
				_validation = "cm unavailable (" + exception.Message + ") - degrade: paste a PAT (TODO).";
				Debug.LogWarning("[Project Setup] " + _validation);
			}
		}

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowUvcsPaste)), PropertyOrder(36)]
		[InfoBox("Run as the bot, then paste the result above:\n" +
			"  cm accesstoken create <org>@cloud\n" +
			"On the agent the PAT is consumed via: cm profile create --token … --workingmode=SSOWorkingMode", InfoMessageType.None)]
		[ShowInInspector, DisplayAsString, HideLabel]
		private string PasteHint => " ";

		#endregion

		#region Validate / Create

		[PropertyOrder(50), PropertySpace(8), ButtonGroup("Actions")]
		[Button("Validate", ButtonSizes.Large)]
		private void Validate()
		{
			ValidateAsync();
		}

		[PropertyOrder(51), ButtonGroup("Actions")]
		[Button("Create ProjectConfig", ButtonSizes.Large)]
		private void Create()
		{
			if (string.IsNullOrWhiteSpace(_gameSlug))
			{
				_validation = "Enter a game slug.";
				return;
			}

			EnsureBuildConfigsFolder();
			const string assetPath = "Assets/BuildConfigs/ProjectConfig.asset";
			if (AssetDatabase.LoadAssetAtPath<ProjectConfig>(assetPath) != null)
			{
				_validation = "A ProjectConfig already exists at " + assetPath + ".";
				return;
			}

			ProvisionPendingCredential();
			WriteVcsRecord();

			ProjectConfig project = CreateInstance<ProjectConfig>();
			ApplyFields(project);

			AssetDatabase.CreateAsset(project, assetPath);
			EditorUtility.SetDirty(project);
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();

			if (_owner != null) _owner.RefreshProject();

			Debug.Log("[Project Setup] Created " + assetPath + " for slug '" + _gameSlug + "'.");
			Close();
		}

		[ShowInInspector, DisplayAsString(false), HideLabel, PropertyOrder(52)]
		[ShowIf("@!string.IsNullOrEmpty(this._validation)")]
		private string Validation => _validation;

		private async void ValidateAsync()
		{
			_validation = "Validating...";
			Repaint();

			string server = await TestServerAsync();
			string provider = await TestProviderAsync();
			_validation = server + "\n" + provider;
			Repaint();
		}

		private async Task<string> TestServerAsync()
		{
			string token = BuildServerSettings.Token;
			if (string.IsNullOrEmpty(token)) return "Server: no access token set (Settings) - skipped.";

			try
			{
				using (TeamCityClient client = new TeamCityClient(_serverBaseUrl, token))
				{
					Dictionary<string, string> executors = await client.DiscoverExecutorsAsync();
					return "Server: connected. " + executors.Count + " executor(s) visible.";
				}
			}
			catch (Exception exception)
			{
				return "Server: NOT reachable (" + exception.Message + ").";
			}
		}

		private async Task<string> TestProviderAsync()
		{
			if (_secretProviderScheme != OnePasswordProvider.SchemeName) return "Provider: scheme '" + _secretProviderScheme + "' - no live check.";

			try
			{
				ISecretProvider provider = SecretProviders.Resolve(_secretProviderScheme, _secretProviderConfig, _secretProviderAccount);
				// A harmless presence probe; a signed-out provider throws and is reported, not fatal.
				await provider.ExistsAsync(new SecretRef(
					OnePasswordProvider.SchemeName + "://" + _secretProviderConfig + "/unity-licenses/" + _unityLicenseName));
				return "Provider: 1Password reachable (vault '" + _secretProviderConfig + "').";
			}
			catch (Exception exception)
			{
				return "Provider: 1Password NOT reachable / signed out (" + exception.Message + ").";
			}
		}

		#endregion

		#region Auto-detect

		private void AutoDetect()
		{
			if (string.IsNullOrEmpty(_gameSlug)) _gameSlug = Slugify(Application.productName);
			if (string.IsNullOrEmpty(_unityVersion)) _unityVersion = Application.unityVersion;
			DetectGitRemote();
			RefreshLicenses();
		}

		private void DetectGitRemote()
		{
			if (!string.IsNullOrEmpty(_repoUrl)) return;

			try
			{
				int exit = WizardShell.Run("git", "remote get-url origin", BuildPanel.ProjectRoot, out string url, out _);
				if (exit == 0 && !string.IsNullOrWhiteSpace(url)) _repoUrl = url.Trim();
			}
			catch (Exception exception)
			{
				Debug.Log("[Project Setup] git remote auto-detect failed (" + exception.Message + ") - enter the repo URL manually.");
			}
		}

		private void RefreshLicenses()
		{
			List<string> names = ReadLicenseNames();
			_licenses = names != null && names.Count > 0 ? names : new List<string> { "ateo" };
			if (string.IsNullOrEmpty(_unityLicenseName)) _unityLicenseName = _licenses[0];
		}

		/// <summary>Best-effort read of the field labels on the 1Password unity-licenses item (the license registry §13.4).</summary>
		private List<string> ReadLicenseNames()
		{
			try
			{
				int exit = WizardShell.Run(OpCli.ResolveCliPath(),
					"item get unity-licenses --vault " + Quote(_secretProviderConfig) +
					" --account " + Quote(_secretProviderAccount) + " --format json",
					BuildPanel.ProjectRoot, out string json, out _);
				if (exit != 0 || string.IsNullOrWhiteSpace(json)) return null;

				OpItem item = JsonUtility.FromJson<OpItem>(json);
				if (item == null || item.fields == null) return null;

				List<string> names = new List<string>();
				foreach (OpField field in item.fields)
				{
					if (field == null || string.IsNullOrEmpty(field.label)) continue;
					if (field.label == "notesPlain" || field.label == "password") continue;
					if (!names.Contains(field.label)) names.Add(field.label);
				}

				return names;
			}
			catch (Exception exception)
			{
				Debug.Log("[Project Setup] Could not read unity-licenses from 1Password (" + exception.Message + ") - using a free-text license field.");
				return null;
			}
		}

		private IEnumerable<string> LicenseOptions()
		{
			return _licenses ?? new List<string> { "ateo" };
		}

		#endregion

		#region Apply / secrets

		private void ApplyFields(ProjectConfig project)
		{
			SetField(project, "_gameToken", _gameSlug);
			SetField(project, "_teamId", _teamId);
			SetField(project, "_serverBaseUrl", _serverBaseUrl);
			SetField(project, "_slackChannelId", _slackChannelId);
			SetField(project, "_vcs", _vcs);
			SetField(project, "_repoUrl", _repoUrl);
			SetField(project, "_unityVersion", _unityVersion);
			SetField(project, "_unityLicenseName", _unityLicenseName);
			SetField(project, "_secretProviderScheme", _secretProviderScheme);
			SetField(project, "_secretProviderConfig", _secretProviderConfig);
			SetField(project, "_vcsCredentialName", ResolvedCredentialName());
		}

		private string ResolvedCredentialName()
		{
			return _credentialMode == CredentialMode.SelectExisting ? _credentialName : _newCredentialName;
		}

		/// <summary>Store any add-new credential material whose secrets weren't already provisioned by a dedicated button (plastic user/pass, a pasted UVCS PAT).</summary>
		private void ProvisionPendingCredential()
		{
			if (_credentialMode != CredentialMode.AddNew || string.IsNullOrEmpty(_newCredentialName)) return;

			switch (_credentialType)
			{
				case CredentialType.PlasticUserpass:
					if (!string.IsNullOrEmpty(_plasticUser))
					{
						Provision(_newCredentialName, "username", SecretValue.OfString(_plasticUser), SecretKind.String, "Plastic username");
					}

					if (!string.IsNullOrEmpty(_plasticPassword))
					{
						Provision(_newCredentialName, "password", SecretValue.OfString(_plasticPassword), SecretKind.String, "Plastic password");
					}

					break;

				case CredentialType.UvcsPat:
					if (_patMode == PatMode.Paste && !string.IsNullOrEmpty(_uvcsPat))
					{
						Provision(_newCredentialName, "token", SecretValue.OfString(_uvcsPat), SecretKind.String, "UVCS PAT");
						if (!string.IsNullOrEmpty(_uvcsBotUser))
						{
							Provision(_newCredentialName, "username", SecretValue.OfString(_uvcsBotUser), SecretKind.String, "UVCS bot username");
						}
					}

					break;
			}
		}

		/// <summary>Store credential material as a provider secret, degrading to a logged TODO when the provider is signed out.</summary>
		private void Provision(string item, string field, SecretValue value, SecretKind kind, string description)
		{
			ISecretProvider provider = SecretProviders.Resolve(_secretProviderScheme, _secretProviderConfig, _secretProviderAccount);
			if (provider == null)
			{
				Debug.Log("[Project Setup] No provider for scheme '" + _secretProviderScheme + "'; record '" + item + "/" + field + "' manually (TODO).");
				return;
			}

			try
			{
				provider.CreateOrUpdateAsync(item, field, value).GetAwaiter().GetResult();
				Debug.Log("[Project Setup] Stored credential secret " + item + "/" + field + " (" + description + ").");
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[Project Setup] Could not store '" + item + "/" + field + "' (" + exception.Message +
					"). Create it manually (TODO).");
			}
		}

		/// <summary>
		/// Write the per-project VCS record <c>vcs-&lt;project-key&gt;</c> to the provider (§11.7) so the server
		/// resolves {repoUrl, vcsType, credentialName, cmServer?} from the project key alone, **pre-checkout** — the
		/// piece that makes onboarding self-service end to end. Degrades to a logged note if the provider is
		/// unreachable. (Fields are written as concealed for now; a text-field variant for vault-UI inspectability
		/// is a later polish.)
		/// </summary>
		private void WriteVcsRecord()
		{
			ISecretProvider provider = SecretProviders.Resolve(_secretProviderScheme, _secretProviderConfig, _secretProviderAccount);
			string item = "vcs-" + _gameSlug;
			if (provider == null)
			{
				Debug.Log("[Project Setup] No provider for scheme '" + _secretProviderScheme + "'; create the '" + item + "' record manually (TODO).");
				return;
			}

			try
			{
				WriteField(provider, item, "repoUrl", _repoUrl);
				WriteField(provider, item, "vcsType", ResolvedVcsType());
				WriteField(provider, item, "credentialName", ResolvedCredentialName());
				string cmServer = ResolvedCmServer();
				if (!string.IsNullOrEmpty(cmServer)) WriteField(provider, item, "cmServer", cmServer);
				Debug.Log("[Project Setup] Wrote VCS record '" + item + "' (repoUrl, vcsType, credentialName).");
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[Project Setup] Could not write VCS record '" + item + "' (" + exception.Message + "). Create it manually (TODO).");
			}
		}

		private static void WriteField(ISecretProvider provider, string item, string field, string value)
		{
			provider.CreateOrUpdateAsync(item, field, SecretValue.OfString(value ?? string.Empty)).GetAwaiter().GetResult();
		}

		/// <summary>Provider-contract `vcsType` (§11.7): git / plastic / uvcs. UVCS is distinguished from on-prem Plastic by the chosen credential type.</summary>
		private string ResolvedVcsType()
		{
			if (_vcs == VcsKind.Git) return "git";
			if (_credentialMode == CredentialMode.AddNew && _credentialType == CredentialType.UvcsPat) return "uvcs";
			return "plastic";
		}

		/// <summary>The `cm` server for plastic/uvcs. UVCS = <c>org@cloud</c>; an on-prem Plastic server isn't collected by the wizard yet (P3.2).</summary>
		private string ResolvedCmServer()
		{
			if (ResolvedVcsType() == "uvcs" && !string.IsNullOrEmpty(_uvcsOrg))
			{
				return _uvcsOrg.Contains("@") ? _uvcsOrg : (_uvcsOrg + "@cloud");
			}

			return "";
		}

		#endregion

		#region Helpers

		private static void SetField(object target, string name, object value)
		{
			FieldInfo field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
			field?.SetValue(target, value);
		}

		private static void EnsureBuildConfigsFolder()
		{
			if (!AssetDatabase.IsValidFolder("Assets/BuildConfigs"))
			{
				AssetDatabase.CreateFolder("Assets", "BuildConfigs");
			}
		}

		private static string Slugify(string value)
		{
			if (string.IsNullOrEmpty(value)) return "";

			System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);
			foreach (char c in value)
			{
				if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
				else if (c == ' ' || c == '_' || c == '-') builder.Append('-');
			}

			return builder.ToString().Trim('-');
		}

		private static string Quote(string value)
		{
			return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
		}

		private static void TryDelete(string path)
		{
			try
			{
				if (File.Exists(path)) File.Delete(path);
			}
			catch (Exception)
			{
				// Best-effort temp cleanup.
			}
		}

		#endregion

		#region Nested Types

		private enum CredentialMode
		{
			SelectExisting,
			AddNew
		}

		private enum CredentialType
		{
			GitDeployKey,
			GitSshKey,
			PlasticUserpass,
			UvcsPat
		}

		private enum PatMode
		{
			GenerateNow,
			Paste
		}

		[Serializable]
		private sealed class OpItem
		{
			public OpField[] fields;
		}

		[Serializable]
		private sealed class OpField
		{
			public string label;
			public string id;
		}

		#endregion
	}
}

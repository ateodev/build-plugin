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
	/// offers when no <see cref="ProjectConfig"/> asset exists. Heavy on auto-detection (project key from the
	/// product name, repo URL from <c>git remote</c>), it gathers the per-project onboarding facts - project key,
	/// team (which supplies the secret-provider coords from its TeamCity params), server URL, VCS + a reusable
	/// checkout credential (the credential registry §13.3), Slack channel, Unity license (§13.4) - then
	/// <c>Create ProjectConfig</c> provisions the pending credential secrets, writes the <c>vcs-&lt;key&gt;</c>
	/// record and creates <c>Assets/BuildConfigs/ProjectConfig.asset</c>. Afterward, editing lives in the
	/// Settings view.
	///
	/// Live integrations (TeamCity, the secret provider, the <c>cm</c>/<c>ssh-keygen</c> CLIs) DEGRADE GRACEFULLY:
	/// a missing tool / signed-out provider logs and continues; the asset is still creatable.
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
		[NonSerialized] private byte[] _privateKey;   // kept in memory for the session so 'Verify' can test-clone without re-touching disk
		[NonSerialized] private List<string> _licenses = new List<string> { "ateo" };

		// --- Identity -----------------------------------------------------------------------------------------

		[SerializeField, HideInInspector] private string _projectKey = "";

		// Custom-drawn so the key formats LIVE as you type: [OnValueChanged] fires per keystroke on the backing
		// field, but a focused IMGUI text field keeps its OWN edit buffer, so the reformat isn't visible until
		// focus-loss. Transforming the input EVENT (space -> '-', upper -> lower, block illegal) makes the field's
		// buffer itself only ever hold valid chars; a full normalize on focus-loss handles paste + '--' collapse/trim.
		[BoxGroup("Project"), PropertyOrder(0), OnInspectorGUI]
		private void DrawProjectKey()
		{
			const string controlName = "ateo.projectKey";
			Event e = Event.current;
			if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == controlName)
			{
				char c = e.character;
				if (c == ' ') e.character = '-';
				else if (c >= 'A' && c <= 'Z') e.character = char.ToLowerInvariant(c);
				else if (c != '\0' && !char.IsControl(c) && !((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) e.Use();
			}

			GUI.SetNextControlName(controlName);
			_projectKey = EditorGUILayout.TextField(
				new GUIContent("Project key", "Unique project key - lowercase a-z 0-9 and '-' (whitespace becomes '-'). Suggested from the product name; formats live as you type."),
				_projectKey);

			if (GUI.GetNameOfFocusedControl() != controlName) _projectKey = ToProjectKey(_projectKey);
		}

		[NonSerialized] private List<string> _teams = new List<string>();
		[NonSerialized] private bool _tokenMissing;

		// Teams are fetched once on wizard open (they don't change while the window is open). TeamCity-only -
		// NO secret-provider interaction here; that waits until a team is chosen and its provider type is known.
		private async void FetchTeams()
		{
			string token = BuildServerSettings.Token;
			// Recorded (not a silent return): the first-run dev otherwise just saw an empty Team dropdown
			// with no hint that the machine-local token is the missing prerequisite.
			_tokenMissing = string.IsNullOrEmpty(token);
			if (_tokenMissing)
			{
				Repaint();
				return;
			}

			try
			{
				using (TeamCityClient client = new TeamCityClient(_serverBaseUrl, token)) { _teams = await client.ListTeamsAsync(); }
			}
			catch (Exception exception) { _validation = "Could not fetch teams: " + exception.Message; }

			Repaint();
		}

		[BoxGroup("Project"), PropertyOrder(1), LabelText("Team")]
		[InfoBox("Set your TeamCity access token in the Build Panel's Settings to load teams.",
			InfoMessageType.Warning, nameof(_tokenMissing))]
		[ValueDropdown(nameof(_teams)), OnValueChanged(nameof(OnTeamChanged))]
		[SerializeField, Tooltip("Trust-boundary team - a top-level TeamCity project, fetched from the server. Choosing " +
			"one fills the secret-provider coords + licenses; you never have to guess a valid value.")]
		private string _teamId = "";

		// Teams normally load on wizard open; this retry exists for the token-missing first run, so setting the
		// token in Settings doesn't force the dev to close and reopen a half-filled wizard.
		[BoxGroup("Project"), PropertyOrder(1.5f), ShowIf(nameof(_tokenMissing))]
		[Button("Reload teams")]
		private void ReloadTeams()
		{
			FetchTeams();
		}

		[BoxGroup("Project"), PropertyOrder(2)]
		[OnValueChanged(nameof(FetchTeams))]
		[SerializeField, LabelText("Server URL"), Tooltip("TeamCity base URL the panel talks to. Re-fetches the team list when changed.")]
		private string _serverBaseUrl = "https://build.ateonet.work";

		[BoxGroup("Project"), PropertyOrder(3)]
		[SerializeField, Tooltip("Slack channel id this project's build notifications post to.")]
		private string _slackChannelId = "";

		// --- Secrets provider ---------------------------------------------------------------------------------

		[BoxGroup("Secrets provider"), PropertyOrder(10)]
		[InfoBox("Empty until you pick a team - then filled from that team's TeamCity params (the single source). These " +
			"are provider-agnostic coordinates: 'Config'/'Account' mean whatever the team's provider needs (a 1Password " +
			"vault/account, an OpenBao URL, ...). Never typed here or committed.", InfoMessageType.None)]
		[SerializeField, ReadOnly, LabelText("Scheme")]
		private string _secretProviderScheme = "";

		[BoxGroup("Secrets provider"), PropertyOrder(11)]
		[SerializeField, ReadOnly, LabelText("Config")]
		private string _secretProviderConfig = "";

		[BoxGroup("Secrets provider"), PropertyOrder(12)]
		[SerializeField, ReadOnly, LabelText("Account")]
		private string _secretProviderAccount = "";

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

		// Git key types. Not [ReadOnly] - that greys the field and blocks text selection; the value is
		// display-only (the real key lives in _publicKey), so an accidental edit is harmless.
		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(ShowGitKey)), PropertyOrder(26)]
		[SerializeField, LabelText("Public key"), TextArea(2, 4),
			Tooltip("Generated public key - add it to your repo host as a deploy key (deploy key = least privilege). Select the text or use the Copy button below.")]
		private string _publicKeyDisplay = "";

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(HasPublicKey)), PropertyOrder(26.5f)]
		[Button(ButtonSizes.Medium, Name = "Copy public key to clipboard")]
		private void CopyPublicKey()
		{
			EditorGUIUtility.systemCopyBuffer = _publicKey;
			_validation = "Public key copied to clipboard.";
		}

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
		[ValueDropdown(nameof(LicenseOptions))]
		[SerializeField, LabelText("License"), Tooltip("Unity license name (matched agent-side to <name>.ulf). Enumerated from the team provider's 'unity-licenses' item after a team is chosen; default \"ateo\".")]
		private string _unityLicenseName = "ateo";

		// The Unity version is intentionally NOT in the wizard: it's rarely pinned. Left empty so the agent reads it
		// from ProjectSettings/ProjectVersion.txt; pin it later on the ProjectConfig asset only if a build must differ.
		[SerializeField, HideInInspector] private string _unityVersion = "";

		#endregion

		#region Predicates (Odin ShowIf)

		private bool IsSelectExisting => _credentialMode == CredentialMode.SelectExisting;
		private bool IsAddNew => _credentialMode == CredentialMode.AddNew;
		private bool ShowGitKey => IsAddNew && (_credentialType == CredentialType.GitDeployKey || _credentialType == CredentialType.GitSshKey);
		private bool HasPublicKey => ShowGitKey && !string.IsNullOrEmpty(_publicKey);
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
				// stdin "\n\n" answers ssh-keygen's two passphrase prompts with empty, in case the -N "" arg is lost in
				// command-line quoting - so it can never block on a prompt (which froze the editor; see WizardShell.Run).
				int exit = WizardShell.Run("ssh-keygen", "-t ed25519 -N \"\" -C " + Quote(name) + " -f " + Quote(temp),
					BuildPanel.ProjectRoot, out _, out string stderr, "\n\n");
				if (exit != 0)
				{
					_validation = "ssh-keygen failed (" + exit + "): " + stderr + " - degrade: add a key manually.";
					return;
				}

				_publicKey = File.ReadAllText(temp + ".pub");
				_publicKeyDisplay = _publicKey;

				byte[] privateKey = File.ReadAllBytes(temp);
				_privateKey = privateKey;
				Provision(name, "private_key", SecretValue.OfFile(privateKey), SecretKind.File, name + " checkout private key");
				_validation = "Keypair generated. " + HostGuidance() + " Then hit 'Verify deploy key'.";
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

		// Host-capability registry (#29): which repo hosts take a read-only SSH deploy/access key and how to add it.
		// Powers the guidance shown after key generation and on a failed Verify - so the dev never has to hunt for it.
		private static readonly (string Match, string Name, string Guidance)[] HostRegistry =
		{
			("github.com",    "GitHub",    "GitHub: repo Settings -> Deploy keys -> Add deploy key. Paste the public key; leave 'Allow write access' OFF (read-only)."),
			("gitlab.com",    "GitLab",    "GitLab: repo Settings -> Repository -> Deploy keys. Paste the public key; do NOT tick write access."),
			("bitbucket.org", "Bitbucket", "Bitbucket: repo Settings -> Access keys -> Add key. Paste the public key (Bitbucket access keys are read-only)."),
		};

		private string HostGuidance()
		{
			string url = _repoUrl ?? "";
			foreach ((string match, string _, string guidance) in HostRegistry)
			{
				if (url.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) return guidance;
			}

			return "Add the public key to your repo host as a read-only deploy/access key.";
		}

		[BoxGroup("Version control/Checkout credential"), ShowIf(nameof(HasPublicKey)), PropertyOrder(33)]
		[InfoBox("$DeployKeyGuidance", InfoMessageType.Info)]
		[Button("Verify deploy key")]
		private void VerifyDeployKey()
		{
			if (_privateKey == null || _privateKey.Length == 0) { _validation = "Generate the keypair first."; return; }
			if (string.IsNullOrEmpty(_repoUrl)) { _validation = "Set the repo URL first."; return; }

			string keyPath = Path.Combine(Path.GetTempPath(), "ateo-verify-" + Guid.NewGuid().ToString("N"));
			try
			{
				File.WriteAllBytes(keyPath, _privateKey);
				string devNull = Application.platform == RuntimePlatform.WindowsEditor ? "NUL" : "/dev/null";
				string sshCmd = "core.sshCommand=ssh -i \\\"" + keyPath + "\\\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=" + devNull;
				int exit = WizardShell.Run("git", "-c \"" + sshCmd + "\" ls-remote " + Quote(_repoUrl),
					BuildPanel.ProjectRoot, out _, out string stderr);
				_validation = exit == 0
					? "Deploy key VERIFIED - it can read " + _repoUrl + "."
					: "Deploy key NOT working yet (git exit " + exit + "). " + HostGuidance();
			}
			catch (Exception exception) { _validation = "Verify failed to run: " + exception.Message; }
			finally { TryDelete(keyPath); }
		}

		private string DeployKeyGuidance => HostGuidance();

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

		[PropertyOrder(51), PropertySpace(8)]
		[Button("Create ProjectConfig", ButtonSizes.Large)]
		private void Create()
		{
			if (string.IsNullOrWhiteSpace(_projectKey))
			{
				_validation = "Enter a project key.";
				return;
			}

			_projectKey = ToProjectKey(_projectKey); // enforce the lowercase [a-z0-9-] format on hand-typed input

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

			Debug.Log("[Project Setup] Created " + assetPath + " for project '" + _projectKey + "'.");
			Close();
		}

		[ShowInInspector, DisplayAsString(false), HideLabel, PropertyOrder(52)]
		[ShowIf("@!string.IsNullOrEmpty(this._validation)")]
		private string Validation => _validation;

		private async void OnTeamChanged()
		{
			string token = BuildServerSettings.Token;
			if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_teamId)) return;

			try
			{
				using (TeamCityClient client = new TeamCityClient(_serverBaseUrl, token)) { await FetchCoords(client); }
				// The team's provider coords are now known - this is the FIRST time the wizard touches the secret
				// provider (a 1Password auth prompt legitimately appears HERE, not on wizard open).
				RefreshLicenses();
				Repaint();
			}
			catch (Exception exception) { _validation = "Coords fetch failed: " + exception.Message; Repaint(); }
		}

		// Provider coords are team-level (single source, §11.7): fetched from the team's TeamCity params, never typed
		// or committed. The wizard uses them only to WRITE the vcs-<key> record + provision secrets.
		private async Task FetchCoords(TeamCityClient client)
		{
			TeamCityClient.ProviderCoords coords = await client.GetTeamProviderCoordsAsync(_teamId);
			if (!string.IsNullOrEmpty(coords.Scheme))  _secretProviderScheme  = coords.Scheme;
			if (!string.IsNullOrEmpty(coords.Config))  _secretProviderConfig  = coords.Config;
			if (!string.IsNullOrEmpty(coords.Account)) _secretProviderAccount = coords.Account;
		}


		#endregion

		#region Auto-detect

		private void AutoDetect()
		{
			// Pre-fill the project key from the Unity product name (already normalized to a valid [a-z0-9-] key).
			if (string.IsNullOrEmpty(_projectKey)) _projectKey = ToProjectKey(Application.productName);
			DetectGitRemote();
			FetchTeams(); // TeamCity-only. Licenses + coords wait for a team to be chosen - no provider auth on open.
		}

		private void DetectGitRemote()
		{
			if (!string.IsNullOrEmpty(_repoUrl)) return;

			// Auto-detect VCS + repo from the checkout: a git remote -> Git; else a .plastic workspace -> Plastic.
			try
			{
				int exit = WizardShell.Run("git", "remote get-url origin", BuildPanel.ProjectRoot, out string url, out _);
				if (exit == 0 && !string.IsNullOrWhiteSpace(url)) { _vcs = VcsKind.Git; _repoUrl = ToSshRemote(url.Trim()); return; }
			}
			catch (Exception exception)
			{
				Debug.Log("[Project Setup] git remote auto-detect failed (" + exception.Message + ") - checking Plastic / enter the repo manually.");
			}

			if (Directory.Exists(Path.Combine(BuildPanel.ProjectRoot, ".plastic"))) _vcs = VcsKind.Plastic;
		}

		/// <summary>
		/// Normalize a git remote to its SSH form - we only check out over SSH (deploy / bot keys), so a detected
		/// <c>https://host/owner/repo(.git)</c> becomes <c>git@host:owner/repo.git</c>. Already-SSH URLs pass through.
		/// </summary>
		private static string ToSshRemote(string url)
		{
			if (string.IsNullOrEmpty(url)) return url;
			if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return url;

			const string https = "https://";
			if (url.StartsWith(https, StringComparison.OrdinalIgnoreCase))
			{
				string rest = url.Substring(https.Length);
				int slash = rest.IndexOf('/');
				if (slash > 0)
				{
					string host = rest.Substring(0, slash);
					string path = rest.Substring(slash + 1).TrimEnd('/');
					if (!path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path += ".git";
					return "git@" + host + ":" + path;
				}
			}

			return url;
		}

		private void RefreshLicenses()
		{
			List<string> names = ReadLicenseNames();
			_licenses = names != null && names.Count > 0 ? names : new List<string> { "ateo" };
			if (string.IsNullOrEmpty(_unityLicenseName)) _unityLicenseName = _licenses[0];
		}

		/// <summary>Best-effort read of the field labels on the provider's unity-licenses record (the license registry §13.4).</summary>
		private List<string> ReadLicenseNames()
		{
			try
			{
				// Read the license registry through the provider (ReadRecord = field-label -> value), not the op CLI:
				// the field LABELS are the license names. Any provider serving the record works, unchanged.
				ISecretProvider provider = SecretProviders.Resolve(_secretProviderScheme, _secretProviderConfig, _secretProviderAccount);
				if (provider == null) return null;

				IReadOnlyDictionary<string, string> record = WizardShell.RunSync(() => provider.ReadRecordAsync("unity-licenses"));
				if (record == null || record.Count == 0) return null;

				List<string> names = new List<string>();
				foreach (string label in record.Keys)
				{
					if (string.IsNullOrEmpty(label) || label == "notesPlain" || label == "password") continue;
					if (!names.Contains(label)) names.Add(label);
				}

				return names;
			}
			catch (Exception exception)
			{
				Debug.Log("[Project Setup] Could not read the unity-licenses record via provider '" + _secretProviderScheme + "' (" + exception.Message + ") - using a free-text license field.");
				return null;
			}
		}

		// Store the lowercase name (matches <name>.ulf on the agent), but SHOW it capitalized so the dropdown reads nicely.
		private IEnumerable<ValueDropdownItem<string>> LicenseOptions()
		{
			foreach (string name in _licenses ?? new List<string> { "ateo" })
			{
				string display = string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
				yield return new ValueDropdownItem<string>(display, name);
			}
		}

		#endregion

		#region Apply / secrets

		private void ApplyFields(ProjectConfig project)
		{
			// Slim ProjectConfig (§11.7): repo URL, checkout cred and provider coords are NOT persisted here -
			// the server reads the repo/cred from the provider vcs-<key> record and the coords from TeamCity team
			// params. The wizard still uses its own coord fields (fetched) to WRITE those records; see WriteVcsRecord.
			SetField(project, "_projectKey", _projectKey);
			SetField(project, "_teamId", _teamId);
			SetField(project, "_serverBaseUrl", _serverBaseUrl);
			SetField(project, "_slackChannelId", _slackChannelId);
			SetField(project, "_vcs", _vcs);
			SetField(project, "_unityVersion", _unityVersion);
			SetField(project, "_unityLicenseName", _unityLicenseName);
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

		/// <summary>
		/// Store credential material as a provider secret under the contract item name <c>cred-&lt;name&gt;</c>
		/// (provider-contract.md): the agent resolves the credential a vcs-record points at as <c>cred-&lt;credentialName&gt;</c>,
		/// so the wizard must write it with that prefix. Degrades to a logged TODO when the provider is signed out.
		/// </summary>
		private void Provision(string item, string field, SecretValue value, SecretKind kind, string description)
		{
			ISecretProvider provider = SecretProviders.Resolve(_secretProviderScheme, _secretProviderConfig, _secretProviderAccount);
			string credItem = "cred-" + item;
			if (provider == null)
			{
				Debug.Log("[Project Setup] No provider for scheme '" + _secretProviderScheme + "'; record '" + credItem + "/" + field + "' manually (TODO).");
				return;
			}

			try
			{
				WizardShell.RunSync(() => provider.CreateOrUpdateAsync(credItem, field, value)); // off-main-thread: avoids the Editor deadlock
				Debug.Log("[Project Setup] Stored credential secret " + credItem + "/" + field + " (" + description + ").");
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[Project Setup] Could not store '" + credItem + "/" + field + "' (" + exception.Message +
					"). Create it manually (TODO).");
			}
		}

		/// <summary>
		/// Write the per-project VCS record <c>vcs-&lt;project-key&gt;</c> to the provider (§11.7) so the server
		/// resolves {repoUrl, vcsType, credentialName, cmServer?} from the project key alone, **pre-checkout** — the
		/// piece that makes onboarding self-service end to end. Degrades to a logged note if the provider is
		/// unreachable. (Fields are written as plain text, concealed:false - they are non-secret pointers, kept
		/// inspectable in the vault UI.)
		/// </summary>
		private void WriteVcsRecord()
		{
			ISecretProvider provider = SecretProviders.Resolve(_secretProviderScheme, _secretProviderConfig, _secretProviderAccount);
			string item = "vcs-" + _projectKey;
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
			// concealed:false - these are non-secret record pointers (repoUrl/vcsType/credentialName), stored as plain text.
			WizardShell.RunSync(() => provider.CreateOrUpdateAsync(item, field, SecretValue.OfString(value ?? string.Empty), concealed: false));
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

		/// <summary>Normalize free text to a valid project key: lowercase ASCII <c>[a-z0-9-]</c>, '-' the only separator, collapsed + trimmed (§11.7).</summary>
		private static string ToProjectKey(string value)
		{
			if (string.IsNullOrEmpty(value)) return "";

			System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);
			foreach (char c in value)
			{
				char lower = char.ToLowerInvariant(c);
				builder.Append((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') ? lower : '-');
			}

			string result = builder.ToString();
			while (result.Contains("--")) result = result.Replace("--", "-");
			return result.Trim('-');
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

		#endregion
	}
}

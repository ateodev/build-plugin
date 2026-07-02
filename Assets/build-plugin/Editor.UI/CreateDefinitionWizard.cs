using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif
using Debug = UnityEngine.Debug;

namespace Ateo.Build
{
	/// <summary>
	/// The create-definition wizard (§13.2), a floating <see cref="OdinEditorWindow"/> opened from the Build
	/// Panel's <c>+ Add Build Definition</c> button. It owns SO creation to a buildable state: pick a target
	/// (the concrete <see cref="BuildDefinition"/> subclass), name it (a BARE name - the docked platform prefix
	/// only PREVIEWS the composed display label, see <see cref="DefinitionNaming"/> - written to
	/// <c>Assets/BuildConfigs/&lt;token&gt;/&lt;name&gt;.asset</c>, unique per platform folder), choose
	/// the build source (Build Profile or a manual scenes+defines list), pick a default branch (enumerated from
	/// the repo), and - for iOS/Android - wire signing (create a keystore via <c>keytool</c> and provision it as
	/// a secret, or reference an existing one). <c>Create</c> writes the correctly-typed asset and selects it in
	/// the panel. Ongoing editing (post-build actions, etc.) is the Configure tab's job, not the wizard's.
	///
	/// Live integrations (git, keytool, the secret provider) DEGRADE GRACEFULLY: a missing tool / signed-out
	/// provider logs and continues - the asset is still created and references are still recorded.
	/// </summary>
	public sealed class CreateDefinitionWizard : OdinEditorWindow
	{
		#region Open

		/// <summary>Open the wizard as a floating utility window, bound to the panel that will receive the new asset.</summary>
		public static void Open(BuildPanel owner)
		{
			CreateDefinitionWizard window = CreateInstance<CreateDefinitionWizard>();
			window.titleContent = new GUIContent("New Build Definition");
			window.minSize = new Vector2(540, 560);
			window._owner = owner;
			window.Initialize();
			window.ShowUtility();
		}

		#endregion

		#region Fields

		[NonSerialized] private BuildPanel _owner;
		[NonSerialized] private List<string> _branches = new List<string>();
		[NonSerialized] private bool _gitOk;
		[NonSerialized] private string _result = "";

		[BoxGroup("Target"), ValueDropdown(nameof(AuthorablePlatforms)), HideLabel, PropertyOrder(0)]
		[SerializeField, Tooltip("The build target. The concrete BuildDefinition subclass is chosen from this - it IS the platform. " +
			"The map-only Windows32 + LinuxSim targets are intentionally not offered.")]
		private BuildPlatform _target = BuildPlatform.Android;

		[BoxGroup("Target"), PropertyOrder(1)]
		[InfoBox("All targets are offered. A target whose team executor / editor module isn't provisioned can still " +
			"be authored - availability surfaces on the definition's Build tab, not here.", InfoMessageType.None)]
		[ShowInInspector, DisplayAsString, LabelText("Creates")]
		private string CreatesTypeName => TypeForTarget(_target)?.Name ?? "(none)";

		// The BARE definition name - exactly what gets stored and used as the asset file name. It never
		// contains the platform: the platform lives in the asset's folder, so changing the target mid-wizard
		// swaps the docked prefix label (and the destination folder) and what was typed survives untouched.
		[SerializeField, HideInInspector] private string _definitionName = "";

		// Custom-drawn so the platform token is a DOCKED, non-editable prefix label left of the field (visual:
		// "Linux - [Test]"). The prefix PREVIEWS the composed display label users will see in Slack/TeamCity,
		// so nobody is tempted to type the platform into the name. Illegal characters are blocked on the input
		// EVENT: [OnValueChanged] fires on the backing field, but a focused IMGUI text field keeps its OWN edit
		// buffer, so a post-hoc reformat wouldn't show until focus-loss (the project-setup wizard's project-key
		// field pioneered this). A full sanitize on focus-loss handles paste + space collapse/trim.
		[BoxGroup("Name"), PropertyOrder(2), OnInspectorGUI]
		private void DrawDefinitionName()
		{
			const string controlName = "ateo.definitionName";
			Event current = Event.current;
			if (current.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == controlName
				&& DefinitionNaming.IsIllegalCharacter(current.character))
			{
				current.Use();
			}

			using (new EditorGUILayout.HorizontalScope())
			{
				// Token + a bare hyphen: the separator's surrounding spaces come from the layout gap, so the
				// label reads exactly like the composed display label "<token> - <name>".
				GUIContent prefix = new GUIContent(_target.ToServerToken() + " -",
					"Platform prefix preview (from the chosen target). Stored name and file name are just '<name>' - " +
					"the platform comes from the asset's folder (" + DefinitionNaming.FolderFor(_target.ToServerToken()) +
					"). Standalone contexts (Slack, TeamCity) show '" + _target.ToServerToken() +
					DefinitionNaming.Separator + "<name>'.");
				GUILayout.Label(prefix, GUILayout.Width(EditorStyles.label.CalcSize(prefix).x + 2f));

				GUI.SetNextControlName(controlName);
				_definitionName = EditorGUILayout.TextField(_definitionName);
			}

			if (GUI.GetNameOfFocusedControl() != controlName) _definitionName = DefinitionNaming.SanitizeName(_definitionName);
		}

		[BoxGroup("Build source"), EnumToggleButtons, HideLabel, PropertyOrder(3)]
		[SerializeField, Tooltip("Profile = build a Unity 6 Build Profile; Manual = an explicit scene list + scripting defines.")]
		private BuildSourceMode _source = BuildSourceMode.Profile;

#if UNITY_6000_0_OR_NEWER
		[BoxGroup("Build source"), ShowIf(nameof(IsProfileSource)), PropertyOrder(4)]
		[SerializeField, Tooltip("Unity 6 Build Profile to build (platform/scenes/defines/player-settings come from it).")]
		private BuildProfile _buildProfile;
#endif

		[BoxGroup("Build source"), ShowIf(nameof(IsManualSource)), PropertyOrder(5)]
		[SerializeField, Tooltip("Explicit scenes to build (empty = the enabled EditorBuildSettings scenes).")]
		private SceneAsset[] _scenes = Array.Empty<SceneAsset>();

		[BoxGroup("Build source"), ShowIf(nameof(IsManualSource)), PropertyOrder(6)]
		[SerializeField, Tooltip("Scripting defines to ADD for this build (built-in path only).")]
		private string[] _includeDefines = Array.Empty<string>();

		[BoxGroup("Build source"), ShowIf(nameof(IsManualSource)), PropertyOrder(7)]
		[SerializeField, Tooltip("Scripting defines to REMOVE for this build.")]
		private string[] _excludeDefines = Array.Empty<string>();

		[BoxGroup("Default branch"), HideLabel, PropertyOrder(8)]
		[ValueDropdown(nameof(BranchOptions), AppendNextDrawer = true)]
		[SerializeField, Tooltip("Default branch this definition builds, as a bare branch name (no origin/ prefix). " +
			"Dropdown is the repo's branches; you may also type one freely (git fallback). Empty = the repo's default branch.")]
		private string _defaultBranch = "";

		// --- Signing (iOS / Android only) -------------------------------------------------------------------

		[BoxGroup("Signing"), ShowIf(nameof(IsSigningTarget)), EnumToggleButtons, HideLabel, PropertyOrder(9)]
		[SerializeField, Tooltip("Create a fresh keystore via keytool (Android), or reference an existing keystore / signing setup.")]
		private SigningMode _signingMode = SigningMode.SelectExisting;

		// Android - create new keystore (keytool).
		[FoldoutGroup("Signing/New keystore (keytool)"), ShowIf(nameof(ShowAndroidCreate)), PropertyOrder(10)]
		[SerializeField, Tooltip("Key alias inside the keystore.")]
		private string _keyAlias = "release";

		[FoldoutGroup("Signing/New keystore (keytool)"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, Tooltip("Keystore password (stored as a secret, never committed).")]
		private string _storePass = "";

		[FoldoutGroup("Signing/New keystore (keytool)"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, Tooltip("Key (alias) password (stored as a secret, never committed). Empty = reuse the keystore password.")]
		private string _keyPass = "";

		[FoldoutGroup("Signing/New keystore (keytool)/Distinguished name"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, LabelText("CN (name)")] private string _dnCommonName = "";
		[FoldoutGroup("Signing/New keystore (keytool)/Distinguished name"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, LabelText("O (org)")] private string _dnOrg = "";
		[FoldoutGroup("Signing/New keystore (keytool)/Distinguished name"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, LabelText("OU (unit)")] private string _dnOrgUnit = "";
		[FoldoutGroup("Signing/New keystore (keytool)/Distinguished name"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, LabelText("L (city)")] private string _dnCity = "";
		[FoldoutGroup("Signing/New keystore (keytool)/Distinguished name"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, LabelText("ST (state)")] private string _dnState = "";
		[FoldoutGroup("Signing/New keystore (keytool)/Distinguished name"), ShowIf(nameof(ShowAndroidCreate))]
		[SerializeField, LabelText("C (country)")] private string _dnCountry = "";

		// Android - select existing keystore.
		[FoldoutGroup("Signing/Existing keystore"), ShowIf(nameof(ShowAndroidExisting)), PropertyOrder(11)]
		[SerializeField, Tooltip("Keystore path relative to the checkout root (e.g. keystores/user.keystore).")]
		private string _existingKeystoreFile = "";

		[FoldoutGroup("Signing/Existing keystore"), ShowIf(nameof(ShowAndroidExisting))]
		[SerializeField, Tooltip("Key alias inside the existing keystore.")]
		private string _existingKeyAlias = "release";

		// iOS - signing references (match / ASC). No keytool path (iOS signs via fastlane match downstream).
		[FoldoutGroup("Signing/iOS references"), ShowIf(nameof(IsiOS)), PropertyOrder(12)]
		[SerializeField, Tooltip("Apple Developer Team ID (e.g. ABCDE12345).")]
		private string _appleTeamId = "";

		[FoldoutGroup("Signing/iOS references"), ShowIf(nameof(IsiOS))]
		[SerializeField, Tooltip("Provisioning profile / specifier (e.g. 'match AppStore com.ateo.game').")]
		private string _provisioningProfile = "";

		#endregion

		#region Predicates (Odin ShowIf)

		private bool IsProfileSource => _source == BuildSourceMode.Profile;
		private bool IsManualSource => _source == BuildSourceMode.Manual;
		private bool IsAndroid => _target == BuildPlatform.Android;
		private bool IsiOS => _target == BuildPlatform.iOS;
		private bool IsSigningTarget => IsAndroid || IsiOS;
		private bool ShowAndroidCreate => IsAndroid && _signingMode == SigningMode.CreateNew;
		private bool ShowAndroidExisting => IsAndroid && _signingMode == SigningMode.SelectExisting;

		#endregion

		#region Create

		// The composed display label ("<token> - <name>") for the HUMAN-FACING consumers (keystore DN, secret
		// UsedBy) - composed on demand, never stored. Path-like consumers (asset path, keystore file name) use
		// the bare _definitionName instead.
		private string DisplayLabel => DefinitionNaming.ComposeDisplayLabel(_target.ToServerToken(), _definitionName);

		[PropertyOrder(20), PropertySpace(8)]
		[Button("Create", ButtonSizes.Large)]
		private void Create()
		{
			_result = "";

			// Sanitize BEFORE validating: a whitespace/illegal-only entry must read as empty, not sneak through.
			_definitionName = DefinitionNaming.SanitizeName(_definitionName);
			if (string.IsNullOrEmpty(_definitionName))
			{
				_result = "Enter a definition name (at least one character after the '" +
					_target.ToServerToken() + DefinitionNaming.Separator + "' prefix).";
				return;
			}

			Type type = TypeForTarget(_target);
			if (type == null)
			{
				_result = "No definition subclass maps to target '" + _target + "'.";
				return;
			}

			string token = _target.ToServerToken();
			EnsurePlatformFolder(token);

			// Per-platform uniqueness IS the filesystem: one file per name per platform folder - no registry to
			// keep in sync. (Relative Assets/ paths resolve because the editor's cwd is the project root.)
			string assetPath = DefinitionNaming.FolderFor(token) + "/" + _definitionName + ".asset";
			if (File.Exists(assetPath))
			{
				_result = "A " + token + " definition named '" + _definitionName + "' already exists.";
				return;
			}

			BuildDefinition definition = (BuildDefinition)CreateInstance(type);
			definition.name = _definitionName;

			ApplySharedFields(definition);
			ApplySigning(definition);

			// EnsureId on any default post-build actions the subclass shipped (normally none).
			if (definition.PostBuildActions != null)
			{
				foreach (PostBuildAction action in definition.PostBuildActions) action?.EnsureId();
			}

			AssetDatabase.CreateAsset(definition, assetPath);
			EditorUtility.SetDirty(definition);
			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();

			// Select by ASSET PATH: bare names are only unique per platform, so a name lookup could land on
			// another platform's same-named definition.
			if (_owner != null) _owner.RefreshAndSelect(assetPath);

			Debug.Log("[New Definition] Created " + type.Name + " at " + assetPath + ". " + _result);
			Close();
		}

		[ShowInInspector, DisplayAsString(false), HideLabel, PropertyOrder(21)]
		[ShowIf("@!string.IsNullOrEmpty(this._result)")]
		private string Result => _result;

		#endregion

		#region Build wizard

		private void Initialize()
		{
			EnumerateBranches();
		}

		private void ApplySharedFields(BuildDefinition definition)
		{
			SetField(definition, "_definitionName", _definitionName); // the BARE name - platform lives in the folder
			// Canonical form (see VcsBranch): a bare branch name, "origin/"-stripped; empty is VALID and means
			// the agent resolves the repo's actual default branch - so no hardcoded "main" guess is stored.
			SetField(definition, "_defaultBranch", VcsBranch.Normalize(_defaultBranch));

			if (_source == BuildSourceMode.Profile)
			{
#if UNITY_6000_0_OR_NEWER
				SetField(definition, "_buildProfile", _buildProfile);
#endif
			}
			else
			{
				SetField(definition, "_scenes", _scenes ?? Array.Empty<SceneAsset>());
				SetField(definition, "_includeDefines", _includeDefines ?? Array.Empty<string>());
				SetField(definition, "_excludeDefines", _excludeDefines ?? Array.Empty<string>());
			}
		}

		private void ApplySigning(BuildDefinition definition)
		{
			if (definition is AndroidBuildDefinition android)
			{
				ApplyAndroidSigning(android);
			}
			else if (definition is iOSBuildDefinition ios)
			{
				if (!string.IsNullOrEmpty(_appleTeamId) || !string.IsNullOrEmpty(_provisioningProfile))
				{
					iOSSigning signing = new iOSSigning(_appleTeamId, _provisioningProfile, "MATCH_PASSWORD", "ASC_API_KEY");
					SetField(ios, "_iosSigning", signing);
				}
			}
		}

		private void ApplyAndroidSigning(AndroidBuildDefinition android)
		{
			if (_signingMode == SigningMode.SelectExisting)
			{
				if (string.IsNullOrEmpty(_existingKeystoreFile)) return;

				AndroidSigning existing = new AndroidSigning(
					_existingKeystoreFile, _existingKeyAlias, "ANDROID_KEYSTORE_PASS", "ANDROID_KEYALIAS_PASS");
				SetField(android, "_androidSigning", existing);
				return;
			}

			// Create-new path: generate a keystore, provision it + passwords as secrets, wire the references.
			// Path-like consumer, so bare name joined with a plain "-" (never the " - " display separator in
			// paths). The token IS included: keystores/ is one flat folder at the checkout root shared by all
			// platforms, while names are only unique per platform - "<token>-<name>" restores uniqueness there
			// without inventing per-platform subfolders for a folder that rarely holds more than a few files.
			string relativePath = "keystores/" + _target.ToServerToken() + "-" + _definitionName + ".keystore";
			string keystorePath = Path.Combine(BuildPanel.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

			bool generated = GenerateKeystore(keystorePath);

			ProjectConfig project = _owner != null ? _owner.Project : null;
			ISecretProvider provider = SecretProvisioner.ResolveTeamProvider(project);

			// One place generates conventional names (SecretProvisioner.ItemNameFor): the signing item groups
			// its three fields under the pseudo-key ANDROID_SIGNING -> "<project-key>_android-signing".
			string item = SecretProvisioner.ItemNameFor(project, "ANDROID_SIGNING");

			if (generated)
			{
				try
				{
					byte[] bytes = File.ReadAllBytes(keystorePath);
					ProvisionSecret(provider, item, "keystore", SecretValue.OfFile(bytes), SecretKind.File,
						"ANDROID_KEYSTORE_FILE", "Android keystore (document)");
				}
				catch (Exception exception)
				{
					Debug.LogWarning("[New Definition] Could not read generated keystore to provision it: " + exception.Message);
				}
			}

			ProvisionSecret(provider, item, "storepass", SecretValue.OfString(_storePass), SecretKind.String,
				"ANDROID_KEYSTORE_PASS", "Android keystore password");
			string keyPass = string.IsNullOrEmpty(_keyPass) ? _storePass : _keyPass;
			ProvisionSecret(provider, item, "keypass", SecretValue.OfString(keyPass), SecretKind.String,
				"ANDROID_KEYALIAS_PASS", "Android key-alias password");

			AndroidSigning signing = new AndroidSigning(
				relativePath, _keyAlias, "ANDROID_KEYSTORE_PASS", "ANDROID_KEYALIAS_PASS");
			SetField(android, "_androidSigning", signing);

			_result = generated
				? "Keystore generated at " + relativePath + " and signing references wired."
				: "keytool unavailable - asset created with signing references; generate the keystore manually (TODO).";
		}

		#endregion

		#region keytool

		private bool GenerateKeystore(string keystorePath)
		{
			string keytool = ResolveKeytool();
			if (string.IsNullOrEmpty(keytool))
			{
				Debug.LogWarning("[New Definition] keytool not found (JAVA_HOME / Unity OpenJDK / PATH). Skipping keystore generation - degrade to references only.");
				return false;
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(keystorePath));
				string keyPass = string.IsNullOrEmpty(_keyPass) ? _storePass : _keyPass;
				string dname = BuildDistinguishedName();

				StringBuilder args = new StringBuilder();
				args.Append("-genkeypair -storetype PKCS12 -keyalg RSA -keysize 2048 -validity 10000");
				args.Append(" -keystore ").Append(Quote(keystorePath));
				args.Append(" -alias ").Append(Quote(_keyAlias));
				args.Append(" -storepass ").Append(Quote(_storePass));
				args.Append(" -keypass ").Append(Quote(keyPass));
				args.Append(" -dname ").Append(Quote(dname));

				int exit = WizardShell.Run(keytool, args.ToString(), BuildPanel.ProjectRoot, out _, out string stderr);
				if (exit != 0)
				{
					Debug.LogWarning("[New Definition] keytool exited " + exit + ": " + stderr + " - degrade to references only.");
					return false;
				}

				return File.Exists(keystorePath);
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[New Definition] keytool generation threw: " + exception.Message + " - degrade to references only.");
				return false;
			}
		}

		private string BuildDistinguishedName()
		{
			// Default CN is the DISPLAY LABEL: a certificate is read standalone (keytool -list, Play Console),
			// where a bare "Test" would not say which platform's definition it signs.
			List<string> parts = new List<string>();
			AddDnPart(parts, "CN", string.IsNullOrEmpty(_dnCommonName) ? DisplayLabel : _dnCommonName);
			AddDnPart(parts, "OU", _dnOrgUnit);
			AddDnPart(parts, "O", _dnOrg);
			AddDnPart(parts, "L", _dnCity);
			AddDnPart(parts, "ST", _dnState);
			AddDnPart(parts, "C", _dnCountry);
			return parts.Count > 0 ? string.Join(", ", parts) : "CN=" + DisplayLabel;
		}

		private static void AddDnPart(List<string> parts, string key, string value)
		{
			if (!string.IsNullOrWhiteSpace(value)) parts.Add(key + "=" + value.Replace(",", "\\,"));
		}

		private static string ResolveKeytool()
		{
			string exe = Application.platform == RuntimePlatform.WindowsEditor ? "keytool.exe" : "keytool";

			string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
			if (!string.IsNullOrEmpty(javaHome))
			{
				string candidate = Path.Combine(javaHome, "bin", exe);
				if (File.Exists(candidate)) return candidate;
			}

			// Unity ships an OpenJDK with the Android module.
			string embedded = Path.Combine(EditorApplication.applicationContentsPath,
				"PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin", exe);
			if (File.Exists(embedded)) return embedded;

			return WizardShell.OnPath(exe) ? exe : null;
		}

		#endregion

		#region Secrets

		// The write/register mechanics live in the shared SecretProvisioner (also behind the Secrets view's
		// register dialog); this wrapper only keeps the wizard's DEGRADE semantics: a failed vault write still
		// records the convention reference so the created asset stays consistent, with a loud TODO to store the
		// value manually (the register dialog, by contrast, refuses to register what it could not write).
		private void ProvisionSecret(ISecretProvider provider, string item, string field, SecretValue value, SecretKind kind,
			string logicalKey, string description)
		{
			if (provider == null)
			{
				// No provider = no scheme to even build a reference in; registering a fabricated pointer would
				// commit a lie, so fail loudly instead of degrading.
				Debug.LogError("[New Definition] Cannot provision secret '" + item + "/" + field + "' (" + description +
					"): no secret provider is resolvable. Provider coordinates (scheme/config/account) come from the " +
					"team's TeamCity project params (unitybuild.provider.*) or, locally, the UNITYBUILD_PROVIDER_* " +
					"environment - none yielded a known provider. Fix the team params (or set the environment) and " +
					"re-run the wizard's signing step.");
				return;
			}

			// Scheme-correct pointer from the provider itself (never hand-assembled); replaced by the
			// write-confirmed reference on success.
			string reference = provider.ReferenceFor(item, field, kind).Reference;

			try
			{
				reference = SecretProvisioner.WriteSecret(provider, item, field, value).Reference;
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[New Definition] Secret provider could not store '" + item + "/" + field +
					"' (" + exception.Message + "). Recording the reference only - create the secret manually (TODO).");
			}

			ProjectConfig project = _owner != null ? _owner.Project : null;

			// UsedBy is human-facing (read standalone in the Secrets view), so record the display label - a
			// bare "Test" would not say which platform's definition uses the secret. Never overwrites: an entry
			// registered by an earlier wizard run (or by hand) wins over a re-run.
			SecretProvisioner.RegisterSecret(project, logicalKey, description, kind, reference,
				new[] { DisplayLabel }, overwriteExisting: false);
		}

		#endregion

		#region Git branches

		private void EnumerateBranches()
		{
			_branches = new List<string>();
			_gitOk = false;

			try
			{
				int exit = WizardShell.Run("git", "for-each-ref --format=%(refname:short) refs/heads refs/remotes",
					BuildPanel.ProjectRoot, out string stdout, out _);
				if (exit == 0 && !string.IsNullOrEmpty(stdout))
				{
					foreach (string line in stdout.Split('\n'))
					{
						// refs/remotes entries come back "origin/"-prefixed; only the bare canonical form is
						// offered (the agent resolves it to origin/<branch>), which also folds the local and
						// remote copy of the same branch into one entry.
						string branch = VcsBranch.Normalize(line.Trim());
						// "origin" is the short name of refs/remotes/origin/HEAD - an alias, not a branch.
						if (branch.Length == 0 || branch == "origin" || branch == "HEAD") continue;
						if (!_branches.Contains(branch)) _branches.Add(branch);
					}

					_gitOk = true;
				}

				int headExit = WizardShell.Run("git", "rev-parse --abbrev-ref HEAD", BuildPanel.ProjectRoot, out string head, out _);
				if (headExit == 0 && !string.IsNullOrWhiteSpace(head) && string.IsNullOrEmpty(_defaultBranch))
				{
					// A detached HEAD reports the literal "HEAD" - not a branch; leave empty (= repo default).
					string current = head.Trim();
					if (current != "HEAD") _defaultBranch = current;
				}
			}
			catch (Exception exception)
			{
				Debug.Log("[New Definition] git branch enumeration failed (" + exception.Message + ") - using a free-text branch field.");
			}

			// No "main" prefill when git failed: empty is the canonical "repo's default branch" (resolved
			// agent-side), which is correct for any repo - a guessed "main" is not.
		}

		private IEnumerable<string> BranchOptions()
		{
			return _branches ?? new List<string>();
		}

		#endregion

		#region Reflection / process helpers

		private static void SetField(object target, string name, object value)
		{
			Type type = target.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null)
				{
					field.SetValue(target, value);
					return;
				}

				type = type.BaseType;
			}
		}

		// Both levels via AssetDatabase (not Directory.CreateDirectory) so the folders get their .meta files
		// immediately and the CreateAsset below never races the importer.
		private static void EnsurePlatformFolder(string token)
		{
			if (!AssetDatabase.IsValidFolder(DefinitionNaming.RootFolder))
			{
				AssetDatabase.CreateFolder("Assets", "BuildConfigs");
			}

			if (!AssetDatabase.IsValidFolder(DefinitionNaming.FolderFor(token)))
			{
				AssetDatabase.CreateFolder(DefinitionNaming.RootFolder, token);
			}
		}

		/// <summary>The concrete definition asset type for a platform, or null if the platform is not authorable
		/// (the map-only legacy/niche targets Windows32 + LinuxSim).</summary>
		private static Type TypeForTarget(BuildPlatform target)
		{
			switch (target)
			{
				case BuildPlatform.Android:       return typeof(AndroidBuildDefinition);
				case BuildPlatform.iOS:           return typeof(iOSBuildDefinition);
				case BuildPlatform.Windows:       return typeof(WindowsBuildDefinition);
				case BuildPlatform.Mac:           return typeof(MacOSBuildDefinition);
				case BuildPlatform.Linux:         return typeof(LinuxBuildDefinition);
				case BuildPlatform.WindowsServer: return typeof(WindowsServerBuildDefinition);
				case BuildPlatform.MacServer:     return typeof(MacServerBuildDefinition);
				case BuildPlatform.LinuxServer:   return typeof(LinuxServerBuildDefinition);
				case BuildPlatform.WebGL:         return typeof(WebGLBuildDefinition);
				case BuildPlatform.UWP:           return typeof(UWPBuildDefinition);
				case BuildPlatform.tvOS:          return typeof(TvOSBuildDefinition);
				case BuildPlatform.VisionOS:      return typeof(VisionOSBuildDefinition);
				case BuildPlatform.Switch:        return typeof(SwitchBuildDefinition);
				case BuildPlatform.PS4:           return typeof(PS4BuildDefinition);
				case BuildPlatform.PS5:           return typeof(PS5BuildDefinition);
				case BuildPlatform.XboxOne:       return typeof(XboxOneBuildDefinition);
				case BuildPlatform.XboxGDKOne:    return typeof(XboxGDKOneBuildDefinition);
				case BuildPlatform.XboxSeries:    return typeof(XboxSeriesBuildDefinition);
				default:                          return null; // Windows32, LinuxSim: map-only, not authorable
			}
		}

		/// <summary>The platforms offered in the wizard's target dropdown - every BuildPlatform that has an
		/// authorable definition type (excludes the map-only Windows32 + LinuxSim).</summary>
		private static BuildPlatform[] AuthorablePlatforms()
		{
			System.Collections.Generic.List<BuildPlatform> list = new System.Collections.Generic.List<BuildPlatform>();
			foreach (BuildPlatform platform in System.Enum.GetValues(typeof(BuildPlatform)))
			{
				if (TypeForTarget(platform) != null) list.Add(platform);
			}

			return list.ToArray();
		}

		private static string Quote(string value)
		{
			return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
		}

		#endregion

		#region Nested Types

		private enum BuildSourceMode
		{
			Profile,
			Manual
		}

		private enum SigningMode
		{
			SelectExisting,
			CreateNew
		}

		#endregion
	}

	/// <summary>
	/// Tiny synchronous process helper shared by the wizards - runs a CLI and captures stdout/stderr without
	/// deadlocking. Every call is wrapped by callers in try/catch so a missing tool degrades gracefully.
	/// </summary>
	internal static class WizardShell
	{
		/// <summary>
		/// Run an async provider call to completion OFF the Editor main thread and return its result. The wizards'
		/// Odin button handlers are synchronous on the main thread, and 1Password's async chain captures Unity's
		/// SynchronizationContext - awaiting it via <c>.GetResult()</c> directly on the main thread DEADLOCKS (the
		/// continuation can never resume on the blocked main thread). <see cref="System.Threading.Tasks.Task.Run(System.Func{System.Threading.Tasks.Task})"/>
		/// gives the chain a thread-pool context instead, so it can't deadlock. Bounded by the op CLI's own timeout.
		/// (A fully-async wizard would avoid the brief main-thread block entirely - a later refinement.)
		/// </summary>
		public static T RunSync<T>(System.Func<System.Threading.Tasks.Task<T>> call)
		{
			return System.Threading.Tasks.Task.Run(call).GetAwaiter().GetResult();
		}

		public static int Run(string exe, string arguments, string workingDirectory, out string stdout, out string stderr,
			string stdin = null, int timeoutMs = 60000)
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true, // own stdin (never the editor's) - feed it or close it so a prompt can't hang us
				CreateNoWindow = true,
				WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory
			};

			using (Process process = new Process { StartInfo = startInfo })
			{
				process.Start();

				// Write any expected input (e.g. ssh-keygen's empty passphrase, twice) then CLOSE stdin: any further
				// prompt gets EOF and the tool fails fast instead of freezing the editor on WaitForExit forever.
				if (!string.IsNullOrEmpty(stdin)) process.StandardInput.Write(stdin);
				process.StandardInput.Close();

				// Drain both pipes concurrently (sequential ReadToEnd can deadlock if the other buffer fills).
				System.Threading.Tasks.Task<string> outTask = process.StandardOutput.ReadToEndAsync();
				System.Threading.Tasks.Task<string> errTask = process.StandardError.ReadToEndAsync();

				if (!process.WaitForExit(timeoutMs))
				{
					try { process.Kill(); } catch (Exception) { /* best effort */ }
					stdout = "";
					stderr = "'" + exe + "' did not finish within " + (timeoutMs / 1000) + "s and was killed (it likely prompted for input).";
					return -1;
				}

				stdout = outTask.GetAwaiter().GetResult();
				stderr = errTask.GetAwaiter().GetResult();
				return process.ExitCode;
			}
		}

		/// <summary>Best-effort check that an executable is launchable (used for keytool / cm / ssh-keygen presence).</summary>
		public static bool OnPath(string exe)
		{
			try
			{
				ProcessStartInfo info = new ProcessStartInfo
				{
					FileName = exe,
					Arguments = "-help",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};

				using (Process process = new Process { StartInfo = info })
				{
					process.Start();
					process.StandardOutput.ReadToEnd();
					process.StandardError.ReadToEnd();
					process.WaitForExit();
					return true;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}

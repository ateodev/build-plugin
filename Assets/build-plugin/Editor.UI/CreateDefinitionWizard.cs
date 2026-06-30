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
	/// (the concrete <see cref="BuildDefinition"/> subclass), name it (unique under Assets/BuildConfigs/), choose
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

		[BoxGroup("Target"), EnumToggleButtons, HideLabel, PropertyOrder(0)]
		[SerializeField, Tooltip("The build target. The concrete BuildDefinition subclass is chosen from this - it IS the platform.")]
		private BuildPlatform _target = BuildPlatform.Android;

		[BoxGroup("Target"), PropertyOrder(1)]
		[InfoBox("All targets are offered. A target whose team executor / editor module isn't provisioned can still " +
			"be authored - availability surfaces on the definition's Build tab, not here.", InfoMessageType.None)]
		[ShowInInspector, DisplayAsString, LabelText("Creates")]
		private string CreatesTypeName => TypeForTarget(_target)?.Name ?? "(none)";

		[BoxGroup("Name"), HideLabel, PropertyOrder(2)]
		[SerializeField, Tooltip("Unique definition name. Becomes Assets/BuildConfigs/<name>.asset and the unitybuild.definition parameter.")]
		private string _definitionName = "";

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
		[SerializeField, Tooltip("Default branch this definition builds. Dropdown is the repo's branches; you may also type one freely (git fallback).")]
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

		[PropertyOrder(20), PropertySpace(8)]
		[Button("Create", ButtonSizes.Large)]
		private void Create()
		{
			_result = "";

			if (string.IsNullOrWhiteSpace(_definitionName))
			{
				_result = "Enter a definition name.";
				return;
			}

			Type type = TypeForTarget(_target);
			if (type == null)
			{
				_result = "No definition subclass maps to target '" + _target + "'.";
				return;
			}

			EnsureBuildConfigsFolder();
			string assetPath = "Assets/BuildConfigs/" + _definitionName + ".asset";
			if (AssetDatabase.LoadAssetAtPath<BuildDefinition>(assetPath) != null || File.Exists(assetPath))
			{
				_result = "A definition named '" + _definitionName + "' already exists. Names must be unique.";
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

			if (_owner != null) _owner.RefreshAndSelect(_definitionName);

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
			SetField(definition, "_definitionName", _definitionName);
			SetField(definition, "_defaultBranch", string.IsNullOrEmpty(_defaultBranch) ? "main" : _defaultBranch);

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
			string relativePath = "keystores/" + _definitionName + ".keystore";
			string keystorePath = Path.Combine(BuildPanel.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

			bool generated = GenerateKeystore(keystorePath);

			ProjectConfig project = _owner != null ? _owner.Project : null;
			string projectKey = project != null ? project.ProjectKey : "project";
			ISecretProvider provider = SecretProviders.ForProject(project);
			string item = projectKey + "-android-signing";

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
			List<string> parts = new List<string>();
			AddDnPart(parts, "CN", string.IsNullOrEmpty(_dnCommonName) ? _definitionName : _dnCommonName);
			AddDnPart(parts, "OU", _dnOrgUnit);
			AddDnPart(parts, "O", _dnOrg);
			AddDnPart(parts, "L", _dnCity);
			AddDnPart(parts, "ST", _dnState);
			AddDnPart(parts, "C", _dnCountry);
			return parts.Count > 0 ? string.Join(", ", parts) : "CN=" + _definitionName;
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

		private void ProvisionSecret(ISecretProvider provider, string item, string field, SecretValue value, SecretKind kind,
			string logicalKey, string description)
		{
			string reference = OnePasswordProvider.SchemeName + "://" + OnePasswordProvider.DefaultVault + "/" + item + "/" + field;

			if (provider != null)
			{
				try
				{
					SecretRef created = provider.CreateOrUpdateAsync(item, field, value).GetAwaiter().GetResult();
					if (!string.IsNullOrEmpty(created.Reference)) reference = created.Reference;
				}
				catch (Exception exception)
				{
					Debug.LogWarning("[New Definition] Secret provider could not store '" + item + "/" + field +
						"' (" + exception.Message + "). Recording the reference only - create the secret manually (TODO).");
				}
			}

			RegisterSecret(logicalKey, description, kind, reference);
		}

		private void RegisterSecret(string logicalKey, string description, SecretKind kind, string reference)
		{
			ProjectConfig project = _owner != null ? _owner.Project : null;
			if (project == null) return;

			List<SecretDeclaration> registry = GetRegistry(project);
			if (registry == null) return;

			foreach (SecretDeclaration existing in registry)
			{
				if (existing != null && existing.LogicalKey == logicalKey) return; // already registered
			}

			registry.Add(new SecretDeclaration(logicalKey, description, kind, reference, new[] { _definitionName }));
			EditorUtility.SetDirty(project);
			AssetDatabase.SaveAssetIfDirty(project);
		}

		private static List<SecretDeclaration> GetRegistry(ProjectConfig project)
		{
			FieldInfo field = typeof(ProjectConfig).GetField("_secretRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
			return field?.GetValue(project) as List<SecretDeclaration>;
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
						string branch = line.Trim();
						if (branch.Length > 0 && !_branches.Contains(branch)) _branches.Add(branch);
					}

					_gitOk = true;
				}

				int headExit = WizardShell.Run("git", "rev-parse --abbrev-ref HEAD", BuildPanel.ProjectRoot, out string head, out _);
				if (headExit == 0 && !string.IsNullOrWhiteSpace(head) && string.IsNullOrEmpty(_defaultBranch))
				{
					_defaultBranch = head.Trim();
				}
			}
			catch (Exception exception)
			{
				Debug.Log("[New Definition] git branch enumeration failed (" + exception.Message + ") - using a free-text branch field.");
			}

			if (string.IsNullOrEmpty(_defaultBranch)) _defaultBranch = _gitOk ? _defaultBranch : "main";
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

		private static void EnsureBuildConfigsFolder()
		{
			if (!AssetDatabase.IsValidFolder("Assets/BuildConfigs"))
			{
				AssetDatabase.CreateFolder("Assets", "BuildConfigs");
			}
		}

		private static Type TypeForTarget(BuildPlatform target)
		{
			switch (target)
			{
				case BuildPlatform.Android:           return typeof(AndroidBuildDefinition);
				case BuildPlatform.iOS:               return typeof(iOSBuildDefinition);
				case BuildPlatform.WindowsStandalone: return typeof(WindowsBuildDefinition);
				case BuildPlatform.MacStandalone:     return typeof(MacOSBuildDefinition);
				case BuildPlatform.LinuxStandalone:   return typeof(LinuxBuildDefinition);
				case BuildPlatform.LinuxServer:       return typeof(ServerBuildDefinition);
				case BuildPlatform.WebGL:             return typeof(WebGLBuildDefinition);
				default:                              return null;
			}
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
		public static int Run(string exe, string arguments, string workingDirectory, out string stdout, out string stderr)
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory
			};

			using (Process process = new Process { StartInfo = startInfo })
			{
				process.Start();
				stdout = process.StandardOutput.ReadToEnd();
				stderr = process.StandardError.ReadToEnd();
				process.WaitForExit();
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

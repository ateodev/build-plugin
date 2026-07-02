using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif

namespace Ateo.Build
{
	/// <summary>
	/// One buildable thing ("Android AAB Release", "iOS Xcode", "Android APK Cheat"), authored in the Editor
	/// and COMMITTED to the game repo under Assets/BuildConfigs/. The CI side never grows a config per
	/// variant - it passes this definition's name as the <c>unitybuild.definition</c> build parameter, and
	/// <see cref="BuildRunner"/> loads and applies it. Wraps a Unity 6 Build Profile (preferred) and adds what
	/// profiles don't cover: output naming/versioning, signing references, an ordered list of pre/post steps,
	/// and an optional named-method shim for a game's existing headless builder.
	///
	/// ABSTRACT base of a polymorphic hierarchy: every definition is its own asset whose CONCRETE TYPE is the
	/// target platform (see <see cref="Platform"/>) - so there is no platform enum field, the type drives
	/// executor resolution, and target-specific data (Android keystore + AAB/APK, iOS signing, ...) lives on
	/// the concrete leaf subclass. Carries only the fields shared across every target.
	/// </summary>
	public abstract class BuildDefinition : ScriptableObject
	{
		#region Fields

		[SerializeField, Tooltip("Unique name. Passed as the unitybuild.definition build parameter and used to locate this asset.")]
		private string _definitionName;

		[SerializeField, Tooltip("Override the Unity editor version for THIS definition only (e.g. \"6000.0.58f2\"). " +
			"Wins over the Project Config's Unity Version and ProjectSettings/ProjectVersion.txt. Empty = inherit those. " +
			"Use when one definition must target a different editor than the rest of the project.")]
		private string _unityVersion;

#if UNITY_6000_0_OR_NEWER
		[SerializeField, Tooltip("Unity 6 Build Profile to build (preferred). If set, platform/scenes/defines/player-settings come from it.")]
		private BuildProfile _buildProfile;
#endif

		[SerializeField, Tooltip("Legacy fallback when no Build Profile is set: explicit scene list. Empty = use EditorBuildSettings.")]
		private SceneAsset[] _scenes;

		[SerializeField, Tooltip("Scripting defines to ADD for this build. Built-in path only, and only when NO Build " +
			"Profile is set (profiles carry their own defines). The original define set is restored after the build.")]
		private string[] _includeDefines;

		[SerializeField, Tooltip("Scripting defines to REMOVE for this build. Same scope/restore as Include Defines.")]
		private string[] _excludeDefines;

		[SerializeField, Tooltip("Output file name without extension. Tokens: {project} {version} {code}. Empty = builder default.")]
		private string _outputFileName;

		[SerializeField, Tooltip("Ordered steps run BEFORE the player build.")]
		private List<BuildStep> _preSteps = new List<BuildStep>();

		[SerializeField, Tooltip("Ordered steps run AFTER the player build.")]
		private List<BuildStep> _postSteps = new List<BuildStep>();

		[SerializeReference, Tooltip("Ordered post-build actions (v2 contract) run on the built artifact - typed " +
			"file-based pipeline keyed by Consumes/Produces, separate from the BuildStep lists.")]
		private List<PostBuildAction> _postBuildActions = new();

		[SerializeField, Tooltip("Default VCS branch this definition builds when no commit/changeset override is given. " +
			"A bare branch name (e.g. \"main\", no origin/ prefix); empty = the repo's default branch.")]
		private string _defaultBranch;

		[SerializeField, Tooltip("Scheme-tagged notification target for THIS definition only, e.g. slack:C0123ABC456. " +
			"Wins over the Project Config's Notification Target; empty = inherit it.")]
		private string _notificationTargetOverride;

		[SerializeField, Tooltip("Fully-qualified static method (e.g. \"AndroidBuilder.BuildFromCommandLine\") to call " +
			"INSTEAD of the built-in build. Lets a game's existing headless builder plug in unchanged. Empty = built-in.")]
		private string _buildMethod;

		[SerializeField, Tooltip("Raw extra args passed through to the build-method shim / executeMethod.")]
		private string _buildMethodArgs;

		#endregion

		#region Properties

		public string DefinitionName => _definitionName;

		/// <summary>Per-definition Unity editor version override; empty = inherit Project Config / ProjectVersion.txt.</summary>
		public string UnityVersion => _unityVersion;
#if UNITY_6000_0_OR_NEWER
		public BuildProfile Profile => _buildProfile;
#endif
		public IReadOnlyList<SceneAsset> Scenes => _scenes;
		public IReadOnlyList<string> IncludeDefines => _includeDefines;
		public IReadOnlyList<string> ExcludeDefines => _excludeDefines;
		public string OutputFileName => _outputFileName;
		public IReadOnlyList<BuildStep> PreSteps => _preSteps;
		public IReadOnlyList<BuildStep> PostSteps => _postSteps;
		public IReadOnlyList<PostBuildAction> PostBuildActions => _postBuildActions;
		public string DefaultBranch => _defaultBranch;

		/// <summary>Per-definition notification target override; empty = inherit the Project Config's target.</summary>
		public string NotificationTargetOverride => _notificationTargetOverride;
		public string BuildMethod => _buildMethod;
		public string BuildMethodArgs => _buildMethodArgs;
		public bool UsesGameBuilder => !string.IsNullOrEmpty(_buildMethod);

		/// <summary>The target platform - the concrete subclass IS the platform (replaces the old enum field).</summary>
		public abstract BuildPlatform Platform { get; }

		/// <summary>The kind of artifact this definition's build produces - seeds the post-build-action pipeline.</summary>
		public abstract ArtifactKind OutputKind { get; }

		#endregion

		#region Public Methods

		/// <summary>Scene paths to build: explicit <see cref="Scenes"/> if set, else enabled EditorBuildSettings.</summary>
		public string[] GetScenePaths()
		{
			if (_scenes != null && _scenes.Length > 0)
			{
				List<string> paths = new List<string>(_scenes.Length);
				foreach (SceneAsset scene in _scenes)
				{
					if (scene == null) continue;

					string path = AssetDatabase.GetAssetPath(scene);
					if (!string.IsNullOrEmpty(path)) paths.Add(path);
				}

				return paths.ToArray();
			}

			List<string> enabled = new List<string>();
			foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
			{
				if (scene.enabled) enabled.Add(scene.path);
			}

			return enabled.ToArray();
		}

		#endregion
	}
}

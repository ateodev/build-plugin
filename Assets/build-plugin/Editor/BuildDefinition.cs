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
	/// </summary>
	[CreateAssetMenu(menuName = "Build/Build Definition", fileName = "NewBuildDefinition", order = 0)]
	public sealed class BuildDefinition : ScriptableObject
	{
		#region Fields

		[SerializeField, Tooltip("Unique name. Passed as the unitybuild.definition build parameter and used to locate this asset.")]
		private string _definitionName;

		[SerializeField, Tooltip("Target platform. Selects the Unity build target and which build-server executor to trigger.")]
		private BuildPlatform _platform = BuildPlatform.Android;

		[SerializeField, Tooltip("Android only: AAB (Play app bundle) or APK (sideloadable).")]
		private AndroidOutput _androidOutput = AndroidOutput.AAB;

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

		[SerializeField, Tooltip("Android signing references (alias + env-var names). Never the secret itself.")]
		private AndroidSigning _androidSigning;

		[SerializeField, Tooltip("Ordered steps run BEFORE the player build.")]
		private List<BuildStep> _preSteps = new List<BuildStep>();

		[SerializeField, Tooltip("Ordered steps run AFTER the player build.")]
		private List<BuildStep> _postSteps = new List<BuildStep>();

		[SerializeField, Tooltip("Default VCS branch this definition builds when no commit/changeset override is given.")]
		private string _defaultBranch;

		[SerializeField, Tooltip("Fully-qualified static method (e.g. \"AndroidBuilder.BuildFromCommandLine\") to call " +
			"INSTEAD of the built-in build. Lets a game's existing headless builder plug in unchanged. Empty = built-in.")]
		private string _buildMethod;

		[SerializeField, Tooltip("Raw extra args passed through to the build-method shim / executeMethod.")]
		private string _buildMethodArgs;

		#endregion

		#region Properties

		public string DefinitionName => _definitionName;
		public BuildPlatform Platform => _platform;
		public AndroidOutput Output => _androidOutput;
#if UNITY_6000_0_OR_NEWER
		public BuildProfile Profile => _buildProfile;
#endif
		public IReadOnlyList<SceneAsset> Scenes => _scenes;
		public IReadOnlyList<string> IncludeDefines => _includeDefines;
		public IReadOnlyList<string> ExcludeDefines => _excludeDefines;
		public string OutputFileName => _outputFileName;
		public AndroidSigning Signing => _androidSigning;
		public IReadOnlyList<BuildStep> PreSteps => _preSteps;
		public IReadOnlyList<BuildStep> PostSteps => _postSteps;
		public string DefaultBranch => _defaultBranch;
		public string BuildMethod => _buildMethod;
		public string BuildMethodArgs => _buildMethodArgs;
		public bool UsesGameBuilder => !string.IsNullOrEmpty(_buildMethod);

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

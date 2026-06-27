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
    /// variant - it passes this definition's <see cref="definitionName"/> as the <c>unitybuild.definition</c>
    /// build parameter, and <see cref="BuildRunner"/> loads and applies it. Wraps a Unity 6 Build Profile
    /// (preferred) and adds what profiles don't cover: output naming/versioning, signing references, an
    /// ordered list of pre/post steps, extra layered defines, and an optional named-method shim for a game's
    /// existing headless builder.
    /// </summary>
    [CreateAssetMenu(menuName = "Build/Build Definition", fileName = "NewBuildDefinition", order = 0)]
    public class BuildDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Unique name. Passed as the unitybuild.definition build parameter and used to locate this asset.")]
        public string definitionName;

        [Tooltip("Target platform. Selects the Unity build target and which build-server executor to trigger.")]
        public BuildPlatform platform = BuildPlatform.Android;

        [Tooltip("Android only: AAB (Play app bundle) or APK (sideloadable).")]
        public AndroidOutput androidOutput = AndroidOutput.AAB;

        [Header("What to build (L0)")]
#if UNITY_6000_0_OR_NEWER
        [Tooltip("Unity 6 Build Profile to build (preferred). If set, platform/scenes/defines/player-settings come from it.")]
        public BuildProfile buildProfile;
#endif
        [Tooltip("Legacy fallback when no Build Profile is set: explicit scene list. Empty = use EditorBuildSettings.")]
        public SceneAsset[] scenes;

        [Tooltip("Extra scripting define symbols layered on top of the profile/project (e.g. CHEATS, TEST_CONFIG).")]
        public string[] extraScriptingDefines;

        [Header("Output")]
        [Tooltip("Output file name without extension. Tokens: {project} {version} {code}. Empty = builder default.")]
        public string outputFileName;

        [Header("Signing (references only - never the secret)")]
        public AndroidSigning androidSigning;

        [Header("Steps (extensibility)")]
        [Tooltip("Ordered steps run BEFORE the player build.")]
        public List<BuildStep> preSteps = new List<BuildStep>();
        [Tooltip("Ordered steps run AFTER the player build.")]
        public List<BuildStep> postSteps = new List<BuildStep>();

        [Header("Source / CI")]
        [Tooltip("Default VCS branch this definition builds when no commit/changeset override is given.")]
        public string defaultBranch;

        [Header("Build method shim (migration escape hatch)")]
        [Tooltip("Fully-qualified static method (e.g. \"AndroidBuilder.BuildFromCommandLine\") to call INSTEAD " +
                 "of the built-in build. Lets a game's existing headless builder plug in unchanged. Empty = built-in.")]
        public string buildMethod;
        [Tooltip("Raw extra args passed through to the build-method shim / executeMethod.")]
        public string buildMethodArgs;

        public bool UsesGameBuilder => !string.IsNullOrEmpty(buildMethod);

        /// <summary>Scene paths to build: explicit <see cref="scenes"/> if set, else enabled EditorBuildSettings.</summary>
        public string[] GetScenePaths()
        {
            if (scenes != null && scenes.Length > 0)
            {
                var paths = new List<string>(scenes.Length);
                foreach (var s in scenes)
                {
                    if (s == null) continue;
                    var p = AssetDatabase.GetAssetPath(s);
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
                return paths.ToArray();
            }

            var enabled = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
                if (scene.enabled) enabled.Add(scene.path);
            return enabled.ToArray();
        }
    }
}

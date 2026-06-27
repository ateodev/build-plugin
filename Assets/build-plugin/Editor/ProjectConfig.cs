using UnityEngine;

namespace Ateo.Build
{
    /// <summary>How this project's source is version-controlled (documentation + the in-editor panel's use).</summary>
    public enum VcsKind { Git, Plastic }

    /// <summary>
    /// Project-wide build configuration: settings shared by every <see cref="BuildDefinition"/> in this
    /// project. Committed under Assets/BuildConfigs/. Holds NON-SECRET, project-level facts only. The
    /// authoritative repo URL + credentials live agent-side on the build server (fixed at onboarding,
    /// keyed by <see cref="gameToken"/>); the values here are for the plugin's local use, the panel, and
    /// human reference.
    /// </summary>
    [CreateAssetMenu(menuName = "Build/Project Config", fileName = "BuildProjectConfig", order = 1)]
    public class ProjectConfig : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Game token - the JOIN KEY. Must equal the build server's agent-side record token; the " +
                 "server resolves repo, credentials, signing secrets, license and checkout dir from it.")]
        public string gameToken;

        [Header("Version control (reference; authoritative copy is agent-side)")]
        public VcsKind vcs = VcsKind.Git;
        [Tooltip("Repo URL (git remote or Plastic repo spec). Documentation + local use only.")]
        public string repoUrl;

        [Header("Build server")]
        [Tooltip("Trust-boundary team this project belongs to (resolves which TeamCity subtree/executors).")]
        public string teamId;
        [Tooltip("TeamCity base URL the in-editor panel talks to, e.g. https://build.ateonet.work")]
        public string serverBaseUrl = "https://build.ateonet.work";

        [Header("Unity")]
        [Tooltip("Pinned Unity version. Empty = read from ProjectSettings/ProjectVersion.txt.")]
        public string unityVersion;
    }
}

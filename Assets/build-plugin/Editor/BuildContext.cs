namespace Ateo.Build
{
    /// <summary>
    /// State threaded through a build: the definition being built, the resolved project config, and the
    /// values computed before the player build (output path, stamped version). Passed to every pre/post
    /// <see cref="BuildStep"/> so steps can read context and contribute to the artifact.
    /// </summary>
    public class BuildContext
    {
        public BuildDefinition definition;
        public ProjectConfig project;

        /// <summary>True when running headless under CI (batchmode), false for an in-editor local build.</summary>
        public bool isBatchMode;

        /// <summary>Absolute path the player build will be written to.</summary>
        public string outputPath;

        /// <summary>Marketing version stamped for this build (see <see cref="VersionStamp"/>).</summary>
        public string versionName;

        /// <summary>Version/build code stamped for this build.</summary>
        public int versionCode;

        public BuildPlatform Platform => definition != null ? definition.platform : BuildPlatform.Android;
    }
}

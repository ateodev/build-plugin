using UnityEditor;

namespace Ateo.Build
{
    /// <summary>
    /// Target platform for a build definition. Maps 1:1 onto a Unity <see cref="BuildTarget"/> and to a
    /// canonical token the build server routes on (one TeamCity executor per platform). Output format
    /// variants that share a platform (Android AAB vs APK) are NOT separate platforms - see
    /// <see cref="AndroidOutput"/> on the definition.
    /// </summary>
    public enum BuildPlatform
    {
        Android,
        iOS,
        WindowsStandalone,
        MacStandalone,
        LinuxStandalone,
        LinuxServer,
        WebGL
    }

    /// <summary>Android ships two output formats from one platform; pick one per definition.</summary>
    public enum AndroidOutput
    {
        AAB,
        APK
    }

    public static class BuildPlatformExtensions
    {
        /// <summary>The Unity build target this platform compiles for.</summary>
        public static BuildTarget ToBuildTarget(this BuildPlatform p)
        {
            switch (p)
            {
                case BuildPlatform.Android:           return BuildTarget.Android;
                case BuildPlatform.iOS:               return BuildTarget.iOS;
                case BuildPlatform.WindowsStandalone: return BuildTarget.StandaloneWindows64;
                case BuildPlatform.MacStandalone:     return BuildTarget.StandaloneOSX;
                case BuildPlatform.LinuxStandalone:   return BuildTarget.StandaloneLinux64;
                case BuildPlatform.LinuxServer:       return BuildTarget.StandaloneLinux64;
                case BuildPlatform.WebGL:             return BuildTarget.WebGL;
                default:                              return BuildTarget.NoTarget;
            }
        }

        /// <summary>The build-target group (for scripting-define and player-setting scope).</summary>
        public static BuildTargetGroup ToBuildTargetGroup(this BuildPlatform p)
        {
            switch (p)
            {
                case BuildPlatform.Android: return BuildTargetGroup.Android;
                case BuildPlatform.iOS:     return BuildTargetGroup.iOS;
                case BuildPlatform.WebGL:   return BuildTargetGroup.WebGL;
                default:                    return BuildTargetGroup.Standalone;
            }
        }

        /// <summary>
        /// The canonical token used by the build server (matches the structure doc's platform tokens and
        /// the executor's <c>unitybuild.platform</c> tag). Lets the in-editor panel resolve which TeamCity
        /// executor to trigger.
        /// </summary>
        public static string ToServerToken(this BuildPlatform p)
        {
            switch (p)
            {
                case BuildPlatform.Android:           return "Android";
                case BuildPlatform.iOS:               return "iOS";
                case BuildPlatform.WindowsStandalone: return "WinStandalone";
                case BuildPlatform.MacStandalone:     return "MacStandalone";
                case BuildPlatform.LinuxStandalone:   return "LinuxStandalone";
                case BuildPlatform.LinuxServer:       return "LinuxServer";
                case BuildPlatform.WebGL:             return "WebGL";
                default:                              return p.ToString();
            }
        }
    }
}

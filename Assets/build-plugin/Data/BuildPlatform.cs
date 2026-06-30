using UnityEditor;

namespace Ateo.Build
{
	/// <summary>
	/// Target platform for a build definition - the concrete <see cref="BuildDefinition"/> subclass IS one of
	/// these (the type drives executor resolution; there is no serialized platform field). Each value maps to a
	/// Unity <see cref="BuildTarget"/> (+ a <see cref="StandaloneBuildSubtarget"/> for the dedicated-server
	/// variants) and to a short canonical TOKEN used end to end: the <c>unitybuild.target</c> build param, the
	/// executor's <c>unitybuild.platform</c> capability tag, and the <c>&lt;build-target&gt;</c> segment of the
	/// checkout/output path (<c>&lt;build.root&gt;/&lt;team&gt;/&lt;project&gt;/&lt;token&gt;</c>). The enum
	/// identifier IS the token (see <see cref="BuildPlatformExtensions.ToServerToken"/>), kept short so deep
	/// Android/Gradle output stays under Windows MAX_PATH.
	/// </summary>
	public enum BuildPlatform
	{
		// --- Mobile ---
		Android,
		iOS,

		// --- Desktop standalone (Player subtarget) ---
		Windows,
		Mac,
		Linux,

		// --- Desktop standalone (dedicated Server subtarget) ---
		WindowsServer,
		MacServer,
		LinuxServer,

		// --- Web ---
		WebGL,

		// --- Other first-party ---
		UWP,
		tvOS,
		VisionOS,

		// --- Map-only: recognized in the token<->BuildTarget map but NOT offered as an authorable
		//     definition type (legacy 32-bit; niche headless simulation). ---
		Windows32,
		LinuxSim,

		// --- Console / partner-SDK: recognized for routing + ensure-unity fail-and-notify; never auto-installed. ---
		Switch,
		PS4,
		PS5,
		XboxOne,
		XboxGDKOne,
		XboxSeries
	}

	/// <summary>Android ships two output formats from one platform; pick one per definition.</summary>
	public enum AndroidOutput
	{
		AAB,
		APK
	}

	public static class BuildPlatformExtensions
	{
		#region Public Methods

		/// <summary>
		/// The short canonical token for this platform - the <c>unitybuild.target</c> value, the capability-tag
		/// entry, and the on-disk <c>&lt;build-target&gt;</c> folder. The enum identifier IS the token by design
		/// (so the C# panel and the PowerShell agent agree without a second table to keep in sync).
		/// </summary>
		public static string ToServerToken(this BuildPlatform platform)
		{
			return platform.ToString();
		}

		/// <summary>The Unity build target this platform compiles for. Server variants share the desktop target
		/// and differ only by <see cref="ToSubtarget"/>.</summary>
		public static BuildTarget ToBuildTarget(this BuildPlatform platform)
		{
			switch (platform)
			{
				case BuildPlatform.Android:       return BuildTarget.Android;
				case BuildPlatform.iOS:           return BuildTarget.iOS;
				case BuildPlatform.Windows:       return BuildTarget.StandaloneWindows64;
				case BuildPlatform.Mac:           return BuildTarget.StandaloneOSX;
				case BuildPlatform.Linux:         return BuildTarget.StandaloneLinux64;
				case BuildPlatform.WindowsServer: return BuildTarget.StandaloneWindows64;
				case BuildPlatform.MacServer:     return BuildTarget.StandaloneOSX;
				case BuildPlatform.LinuxServer:   return BuildTarget.StandaloneLinux64;
				case BuildPlatform.WebGL:         return BuildTarget.WebGL;
				case BuildPlatform.UWP:           return BuildTarget.WSAPlayer;
				case BuildPlatform.tvOS:          return BuildTarget.tvOS;
				case BuildPlatform.VisionOS:      return BuildTarget.VisionOS;
				case BuildPlatform.Windows32:     return BuildTarget.StandaloneWindows;
				case BuildPlatform.LinuxSim:      return BuildTarget.LinuxHeadlessSimulation;
				case BuildPlatform.Switch:        return BuildTarget.Switch;
				case BuildPlatform.PS4:           return BuildTarget.PS4;
				case BuildPlatform.PS5:           return BuildTarget.PS5;
				case BuildPlatform.XboxOne:       return BuildTarget.XboxOne;
				case BuildPlatform.XboxGDKOne:    return BuildTarget.GameCoreXboxOne;
				case BuildPlatform.XboxSeries:    return BuildTarget.GameCoreXboxSeries;
				default:                          return BuildTarget.NoTarget;
			}
		}

		/// <summary>The desktop standalone subtarget. <c>Server</c> for the dedicated-server platforms,
		/// <c>Player</c> for everything else (ignored by non-standalone targets).</summary>
		public static StandaloneBuildSubtarget ToSubtarget(this BuildPlatform platform)
		{
			return platform.IsDedicatedServer() ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player;
		}

		/// <summary>True for the headless dedicated-server desktop platforms (Windows/Mac/Linux Server).</summary>
		public static bool IsDedicatedServer(this BuildPlatform platform)
		{
			return platform == BuildPlatform.WindowsServer
				|| platform == BuildPlatform.MacServer
				|| platform == BuildPlatform.LinuxServer;
		}

		/// <summary>True for console / partner-SDK targets that are never auto-installed (fail-and-notify).</summary>
		public static bool IsConsole(this BuildPlatform platform)
		{
			switch (platform)
			{
				case BuildPlatform.Switch:
				case BuildPlatform.PS4:
				case BuildPlatform.PS5:
				case BuildPlatform.XboxOne:
				case BuildPlatform.XboxGDKOne:
				case BuildPlatform.XboxSeries:
					return true;
				default:
					return false;
			}
		}

		/// <summary>The build-target group (for scripting-define and player-setting scope). Derived from the
		/// Unity build target so the obscure groups don't need hand-maintaining.</summary>
		public static BuildTargetGroup ToBuildTargetGroup(this BuildPlatform platform)
		{
			return BuildPipeline.GetBuildTargetGroup(platform.ToBuildTarget());
		}

		#endregion
	}
}

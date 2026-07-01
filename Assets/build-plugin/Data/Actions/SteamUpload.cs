using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Publishes a desktop standalone build to Steam via <c>steamcmd +run_app_build</c>. A CATEGORY action: the
	/// store SDK is opaque in the artifact, so it accepts any of the desktop trio (<see cref="ArtifactKind.WinStandalone"/>
	/// / <see cref="ArtifactKind.MacApp"/> / <see cref="ArtifactKind.LinuxStandalone"/>) via the widened
	/// <see cref="CanConsume"/>. Terminal action; needs <c>steamcmd</c>. Authenticates with the Steam user + password
	/// + a TOTP code (Steam Guard <c>shared_secret</c>), all resolved as String secrets.
	/// </summary>
	[System.Serializable]
	public sealed class SteamUpload : PostBuildAction<StandaloneBuildDefinition>
	{
		#region Constants

		private const string SteamUserKey = "STEAM_USER";
		private const string SteamPassKey = "STEAM_PASS";
		private const string SteamTotpKey = "STEAM_TOTP";

		#endregion

		#region Fields

		[SerializeField, Tooltip("Steam application id (the appID in the app-build VDF).")]
		private string _appId;

		[SerializeField, Tooltip("Path to the steamcmd app-build VDF (relative to the build folder, or absolute).")]
		private string _appBuildVdfPath;

		#endregion

		#region Properties

		public override string DisplayName => "Upload to Steam";
		public override ArtifactKind Consumes => ArtifactKind.WinStandalone;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(SteamUserKey, "Steam build account username.", SecretKind.String),
			new SecretRequirement(SteamPassKey, "Steam build account password.", SecretKind.String),
			new SecretRequirement(SteamTotpKey, "Steam Guard TOTP code (from shared_secret).", SecretKind.String)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.Tool("steamcmd")
		};

		#endregion

		#region Public Methods

		/// <summary>Category action: accepts any desktop standalone build (Win / Mac / Linux) - the store SDK is opaque in the artifact.</summary>
		public override bool CanConsume(IReadOnlyCollection<ArtifactKind> available)
		{
			return available != null &&
				(available.Contains(ArtifactKind.WinStandalone) ||
				 available.Contains(ArtifactKind.MacApp) ||
				 available.Contains(ArtifactKind.LinuxStandalone));
		}

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, StandaloneBuildDefinition def)
		{
			if (string.IsNullOrEmpty(_appBuildVdfPath))
			{
				return ActionResult.Fail("SteamUpload: no app-build VDF configured (_appBuildVdfPath).");
			}

			string vdfPath = Path.IsPathRooted(_appBuildVdfPath)
				? _appBuildVdfPath
				: Path.Combine(ctx.BuildFolder ?? string.Empty, _appBuildVdfPath);
			if (!File.Exists(vdfPath))
			{
				return ActionResult.Fail("SteamUpload: app-build VDF not found at " + vdfPath + ".");
			}

			string user = ctx.GetSecretString(SteamUserKey);
			string pass = ctx.GetSecretString(SteamPassKey);
			string totp = ctx.GetSecretString(SteamTotpKey);

			// CONSTRAINT: steamcmd's only non-interactive login is '+login user pass totp' on argv - it has no env
			// var or credential-file alternative, so the credentials unavoidably appear in the process list for the
			// upload's duration. Mitigate with a dedicated low-privilege Steam build account, not code.
			List<string> args = new List<string>
			{
				"+login", user, pass, totp,
				"+run_app_build", vdfPath,
				"+quit"
			};

			ctx.Log?.Invoke("SteamUpload: steamcmd +run_app_build (app " + _appId + ")");

			ActionProcess.ToolResult result = await ActionProcess.RunAsync("steamcmd", args, ctx.BuildFolder);

			// steamcmd's exit code is unreliable; a successful build prints 'Success.' / 'App build successful'.
			bool reportedSuccess = result.StdOut.Contains("Success") || result.StdOut.Contains("successful");
			if (!result.Succeeded || !reportedSuccess)
			{
				return ActionResult.Fail("SteamUpload: steamcmd failed (exit " + result.ExitCode + "): " + result.Tail(25));
			}

			ActionResult ok = ActionResult.Ok("uploaded build to Steam (app " + _appId + ")");
			ok.Metadata["steamAppId"] = _appId ?? string.Empty;
			return ok;
		}

		#endregion
	}
}

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Turns the Unity-generated Xcode project (<see cref="ArtifactKind.XcodeProject"/>) into a signed
	/// <see cref="ArtifactKind.IPA"/> by driving fastlane <c>gym</c> + read-only <c>match</c> (mirrors the shared
	/// <c>ios build_ipa</c> lane in build-agent-scripts/fastlane/Fastfile). macOS + Xcode + fastlane only. Signing
	/// inputs (team + provisioning profile) come from the definition's <see cref="iOSBuildDefinition.Signing"/>;
	/// the match passphrase + ASC API key arrive as resolved secrets, never from the committed asset.
	/// </summary>
	[System.Serializable]
	public sealed class BuildIPA : PostBuildAction<iOSBuildDefinition>
	{
		#region Constants

		private const string MatchPasswordKey = "MATCH_PASSWORD";
		private const string AscApiKeyKey = "ASC_API_KEY";

		#endregion

		#region Fields

		[SerializeField, Tooltip("Xcode scheme to archive (Unity's app target; default 'Unity-iPhone').")]
		private string _scheme = "Unity-iPhone";

		[SerializeField, Tooltip("Export method: app-store, ad-hoc, development or enterprise.")]
		private string _exportMethod = "app-store";

		[SerializeField, Tooltip("App bundle identifier (e.g. com.ateo.game) - drives match + the export plist.")]
		private string _bundleId;

		#endregion

		#region Properties

		public override string DisplayName => "Build IPA (fastlane)";
		public override ArtifactKind Consumes => ArtifactKind.XcodeProject;
		public override ArtifactKind Produces => ArtifactKind.IPA;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(MatchPasswordKey, "fastlane match repo passphrase.", SecretKind.String),
			new SecretRequirement(AscApiKeyKey, "App Store Connect API key (.p8).", SecretKind.File)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.OS("macOS"),
			HostRequirement.Tool("xcodebuild"),
			HostRequirement.Tool("fastlane")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, iOSBuildDefinition def)
		{
			string xcodeDir = ActionProcess.PrimaryArtifact(ctx);
			if (string.IsNullOrEmpty(xcodeDir) || !Directory.Exists(xcodeDir))
			{
				return ActionResult.Fail("BuildIPA: no Xcode project folder to archive (artifact path '" + xcodeDir + "').");
			}

			iOSSigning signing = def.Signing;
			string matchPassword = ctx.GetSecretString(MatchPasswordKey);
			string ascKeyPath = ctx.GetSecretFilePath(AscApiKeyKey);

			// Hand the lane its inputs via env (matches the Fastfile's ENV.fetch contract).
			Dictionary<string, string> env = new Dictionary<string, string>
			{
				["CI"] = "true",
				["XCODE_DIR"] = xcodeDir,
				["SCHEME"] = string.IsNullOrEmpty(_scheme) ? "Unity-iPhone" : _scheme,
				["EXPORT_METHOD"] = string.IsNullOrEmpty(_exportMethod) ? "app-store" : _exportMethod,
				["BUNDLE_ID"] = _bundleId ?? string.Empty,
				["TEAM_ID"] = signing.AppleTeamId ?? string.Empty,
				["PROVISIONING_PROFILE"] = signing.ProvisioningProfile ?? string.Empty,
				["MATCH_PASSWORD"] = matchPassword ?? string.Empty,
				["APP_STORE_CONNECT_API_KEY_PATH"] = ascKeyPath ?? string.Empty
			};

			ctx.Log?.Invoke("BuildIPA: fastlane gym + match (export=" + env["EXPORT_METHOD"] + ", scheme=" + env["SCHEME"] + ")");

			ActionProcess.ToolResult result = await ActionProcess.RunAsync(
				"fastlane", new[] { "ios", "build_ipa" }, xcodeDir, env);

			if (!result.Succeeded)
			{
				return ActionResult.Fail("BuildIPA: fastlane exited " + result.ExitCode + ": " + result.Tail(20));
			}

			string ipaPath = ActionProcess.FindNewestFile(xcodeDir, "*.ipa")
				?? ActionProcess.FindNewestFile(ctx.BuildFolder, "*.ipa");
			if (string.IsNullOrEmpty(ipaPath))
			{
				return ActionResult.Fail("BuildIPA: fastlane succeeded but no .ipa was found under " + xcodeDir + ".");
			}

			ctx.ArtifactPaths.Add(ipaPath);

			ActionResult ok = ActionResult.Ok("built IPA " + Path.GetFileName(ipaPath));
			ok.Metadata["ipaPath"] = ipaPath;
			ok.Metadata["exportMethod"] = env["EXPORT_METHOD"];
			return ok;
		}

		#endregion
	}
}

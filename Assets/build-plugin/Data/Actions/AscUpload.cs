using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Uploads a signed <see cref="ArtifactKind.IPA"/> to App Store Connect via fastlane - <c>pilot</c>
	/// (TestFlight) or <c>deliver</c> (App Store) - and records a TestFlight note in the build metadata. Terminal
	/// (side-effect) action, macOS only. Authenticates with the App Store Connect API key (<c>.p8</c>) resolved as
	/// a File secret.
	/// </summary>
	[System.Serializable]
	public sealed class AscUpload : PostBuildAction<iOSBuildDefinition>
	{
		#region Constants

		private const string AscApiKeyKey = "ASC_API_KEY";

		#endregion

		#region Fields

		[SerializeField, Tooltip("Upload to TestFlight (fastlane pilot). When off, delivers to the App Store (fastlane deliver).")]
		private bool _testFlight = true;

		#endregion

		#region Properties

		public override string DisplayName => "Upload to App Store Connect";
		public override ArtifactKind Consumes => ArtifactKind.IPA;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(AscApiKeyKey, "App Store Connect API key (.p8).", SecretKind.File)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.OS("macOS")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, iOSBuildDefinition def)
		{
			string ipaPath = ActionProcess.FindArtifact(ctx, ".ipa");
			if (string.IsNullOrEmpty(ipaPath) || !File.Exists(ipaPath))
			{
				return ActionResult.Fail("AscUpload: no .ipa found in the pipeline artifacts to upload.");
			}

			string ascKeyPath = ctx.GetSecretFilePath(AscApiKeyKey);
			string laneAction = _testFlight ? "upload_to_testflight" : "upload_to_app_store";

			List<string> args = new List<string>
			{
				"run", laneAction,
				"ipa:" + ipaPath,
				"api_key_path:" + ascKeyPath
			};

			ctx.Log?.Invoke("AscUpload: fastlane " + laneAction + " (" + Path.GetFileName(ipaPath) + ")");

			Dictionary<string, string> env = new Dictionary<string, string> { ["CI"] = "true" };
			ActionProcess.ToolResult result = await ActionProcess.RunAsync("fastlane", args, ctx.BuildFolder, env);

			if (!result.Succeeded)
			{
				return ActionResult.Fail("AscUpload: fastlane exited " + result.ExitCode + ": " + result.Tail(20));
			}

			string note = _testFlight
				? "Uploaded " + Path.GetFileName(ipaPath) + " to TestFlight."
				: "Delivered " + Path.GetFileName(ipaPath) + " to the App Store.";

			ActionResult ok = ActionResult.Ok(note);
			ok.Metadata["testflight"] = note;
			return ok;
		}

		#endregion
	}
}

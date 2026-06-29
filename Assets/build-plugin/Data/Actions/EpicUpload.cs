using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Publishes a desktop standalone build to the Epic Games Store via the EOS <c>BuildPatchTool</c> (BPT). A
	/// CATEGORY action: the store layout is opaque in the artifact, so it accepts any of the desktop trio
	/// (<see cref="ArtifactKind.WinStandalone"/> / <see cref="ArtifactKind.MacApp"/> /
	/// <see cref="ArtifactKind.LinuxStandalone"/>) via the widened <see cref="CanConsume"/>. Terminal action; needs
	/// <c>BuildPatchTool</c>. Authenticates with a BPT client id + secret (both String secrets); the
	/// organization / product / artifact ids and the upload cloud-dir are author-supplied settings.
	/// </summary>
	[System.Serializable]
	public sealed class EpicUpload : PostBuildAction<StandaloneBuildDefinition>
	{
		#region Constants

		private const string EpicClientIdKey = "EPIC_BPT_CLIENT_ID";
		private const string EpicClientSecretKey = "EPIC_BPT_CLIENT_SECRET";

		#endregion

		#region Fields

		[SerializeField, Tooltip("EOS organization id (the OrganizationId BuildPatchTool uploads under).")]
		private string _organizationId;

		[SerializeField, Tooltip("EOS product id (the ProductId / app this build belongs to).")]
		private string _productId;

		[SerializeField, Tooltip("EOS artifact id (the platform artifact slot the binary is uploaded to).")]
		private string _artifactId;

		[SerializeField, Tooltip("BuildPatchTool cloud-dir: working directory BPT stages chunked upload state in.")]
		private string _cloudDir;

		#endregion

		#region Properties

		public override string DisplayName => "Upload to Epic Games Store";
		public override ArtifactKind Consumes => ArtifactKind.WinStandalone;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(EpicClientIdKey, "EOS BuildPatchTool client id.", SecretKind.String),
			new SecretRequirement(EpicClientSecretKey, "EOS BuildPatchTool client secret.", SecretKind.String)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.Tool("BuildPatchTool")
		};

		#endregion

		#region Public Methods

		/// <summary>Category action: accepts any desktop standalone build (Win / Mac / Linux) - the store layout is opaque in the artifact.</summary>
		public override bool CanConsume(IReadOnlyCollection<ArtifactKind> available)
		{
			return available != null &&
				(available.Contains(ArtifactKind.WinStandalone) ||
				 available.Contains(ArtifactKind.MacApp) ||
				 available.Contains(ArtifactKind.LinuxStandalone));
		}

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, StandaloneBuildDefinition def)
		{
			string buildRoot = ctx.BuildFolder;
			if (string.IsNullOrEmpty(buildRoot) || !Directory.Exists(buildRoot))
			{
				return ActionResult.Fail("EpicUpload: build folder not found at " + (buildRoot ?? "(null)") + ".");
			}

			if (string.IsNullOrEmpty(_organizationId) || string.IsNullOrEmpty(_productId) || string.IsNullOrEmpty(_artifactId))
			{
				return ActionResult.Fail("EpicUpload: organization / product / artifact id must all be set.");
			}

			string clientId = ctx.GetSecretString(EpicClientIdKey);
			string clientSecret = ctx.GetSecretString(EpicClientSecretKey);
			string cloudDir = string.IsNullOrEmpty(_cloudDir)
				? Path.Combine(Path.GetTempPath(), "ateo-bpt-cloud")
				: _cloudDir;
			string buildVersion = string.IsNullOrEmpty(ctx.VersionName)
				? ctx.VersionCode.ToString()
				: ctx.VersionName + "+" + ctx.VersionCode;

			List<string> args = new List<string>
			{
				"-mode=UploadBinary",
				"-OrganizationId=" + _organizationId,
				"-ProductId=" + _productId,
				"-ArtifactId=" + _artifactId,
				"-ClientId=" + clientId,
				"-ClientSecret=" + clientSecret,
				"-CloudDir=" + cloudDir,
				"-BuildRoot=" + buildRoot,
				"-BuildVersion=" + buildVersion
			};

			ctx.Log?.Invoke("EpicUpload: BuildPatchTool UploadBinary (artifact " + _artifactId + ", version " + buildVersion + ")");

			ActionProcess.ToolResult result = await ActionProcess.RunAsync("BuildPatchTool", args, buildRoot);

			if (!result.Succeeded)
			{
				return ActionResult.Fail("EpicUpload: BuildPatchTool failed (exit " + result.ExitCode + "): " + result.Tail(25));
			}

			ActionResult ok = ActionResult.Ok("uploaded build to Epic (artifact " + _artifactId + ")");
			ok.Metadata["epicArtifactId"] = _artifactId ?? string.Empty;
			ok.Metadata["epicBuildVersion"] = buildVersion;
			return ok;
		}

		#endregion
	}
}

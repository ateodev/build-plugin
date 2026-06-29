using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Notarizes a macOS <see cref="ArtifactKind.MacApp"/> and staples the ticket so it launches on other Macs
	/// without a Gatekeeper prompt. A transform action (<c>MacApp</c> -> notarized <c>MacApp</c>, same path): zips
	/// the .app (<c>ditto</c>), submits with <c>xcrun notarytool submit --wait</c>, then <c>xcrun stapler staple</c>
	/// the original bundle. macOS only (<c>xcrun</c> drives notarytool/stapler). Authenticates with the App Store
	/// Connect API key (<c>.p8</c>) resolved as a File secret, plus its key id + issuer id.
	/// </summary>
	[System.Serializable]
	public sealed class Notarize : PostBuildAction<MacOSBuildDefinition>
	{
		#region Constants

		private const string AscApiKeyKey = "ASC_API_KEY";

		#endregion

		#region Fields

		[SerializeField, Tooltip("App Store Connect API key id (the '-key-id' notarytool needs alongside the .p8).")]
		private string _ascKeyId;

		[SerializeField, Tooltip("App Store Connect issuer id (the '-issuer' notarytool needs).")]
		private string _ascIssuerId;

		#endregion

		#region Properties

		public override string DisplayName => "Notarize (notarytool)";
		public override ArtifactKind Consumes => ArtifactKind.MacApp;
		public override ArtifactKind Produces => ArtifactKind.MacApp;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(AscApiKeyKey, "App Store Connect API key (.p8) for notarytool.", SecretKind.File)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.OS("macOS"),
			HostRequirement.Tool("xcrun")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, MacOSBuildDefinition def)
		{
			string appPath = ActionProcess.FindArtifact(ctx, ".app");
			if (string.IsNullOrEmpty(appPath) || !Directory.Exists(appPath))
			{
				return ActionResult.Fail("Notarize: no .app bundle found in the pipeline artifacts to notarize.");
			}

			string keyPath = ctx.GetSecretFilePath(AscApiKeyKey);
			string workDir = ctx.BuildFolder ?? Path.GetDirectoryName(appPath);

			// notarytool takes a zip/dmg/pkg, not a bare .app - ditto preserves the bundle's symlinks/permissions.
			string zipPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(appPath) + "-notarize.zip");
			if (File.Exists(zipPath)) File.Delete(zipPath);

			ctx.Log?.Invoke("Notarize: ditto -> zip " + Path.GetFileName(appPath));
			ActionProcess.ToolResult ditto = await ActionProcess.RunAsync(
				"ditto", new[] { "-c", "-k", "--keepParent", appPath, zipPath }, workDir);
			if (!ditto.Succeeded)
			{
				return ActionResult.Fail("Notarize: ditto zip failed (exit " + ditto.ExitCode + "): " + ditto.Tail(20));
			}

			ctx.Log?.Invoke("Notarize: xcrun notarytool submit --wait");
			ActionProcess.ToolResult submit = await ActionProcess.RunAsync(
				"xcrun",
				new[]
				{
					"notarytool", "submit", zipPath,
					"--key", keyPath,
					"--key-id", _ascKeyId ?? string.Empty,
					"--issuer", _ascIssuerId ?? string.Empty,
					"--wait"
				},
				workDir);
			if (!submit.Succeeded || submit.StdOut.Contains("status: Invalid"))
			{
				return ActionResult.Fail("Notarize: notarytool submit failed (exit " + submit.ExitCode + "): " + submit.Tail(25));
			}

			ctx.Log?.Invoke("Notarize: xcrun stapler staple");
			ActionProcess.ToolResult staple = await ActionProcess.RunAsync(
				"xcrun", new[] { "stapler", "staple", appPath }, workDir);
			if (!staple.Succeeded)
			{
				return ActionResult.Fail("Notarize: stapler staple failed (exit " + staple.ExitCode + "): " + staple.Tail(20));
			}

			// The notarized .app is the same path - it stays the current MacApp artifact (no new path to add).
			ActionResult ok = ActionResult.Ok("notarized + stapled " + Path.GetFileName(appPath));
			ok.Metadata["notarized"] = "true";
			return ok;
		}

		#endregion
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Uploads a signed <see cref="ArtifactKind.IPA"/> to App Store Connect via fastlane - <c>pilot</c>
	/// (TestFlight) or <c>deliver</c> (App Store) - and records a TestFlight note in the build metadata. Terminal
	/// (side-effect) action, macOS only. Authenticates with the App Store Connect API key: fastlane's
	/// <c>api_key_path</c> expects fastlane's own JSON key DESCRIPTOR ({key_id, issuer_id, key}), NOT the raw
	/// <c>.p8</c>, so the action composes that JSON transiently from its three declared secrets (key id, issuer
	/// id, the .p8 content base64'd) and wipes it in a finally.
	/// </summary>
	[System.Serializable]
	public sealed class AscUpload : PostBuildAction<iOSBuildDefinition>
	{
		#region Constants

		private const string AscApiKeyKey = "ASC_API_KEY";
		private const string AscKeyIdKey = "ASC_KEY_ID";
		private const string AscIssuerIdKey = "ASC_ISSUER_ID";

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
			new SecretRequirement(AscApiKeyKey, "App Store Connect API key (.p8).", SecretKind.File),
			new SecretRequirement(AscKeyIdKey, "App Store Connect API key id.", SecretKind.String),
			new SecretRequirement(AscIssuerIdKey, "App Store Connect API issuer id.", SecretKind.String)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.OS("macOS"),
			HostRequirement.Tool("fastlane")
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

			string keyId = ctx.GetSecretString(AscKeyIdKey);
			string issuerId = ctx.GetSecretString(AscIssuerIdKey);

			// Read the .p8 bytes straight off the resolved secret instead of GetSecretFilePath: fastlane never
			// needs the raw .p8 on disk, only the composed JSON descriptor below - one fewer key file to wipe.
			if (ctx.Secrets == null || !ctx.Secrets.TryGetValue(AscApiKeyKey, out SecretValue apiKey) ||
				apiKey == null || !apiKey.IsFile)
			{
				return ActionResult.Fail("AscUpload: secret '" + AscApiKeyKey + "' was not resolved as a File secret.");
			}

			// fastlane's api_key_path wants its JSON key descriptor, not the .p8 - handing it the raw key fails
			// at JSON parse time. The key content goes in base64 (is_key_content_base64) so the PEM's newlines
			// never need JSON escaping. in_house=false: standard developer accounts, no Apple Enterprise program.
			string keyJson = "{"
				+ "\"key_id\":" + JsonString(keyId) + ","
				+ "\"issuer_id\":" + JsonString(issuerId) + ","
				+ "\"key\":" + JsonString(Convert.ToBase64String(apiKey.FileBytes ?? Array.Empty<byte>())) + ","
				+ "\"is_key_content_base64\":true,"
				+ "\"in_house\":false"
				+ "}";

			string keyJsonPath = Path.Combine(Path.GetTempPath(), "ateo-asc-key-" + Guid.NewGuid().ToString("N") + ".json");
			string laneAction = _testFlight ? "upload_to_testflight" : "upload_to_app_store";

			try
			{
				File.WriteAllText(keyJsonPath, keyJson);

				List<string> args = new List<string>
				{
					"run", laneAction,
					"ipa:" + ipaPath,
					"api_key_path:" + keyJsonPath
				};

				ctx.Log?.Invoke("AscUpload: fastlane " + laneAction + " (" + Path.GetFileName(ipaPath) + ")");

				Dictionary<string, string> env = new Dictionary<string, string> { ["CI"] = "true" };
				ActionProcess.ToolResult result = await ActionProcess.RunAsync("fastlane", args, ctx.BuildFolder, env);

				if (!result.Succeeded)
				{
					return ActionResult.Fail("AscUpload: fastlane exited " + result.ExitCode + ": " + result.Tail(20));
				}
			}
			finally
			{
				// The composed descriptor CONTAINS the private key - wipe it however the upload ended.
				try
				{
					if (File.Exists(keyJsonPath)) File.Delete(keyJsonPath);
				}
				catch (Exception)
				{
					// Best-effort: leave it for OS temp cleanup rather than fail the pipeline's teardown.
				}
			}

			string note = _testFlight
				? "Uploaded " + Path.GetFileName(ipaPath) + " to TestFlight."
				: "Delivered " + Path.GetFileName(ipaPath) + " to the App Store.";

			ActionResult ok = ActionResult.Ok(note);
			ok.Metadata["testflight"] = note;
			return ok;
		}

		#endregion

		#region Private Methods

		/// <summary>Minimal JSON string encoder (quote + escape) - enough for the key descriptor's simple values.</summary>
		private static string JsonString(string value)
		{
			StringBuilder builder = new StringBuilder("\"");
			foreach (char c in value ?? string.Empty)
			{
				switch (c)
				{
					case '"':
						builder.Append("\\\"");
						break;
					case '\\':
						builder.Append("\\\\");
						break;
					case '\n':
						builder.Append("\\n");
						break;
					case '\r':
						builder.Append("\\r");
						break;
					case '\t':
						builder.Append("\\t");
						break;
					default:
						if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
						else builder.Append(c);
						break;
				}
			}

			return builder.Append('"').ToString();
		}

		#endregion
	}
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Publishes an Android <see cref="ArtifactKind.AAB"/> (or, via the widened <see cref="CanConsume"/>, an
	/// <see cref="ArtifactKind.APK"/>) to a Google Play track using fastlane <c>supply</c>. Terminal action; runs
	/// anywhere (no host requirement). Authenticates with the Play service-account JSON resolved as a File secret.
	/// </summary>
	[System.Serializable]
	public sealed class GooglePlayUpload : PostBuildAction<AndroidBuildDefinition>
	{
		#region Constants

		private const string PlayServiceAccountKey = "PLAY_SERVICE_ACCOUNT_JSON";

		#endregion

		#region Fields

		[SerializeField, Tooltip("Play release track: internal, alpha, beta or production.")]
		private string _track = "internal";

		[SerializeField, Tooltip("Application id / package name (e.g. com.ateo.game). Optional - supply can infer it from the bundle.")]
		private string _packageName;

		#endregion

		#region Properties

		public override string DisplayName => "Upload to Google Play";
		public override ArtifactKind Consumes => ArtifactKind.AAB;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(PlayServiceAccountKey, "Google Play service-account JSON.", SecretKind.File)
		};

		#endregion

		#region Public Methods

		/// <summary>Category action: accepts either the primary AAB or an APK produced earlier (e.g. by ExtractApk).</summary>
		public override bool CanConsume(IReadOnlyCollection<ArtifactKind> available)
		{
			return available != null && (available.Contains(ArtifactKind.AAB) || available.Contains(ArtifactKind.APK));
		}

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, AndroidBuildDefinition def)
		{
			string bundlePath = ActionProcess.FindArtifact(ctx, ".aab");
			bool isAab = !string.IsNullOrEmpty(bundlePath);
			if (!isAab) bundlePath = ActionProcess.FindArtifact(ctx, ".apk");

			if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
			{
				return ActionResult.Fail("GooglePlayUpload: no .aab or .apk found in the pipeline artifacts to upload.");
			}

			string jsonKeyPath = ctx.GetSecretFilePath(PlayServiceAccountKey);
			string track = string.IsNullOrEmpty(_track) ? "internal" : _track;

			List<string> args = new List<string>
			{
				"run", "supply",
				"track:" + track,
				"json_key:" + jsonKeyPath,
				(isAab ? "aab:" : "apk:") + bundlePath
			};
			if (!string.IsNullOrEmpty(_packageName)) args.Add("package_name:" + _packageName);

			ctx.Log?.Invoke("GooglePlayUpload: fastlane supply (track=" + track + ", " + Path.GetFileName(bundlePath) + ")");

			Dictionary<string, string> env = new Dictionary<string, string> { ["CI"] = "true" };
			ActionProcess.ToolResult result = await ActionProcess.RunAsync("fastlane", args, ctx.BuildFolder, env);

			if (!result.Succeeded)
			{
				return ActionResult.Fail("GooglePlayUpload: fastlane exited " + result.ExitCode + ": " + result.Tail(20));
			}

			ActionResult ok = ActionResult.Ok("uploaded to Google Play (" + track + ")");
			ok.Metadata["playTrack"] = track;
			return ok;
		}

		#endregion
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Publishes a build to itch.io via <c>butler push</c>. A CATEGORY action that genuinely spans two unrelated
	/// definition families - desktop standalone AND WebGL - which share no typed base, so it inherits the
	/// non-generic <see cref="PostBuildAction"/> directly and gates itself by artifact kind through the widened
	/// <see cref="CanConsume"/> (Win / Mac / Linux standalone or a WebGL build). Terminal action; needs
	/// <c>butler</c>, which reads its credential from the <c>BUTLER_API_KEY</c> environment variable (a String
	/// secret). The destination <c>user/game:channel</c> is an author-supplied setting.
	/// </summary>
	[System.Serializable]
	public sealed class ItchUpload : PostBuildAction
	{
		#region Constants

		private const string ButlerApiKeyKey = "BUTLER_API_KEY";

		#endregion

		#region Fields

		[SerializeField, Tooltip("itch.io push target: user/game:channel (e.g. ateo/dungeon-clawler:windows).")]
		private string _itchTarget;

		#endregion

		#region Properties

		public override string DisplayName => "Upload to itch.io";

		/// <summary>Cross-family action: there is no common typed base for standalone + WebGL, so it binds to the non-generic root and gates via <see cref="CanConsume"/>.</summary>
		public override Type SupportedDefinition => typeof(BuildDefinition);

		public override ArtifactKind Consumes => ArtifactKind.WinStandalone;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(ButlerApiKeyKey, "itch.io butler API key (wharf credentials).", SecretKind.String)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.Tool("butler")
		};

		#endregion

		#region Public Methods

		/// <summary>Category action: accepts any desktop standalone build (Win / Mac / Linux) or a WebGL build.</summary>
		public override bool CanConsume(IReadOnlyCollection<ArtifactKind> available)
		{
			return available != null &&
				(available.Contains(ArtifactKind.WinStandalone) ||
				 available.Contains(ArtifactKind.MacApp) ||
				 available.Contains(ArtifactKind.LinuxStandalone) ||
				 available.Contains(ArtifactKind.WebGLBuild));
		}

		public override async Task<ActionResult> ExecuteAsync(BuildContext ctx, BuildDefinition def)
		{
			if (string.IsNullOrEmpty(_itchTarget))
			{
				return ActionResult.Fail("ItchUpload: no push target configured (_itchTarget, e.g. user/game:channel).");
			}

			string buildDir = ctx.BuildFolder;
			if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
			{
				return ActionResult.Fail("ItchUpload: build folder not found at " + (buildDir ?? "(null)") + ".");
			}

			string apiKey = ctx.GetSecretString(ButlerApiKeyKey);

			List<string> args = new List<string> { "push", buildDir, _itchTarget };
			if (!string.IsNullOrEmpty(ctx.VersionName)) args.Add("--userversion=" + ctx.VersionName);

			ctx.Log?.Invoke("ItchUpload: butler push -> " + _itchTarget);

			// butler reads its credential from BUTLER_API_KEY rather than a flag, so it never lands on the command line.
			Dictionary<string, string> env = new Dictionary<string, string> { ["BUTLER_API_KEY"] = apiKey };
			ActionProcess.ToolResult result = await ActionProcess.RunAsync("butler", args, buildDir, env);

			if (!result.Succeeded)
			{
				return ActionResult.Fail("ItchUpload: butler push failed (exit " + result.ExitCode + "): " + result.Tail(20));
			}

			ActionResult ok = ActionResult.Ok("pushed build to itch.io (" + _itchTarget + ")");
			ok.Metadata["itchTarget"] = _itchTarget;
			return ok;
		}

		#endregion
	}
}

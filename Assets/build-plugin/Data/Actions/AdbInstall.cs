using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Installs an <see cref="ArtifactKind.APK"/> onto a connected Android device via <c>adb install -r</c>.
	/// Terminal, local-only convenience: it needs a physically attached device (a <see cref="HostRequirement.Device"/>
	/// the headless server can't have), so its <see cref="PostBuildAction.RunLocation"/> defaults to
	/// <see cref="RunLocation.Local"/> - a dev's Build Panel build flashes straight to their handset; server builds
	/// skip it. No secrets.
	/// </summary>
	[System.Serializable]
	public sealed class AdbInstall : PostBuildAction<AndroidBuildDefinition>
	{
		#region Fields

		[SerializeField, Tooltip("Target device serial for 'adb -s <serial>'. Empty = the single connected device.")]
		private string _deviceSerial;

		[SerializeField, Tooltip("Reinstall keeping app data (adb install -r). Off = a clean install.")]
		private bool _reinstall = true;

		#endregion

		#region Constructor

		/// <summary>Defaults <see cref="PostBuildAction.RunLocation"/> to Local - flashing a device only makes sense on the dev's machine.</summary>
		public AdbInstall()
		{
			RunLocation = RunLocation.Local;
		}

		#endregion

		#region Properties

		public override string DisplayName => "Install on device (adb)";
		public override ArtifactKind Consumes => ArtifactKind.APK;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.Device("android"),
			HostRequirement.Tool("adb")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, AndroidBuildDefinition def)
		{
			string apkPath = ActionProcess.FindArtifact(ctx, ".apk");
			if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath))
			{
				return ActionResult.Fail("AdbInstall: no .apk found in the pipeline artifacts to install.");
			}

			List<string> args = new List<string>();
			if (!string.IsNullOrEmpty(_deviceSerial))
			{
				args.Add("-s");
				args.Add(_deviceSerial);
			}

			args.Add("install");
			if (_reinstall) args.Add("-r");
			args.Add(apkPath);

			ctx.Log?.Invoke("AdbInstall: adb install " + (_reinstall ? "-r " : "") + Path.GetFileName(apkPath));

			ActionProcess.ToolResult result = await ActionProcess.RunAsync("adb", args, ctx.BuildFolder);

			// adb returns 0 even when the on-device install fails; its failure surfaces as a 'Failure [...]' line.
			bool reportedFailure = result.StdOut.Contains("Failure") || result.StdErr.Contains("Failure");
			if (!result.Succeeded || reportedFailure)
			{
				return ActionResult.Fail("AdbInstall: adb install failed (exit " + result.ExitCode + "): " + result.Tail(20));
			}

			ActionResult ok = ActionResult.Ok("installed " + Path.GetFileName(apkPath));
			ok.Metadata["adbInstalled"] = Path.GetFileName(apkPath);
			if (!string.IsNullOrEmpty(_deviceSerial)) ok.Metadata["adbDevice"] = _deviceSerial;
			return ok;
		}

		#endregion
	}
}

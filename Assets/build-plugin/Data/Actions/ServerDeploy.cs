using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Deploys a <see cref="ArtifactKind.LinuxServerBuild"/> to a remote host over SSH and restarts its systemd
	/// service. Terminal action; the sequence is stop the service (<c>ssh systemctl stop</c>) -> copy the build
	/// (<c>scp -r</c>) into the target folder -> start it again (<c>ssh systemctl start</c>). Authenticates with a
	/// private key resolved as a File secret (passed to ssh/scp via <c>-i</c>); the host, target folder and
	/// service name are author-supplied settings.
	/// </summary>
	[System.Serializable]
	public sealed class ServerDeploy : PostBuildAction<LinuxServerBuildDefinition>
	{
		#region Constants

		private const string SshKeyKey = "SSH_KEY";

		#endregion

		#region Fields

		[SerializeField, Tooltip("Remote target: user@host (e.g. deploy@game.ateo.ch).")]
		private string _host;

		[SerializeField, Tooltip("Absolute remote folder the build is copied into (e.g. /srv/game).")]
		private string _targetFolder;

		[SerializeField, Tooltip("systemd service name to stop before and start after the copy (e.g. game-server).")]
		private string _serviceName;

		#endregion

		#region Properties

		public override string DisplayName => "Deploy to server (SSH)";
		public override ArtifactKind Consumes => ArtifactKind.LinuxServerBuild;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(SshKeyKey, "SSH private key for the deploy host.", SecretKind.File)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.Tool("ssh"),
			HostRequirement.Tool("scp")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, LinuxServerBuildDefinition def)
		{
			if (string.IsNullOrEmpty(_host) || string.IsNullOrEmpty(_targetFolder) || string.IsNullOrEmpty(_serviceName))
			{
				return ActionResult.Fail("ServerDeploy: host, target folder and service name must all be set.");
			}

			string buildDir = ctx.BuildFolder;
			if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
			{
				return ActionResult.Fail("ServerDeploy: server build folder not found at " + (buildDir ?? "(null)") + ".");
			}

			string keyPath = ctx.GetSecretFilePath(SshKeyKey);
			string[] sshOpts = { "-i", keyPath, "-o", "StrictHostKeyChecking=no" };

			// 1) Stop the service.
			ctx.Log?.Invoke("ServerDeploy: ssh systemctl stop " + _serviceName);
			ActionProcess.ToolResult stop = await ActionProcess.RunAsync(
				"ssh", Compose(sshOpts, _host, "sudo", "systemctl", "stop", _serviceName), buildDir);
			if (!stop.Succeeded)
			{
				return ActionResult.Fail("ServerDeploy: failed to stop service (exit " + stop.ExitCode + "): " + stop.Tail(20));
			}

			// 2) Copy the build into the target folder.
			ctx.Log?.Invoke("ServerDeploy: scp -r build -> " + _host + ":" + _targetFolder);
			ActionProcess.ToolResult copy = await ActionProcess.RunAsync(
				"scp", Compose(sshOpts, "-r", buildDir, _host + ":" + _targetFolder), buildDir);
			if (!copy.Succeeded)
			{
				return ActionResult.Fail("ServerDeploy: scp copy failed (exit " + copy.ExitCode + "): " + copy.Tail(20));
			}

			// 3) Start the service again.
			ctx.Log?.Invoke("ServerDeploy: ssh systemctl start " + _serviceName);
			ActionProcess.ToolResult start = await ActionProcess.RunAsync(
				"ssh", Compose(sshOpts, _host, "sudo", "systemctl", "start", _serviceName), buildDir);
			if (!start.Succeeded)
			{
				return ActionResult.Fail("ServerDeploy: failed to start service (exit " + start.ExitCode + "): " + start.Tail(20));
			}

			ActionResult ok = ActionResult.Ok("deployed to " + _host + ":" + _targetFolder + " (" + _serviceName + ")");
			ok.Metadata["deployHost"] = _host;
			ok.Metadata["deployService"] = _serviceName;
			return ok;
		}

		#endregion

		#region Private Methods

		/// <summary>Prepends the shared ssh/scp options (-i key, host-key policy) to the per-command tail.</summary>
		private static List<string> Compose(string[] options, params string[] tail)
		{
			List<string> args = new List<string>(options.Length + tail.Length);
			args.AddRange(options);
			args.AddRange(tail);
			return args;
		}

		#endregion
	}
}

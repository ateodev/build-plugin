using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Deploys a <see cref="ArtifactKind.WebGLBuild"/> to an FTP/SFTP server with <c>curl</c> - the simplest
	/// portable client, no extra host tool beyond curl itself. Terminal action; walks the WebGL build folder and
	/// uploads every file (recreating the relative tree under the target folder via <c>--ftp-create-dirs</c>).
	/// Authenticates with the host + user + password resolved as String secrets; the destination folder is an
	/// author-supplied setting.
	/// </summary>
	[System.Serializable]
	public sealed class FtpUpload : PostBuildAction<WebGLBuildDefinition>
	{
		#region Constants

		private const string FtpHostKey = "FTP_HOST";
		private const string FtpUserKey = "FTP_USER";
		private const string FtpPassKey = "FTP_PASS";

		#endregion

		#region Fields

		[SerializeField, Tooltip("Remote folder the build is uploaded into (e.g. public_html/game). Empty = server root.")]
		private string _targetFolder;

		#endregion

		#region Properties

		public override string DisplayName => "Deploy WebGL (FTP)";
		public override ArtifactKind Consumes => ArtifactKind.WebGLBuild;
		public override ArtifactKind Produces => ArtifactKind.None;

		public override IEnumerable<SecretRequirement> RequiredSecrets => new[]
		{
			new SecretRequirement(FtpHostKey, "FTP/SFTP host (e.g. ftp://host or sftp://host).", SecretKind.String),
			new SecretRequirement(FtpUserKey, "FTP/SFTP username.", SecretKind.String),
			new SecretRequirement(FtpPassKey, "FTP/SFTP password.", SecretKind.String)
		};

		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			HostRequirement.Tool("curl")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, WebGLBuildDefinition def)
		{
			string buildDir = ctx.BuildFolder;
			if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
			{
				return ActionResult.Fail("FtpUpload: WebGL build folder not found at " + (buildDir ?? "(null)") + ".");
			}

			string host = ctx.GetSecretString(FtpHostKey);
			string user = ctx.GetSecretString(FtpUserKey);
			string pass = ctx.GetSecretString(FtpPassKey);

			// Normalize: scheme-prefixed host, a single trailing slash, then the (optional) target folder.
			string baseUrl = host.Contains("://") ? host : "ftp://" + host;
			baseUrl = baseUrl.TrimEnd('/') + "/";
			string folder = (_targetFolder ?? string.Empty).Trim('/');
			if (folder.Length > 0) baseUrl += folder + "/";

			string[] files = Directory.GetFiles(buildDir, "*", SearchOption.AllDirectories);
			if (files.Length == 0)
			{
				return ActionResult.Fail("FtpUpload: WebGL build folder " + buildDir + " is empty - nothing to upload.");
			}

			ctx.Log?.Invoke("FtpUpload: curl upload of " + files.Length + " file(s) -> " + baseUrl);

			// curl --user would put the password on the process command line (readable machine-wide); a transient
			// netrc file keeps the credentials off argv for every per-file invocation.
			string netrcPath = Path.Combine(Path.GetTempPath(), "ateo-netrc-" + Guid.NewGuid().ToString("N"));
			int uploaded = 0;
			try
			{
				string machine = new Uri(baseUrl).Host;
				File.WriteAllText(netrcPath, "machine " + machine + " login " + user + " password " + pass + "\n");

				foreach (string file in files)
				{
					string relative = file.Substring(buildDir.Length).TrimStart('/', '\\').Replace('\\', '/');
					string remoteUrl = baseUrl + relative;

					List<string> args = new List<string>
					{
						"--fail",
						"--silent",
						"--show-error",
						"--ftp-create-dirs",
						"--netrc-file", netrcPath,
						"--upload-file", file,
						remoteUrl
					};

					ActionProcess.ToolResult result = await ActionProcess.RunAsync("curl", args, buildDir);
					if (!result.Succeeded)
					{
						return ActionResult.Fail("FtpUpload: curl failed for '" + relative + "' (exit " + result.ExitCode + "): " + result.Tail(15));
					}

					uploaded++;
				}
			}
			finally
			{
				try
				{
					if (File.Exists(netrcPath)) File.Delete(netrcPath);
				}
				catch (Exception)
				{
					// Best-effort wipe; a leftover netrc in temp is non-fatal.
				}
			}

			ActionResult ok = ActionResult.Ok("uploaded " + uploaded + " file(s) to " + baseUrl);
			ok.Metadata["ftpTarget"] = baseUrl;
			ok.Metadata["ftpFileCount"] = uploaded.ToString();
			return ok;
		}

		#endregion
	}
}

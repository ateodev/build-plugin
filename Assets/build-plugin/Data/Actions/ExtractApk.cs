using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Transforms an <see cref="ArtifactKind.AAB"/> into a sideloadable universal <see cref="ArtifactKind.APK"/>
	/// using bundletool (<c>build-apks --mode=universal</c> produces a <c>.apks</c> archive; the universal APK is
	/// unzipped out of it and added to the pipeline). No secrets. Needs Java (bundletool ships as a jar). Lets a
	/// later AdbInstall / sideload step run off an AAB-producing build.
	/// </summary>
	[System.Serializable]
	public sealed class ExtractApk : PostBuildAction<AndroidBuildDefinition>
	{
		#region Fields

		[SerializeField, Tooltip("Path to bundletool-all.jar (run via 'java -jar'). Leave empty to call a native 'bundletool' on PATH.")]
		private string _bundletoolJar;

		#endregion

		#region Properties

		public override string DisplayName => "Extract universal APK (bundletool)";
		public override ArtifactKind Consumes => ArtifactKind.AAB;
		public override ArtifactKind Produces => ArtifactKind.APK;

		// Requirement follows the configured mode: an explicit jar runs via 'java -jar'; otherwise a native
		// 'bundletool' launcher must be on PATH. Declaring java unconditionally would pass the capability gate
		// on a java-only machine and then fail launching 'bundletool' (and vice versa).
		public override IEnumerable<HostRequirement> HostRequirements => new[]
		{
			string.IsNullOrEmpty(_bundletoolJar) ? HostRequirement.Tool("bundletool") : HostRequirement.Tool("java")
		};

		#endregion

		#region Public Methods

		protected override async Task<ActionResult> ExecuteAsync(BuildContext ctx, AndroidBuildDefinition def)
		{
			string aabPath = ActionProcess.FindArtifact(ctx, ".aab");
			if (string.IsNullOrEmpty(aabPath) || !File.Exists(aabPath))
			{
				return ActionResult.Fail("ExtractApk: no .aab found in the pipeline artifacts to extract from.");
			}

			string workDir = ctx.BuildFolder ?? Path.GetDirectoryName(aabPath);
			string apksPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(aabPath) + ".apks");
			if (File.Exists(apksPath)) File.Delete(apksPath);

			// bundletool refuses to overwrite, so the .apks above is pre-deleted. Universal mode = one APK for all devices.
			string fileName;
			List<string> args = new List<string>();
			if (!string.IsNullOrEmpty(_bundletoolJar))
			{
				fileName = "java";
				args.Add("-jar");
				args.Add(_bundletoolJar);
			}
			else
			{
				fileName = "bundletool";
			}

			args.Add("build-apks");
			args.Add("--mode=universal");
			args.Add("--bundle=" + aabPath);
			args.Add("--output=" + apksPath);

			ctx.Log?.Invoke("ExtractApk: bundletool build-apks --mode=universal (" + Path.GetFileName(aabPath) + ")");

			ActionProcess.ToolResult result = await ActionProcess.RunAsync(fileName, args, workDir);
			if (!result.Succeeded)
			{
				return ActionResult.Fail("ExtractApk: bundletool exited " + result.ExitCode + ": " + result.Tail(20));
			}

			if (!File.Exists(apksPath))
			{
				return ActionResult.Fail("ExtractApk: bundletool succeeded but no .apks archive at " + apksPath + ".");
			}

			// The .apks is a zip; universal mode writes a single 'universal.apk' inside it.
			string apkPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(aabPath) + "-universal.apk");
			if (File.Exists(apkPath)) File.Delete(apkPath);

			try
			{
				using (ZipArchive archive = ZipFile.OpenRead(apksPath))
				{
					ZipArchiveEntry entry = archive.GetEntry("universal.apk") ?? archive.GetEntry("splits/universal.apk");
					if (entry == null)
					{
						return ActionResult.Fail("ExtractApk: no 'universal.apk' inside " + Path.GetFileName(apksPath) + ".");
					}

					entry.ExtractToFile(apkPath, true);
				}
			}
			catch (System.Exception exception)
			{
				return ActionResult.Fail("ExtractApk: failed to unzip the universal APK: " + exception.Message);
			}

			ctx.ArtifactPaths.Add(apkPath);

			ActionResult ok = ActionResult.Ok("extracted " + Path.GetFileName(apkPath));
			ok.Metadata["apkPath"] = apkPath;
			return ok;
		}

		#endregion
	}
}

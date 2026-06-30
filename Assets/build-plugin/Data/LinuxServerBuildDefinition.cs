using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Headless Linux dedicated-server build definition (StandaloneLinux64 + Server subtarget).
	/// Produces a Linux server build, typically deployed via SSH.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Linux Server Definition", fileName = "NewLinuxServerDefinition", order = 15)]
	public sealed class LinuxServerBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.LinuxServer;
		public override ArtifactKind OutputKind => ArtifactKind.LinuxServerBuild;

		#endregion
	}
}

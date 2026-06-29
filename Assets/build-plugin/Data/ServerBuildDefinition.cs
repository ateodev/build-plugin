using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Headless Linux dedicated-server build definition. Produces a Linux server build, deployed via SSH.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Server Definition", fileName = "NewServerDefinition", order = 3)]
	public sealed class ServerBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.LinuxServer;
		public override ArtifactKind OutputKind => ArtifactKind.LinuxServerBuild;

		#endregion
	}
}

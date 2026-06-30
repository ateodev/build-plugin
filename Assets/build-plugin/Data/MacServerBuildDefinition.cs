using UnityEngine;

namespace Ateo.Build
{
	/// <summary>macOS dedicated-server build definition (StandaloneOSX + Server subtarget).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/macOS Server Definition", fileName = "NewMacServerDefinition", order = 14)]
	public sealed class MacServerBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.MacServer;
		public override ArtifactKind OutputKind => ArtifactKind.MacServerBuild;

		#endregion
	}
}

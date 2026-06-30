using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Windows dedicated-server build definition (StandaloneWindows64 + Server subtarget).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Windows Server Definition", fileName = "NewWindowsServerDefinition", order = 13)]
	public sealed class WindowsServerBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.WindowsServer;
		public override ArtifactKind OutputKind => ArtifactKind.WinServerBuild;

		#endregion
	}
}

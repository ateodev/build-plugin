using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Windows desktop standalone build definition.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Windows Definition", fileName = "NewWindowsDefinition", order = 4)]
	public sealed class WindowsBuildDefinition : StandaloneBuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.WindowsStandalone;
		public override ArtifactKind OutputKind => ArtifactKind.WinStandalone;

		#endregion
	}
}

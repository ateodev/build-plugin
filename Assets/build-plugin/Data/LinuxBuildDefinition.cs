using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Linux desktop standalone build definition.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Linux Definition", fileName = "NewLinuxDefinition", order = 6)]
	public sealed class LinuxBuildDefinition : StandaloneBuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.Linux;
		public override ArtifactKind OutputKind => ArtifactKind.LinuxStandalone;

		#endregion
	}
}

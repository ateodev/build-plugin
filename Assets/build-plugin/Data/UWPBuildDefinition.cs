using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Universal Windows Platform (WSAPlayer) build definition.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/UWP Definition", fileName = "NewUWPDefinition", order = 20)]
	public sealed class UWPBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.UWP;
		public override ArtifactKind OutputKind => ArtifactKind.UwpBuild;

		#endregion
	}
}

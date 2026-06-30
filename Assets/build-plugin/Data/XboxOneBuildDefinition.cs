using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Xbox One (legacy ERA) build definition. Partner-SDK target: never auto-installed (fail-and-notify).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Xbox One Definition", fileName = "NewXboxOneDefinition", order = 33)]
	public sealed class XboxOneBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.XboxOne;
		public override ArtifactKind OutputKind => ArtifactKind.XboxOneBuild;

		#endregion
	}
}

using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Xbox One (GDK / GameCore) build definition. Partner-SDK target: never auto-installed (fail-and-notify).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Xbox One (GDK) Definition", fileName = "NewXboxGDKOneDefinition", order = 34)]
	public sealed class XboxGDKOneBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.XboxGDKOne;
		public override ArtifactKind OutputKind => ArtifactKind.XboxGDKOneBuild;

		#endregion
	}
}

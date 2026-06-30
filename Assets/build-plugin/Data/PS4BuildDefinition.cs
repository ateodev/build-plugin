using UnityEngine;

namespace Ateo.Build
{
	/// <summary>PlayStation 4 build definition. Partner-SDK target: never auto-installed (fail-and-notify).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/PS4 Definition", fileName = "NewPS4Definition", order = 31)]
	public sealed class PS4BuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.PS4;
		public override ArtifactKind OutputKind => ArtifactKind.PS4Build;

		#endregion
	}
}

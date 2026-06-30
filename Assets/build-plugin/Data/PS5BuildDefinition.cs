using UnityEngine;

namespace Ateo.Build
{
	/// <summary>PlayStation 5 build definition. Partner-SDK target: never auto-installed (fail-and-notify).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/PS5 Definition", fileName = "NewPS5Definition", order = 32)]
	public sealed class PS5BuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.PS5;
		public override ArtifactKind OutputKind => ArtifactKind.PS5Build;

		#endregion
	}
}

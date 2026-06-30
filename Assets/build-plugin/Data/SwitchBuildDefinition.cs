using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Nintendo Switch build definition. Partner-SDK target: never auto-installed (fail-and-notify).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Switch Definition", fileName = "NewSwitchDefinition", order = 30)]
	public sealed class SwitchBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.Switch;
		public override ArtifactKind OutputKind => ArtifactKind.SwitchBuild;

		#endregion
	}
}

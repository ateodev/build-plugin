using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Xbox Series X|S (GDK / GameCore) build definition. Partner-SDK target: never auto-installed (fail-and-notify).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/Xbox Series Definition", fileName = "NewXboxSeriesDefinition", order = 35)]
	public sealed class XboxSeriesBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.XboxSeries;
		public override ArtifactKind OutputKind => ArtifactKind.XboxSeriesBuild;

		#endregion
	}
}

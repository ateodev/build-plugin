using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Apple tvOS build definition. Unity produces an Xcode project (signed downstream like iOS).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/tvOS Definition", fileName = "NewTvOSDefinition", order = 21)]
	public sealed class TvOSBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.tvOS;
		public override ArtifactKind OutputKind => ArtifactKind.TvOSBuild;

		#endregion
	}
}

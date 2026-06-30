using UnityEngine;

namespace Ateo.Build
{
	/// <summary>Apple visionOS build definition. Unity produces an Xcode project (signed downstream like iOS).</summary>
	[CreateAssetMenu(menuName = "Ateo Build/visionOS Definition", fileName = "NewVisionOSDefinition", order = 22)]
	public sealed class VisionOSBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.VisionOS;
		public override ArtifactKind OutputKind => ArtifactKind.VisionOSBuild;

		#endregion
	}
}

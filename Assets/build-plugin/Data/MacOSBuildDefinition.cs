using UnityEngine;

namespace Ateo.Build
{
	/// <summary>macOS desktop standalone build definition. Notarization (macOS-only) sits on this leaf.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/macOS Definition", fileName = "NewMacOSDefinition", order = 5)]
	public sealed class MacOSBuildDefinition : StandaloneBuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.Mac;
		public override ArtifactKind OutputKind => ArtifactKind.MacApp;

		#endregion
	}
}

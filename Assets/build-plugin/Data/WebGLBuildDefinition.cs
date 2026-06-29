using UnityEngine;

namespace Ateo.Build
{
	/// <summary>WebGL build definition. Produces a WebGL build folder, typically deployed via an FTP upload action.</summary>
	[CreateAssetMenu(menuName = "Ateo Build/WebGL Definition", fileName = "NewWebGLDefinition", order = 2)]
	public sealed class WebGLBuildDefinition : BuildDefinition
	{
		#region Properties

		public override BuildPlatform Platform => BuildPlatform.WebGL;
		public override ArtifactKind OutputKind => ArtifactKind.WebGLBuild;

		#endregion
	}
}

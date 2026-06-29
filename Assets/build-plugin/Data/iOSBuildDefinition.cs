using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// iOS build definition. Unity produces an Xcode project (<see cref="ArtifactKind.XcodeProject"/>); the
	/// post-build pipeline turns it into a signed IPA (fastlane match / gym). Carries the iOS signing
	/// references (mirrors the Android keystore shape - alias + logical secret names, never values).
	/// </summary>
	[CreateAssetMenu(menuName = "Ateo Build/iOS Definition", fileName = "NewiOSDefinition", order = 1)]
	public sealed class iOSBuildDefinition : BuildDefinition
	{
		#region Fields

		[SerializeField, Tooltip("iOS signing references (Apple team + provisioning profile + env-var names). Never the secret itself.")]
		private iOSSigning _iosSigning;

		#endregion

		#region Properties

		public iOSSigning Signing => _iosSigning;

		public override BuildPlatform Platform => BuildPlatform.iOS;
		public override ArtifactKind OutputKind => ArtifactKind.XcodeProject;

		#endregion
	}
}

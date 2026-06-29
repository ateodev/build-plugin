using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Android build definition. Collapses what used to be two TeamCity configs (AAB vs APK) into one
	/// definition + an <see cref="Output"/> data field, and carries the Android keystore signing references.
	/// </summary>
	[CreateAssetMenu(menuName = "Ateo Build/Android Definition", fileName = "NewAndroidDefinition", order = 0)]
	public sealed class AndroidBuildDefinition : BuildDefinition
	{
		#region Fields

		[SerializeField, Tooltip("AAB (Play app bundle) or APK (sideloadable).")]
		private AndroidOutput _androidOutput = AndroidOutput.AAB;

		[SerializeField, Tooltip("Android signing references (alias + env-var names). Never the secret itself.")]
		private AndroidSigning _androidSigning;

		#endregion

		#region Properties

		public AndroidOutput Output => _androidOutput;
		public AndroidSigning Signing => _androidSigning;

		public override BuildPlatform Platform => BuildPlatform.Android;
		public override ArtifactKind OutputKind => _androidOutput == AndroidOutput.AAB ? ArtifactKind.AAB : ArtifactKind.APK;

		#endregion
	}
}

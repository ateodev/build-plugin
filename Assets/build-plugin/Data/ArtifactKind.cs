namespace Ateo.Build
{
	/// <summary>
	/// The kind of artifact a build (or a post-build action) produces or consumes. Each
	/// <see cref="BuildDefinition"/> declares its build output via <see cref="BuildDefinition.OutputKind"/>,
	/// which seeds the ordered post-build-action pipeline: actions declare what they Consume/Produce, and the
	/// chain is validated by flowing these kinds (e.g. AscUpload consumes <see cref="IPA"/>, so it is invalid
	/// without a prior BuildIPA that produces one). Kept as plain data so it lives in the runtime-safe Data
	/// assembly alongside the definition hierarchy.
	/// </summary>
	public enum ArtifactKind
	{
		/// <summary>No artifact - used by a terminal / side-effect post-build action's <see cref="PostBuildAction.Produces"/>.</summary>
		None,
		XcodeProject,
		IPA,
		AAB,
		APK,
		MacApp,
		WinStandalone,
		LinuxStandalone,
		WebGLBuild,
		LinuxServerBuild,
		WinServerBuild,
		MacServerBuild,
		UwpBuild,
		TvOSBuild,
		VisionOSBuild,
		SwitchBuild,
		PS4Build,
		PS5Build,
		XboxOneBuild,
		XboxGDKOneBuild,
		XboxSeriesBuild
	}
}

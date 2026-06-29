namespace Ateo.Build
{
	/// <summary>
	/// The one real intermediate grouping in the hierarchy: desktop standalone (Windows / macOS / Linux).
	/// Justified because Steam / Epic / itch genuinely span the desktop trio, so a standalone-scoped post-build
	/// action (e.g. SteamUpload) auto-applies to all three. Abstract - not directly buildable; pick a leaf.
	/// </summary>
	public abstract class StandaloneBuildDefinition : BuildDefinition
	{
	}
}

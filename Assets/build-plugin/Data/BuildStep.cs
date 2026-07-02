using System;

namespace Ateo.Build
{
	/// <summary>
	/// A pre/post build step, EMBEDDED in its definition as a <c>[SerializeReference]</c> managed object -
	/// exactly like <see cref="PostBuildAction"/>: type-picker add, inline settings, drag-reorder, no
	/// asset-per-step. The division of labor between the two slots: steps WRAP the build (shape it, then
	/// restore state - even on failure); PostBuildActions CONSUME the build (the typed artifact pipeline
	/// with secrets/host-requirement machinery). If your OnPostBuild wants a secret, an external tool, or
	/// the output files, you are writing a PostBuildAction in the wrong slot.
	///
	/// Steps run ONLY for plugin builds (<see cref="BuildRunner"/>), never for regular Unity build-window
	/// builds - deliberate (dev1): a definition's steps are that definition's data, and a build-window build
	/// selects no definition, so there is nothing correct to run. Projects add custom steps with ZERO changes
	/// to this package or to CI - extensibility is data + interface, not an enumerated option list.
	/// </summary>
	[Serializable]
	public abstract class BuildStep
	{
		#region Public Methods

		/// <summary>Runs before the player build. Throw to abort the build.</summary>
		public virtual void OnPreBuild(BuildContext context)
		{
		}

		/// <summary>Runs after the player build (success or failure - check <paramref name="result"/>).</summary>
		public virtual void OnPostBuild(BuildContext context, BuildResult result)
		{
		}

		#endregion
	}
}

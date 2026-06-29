using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// A step in the ordered post-build pipeline, operating on the ALREADY-BUILT artifact by shelling out
	/// (xcodebuild, steamcmd, adb, fastlane, ...) - never via a UnityEditor API, so the same code runs from
	/// CIBuild (server batchmode) or the Build Panel (local). Actions are <c>[SerializeReference]</c> managed
	/// objects in a heterogeneous in-asset list on <see cref="BuildDefinition"/>; serialized settings + the
	/// stable GUID <see cref="Id"/> + author-intent <see cref="RunLocation"/> live on this non-generic base.
	/// Declarative dependencies via <see cref="Consumes"/>/<see cref="Produces"/> artifact kinds let the chain be
	/// validated by flowing kinds. See build-plugin-architecture.md §10.
	/// </summary>
	[Serializable]
	public abstract class PostBuildAction
	{
		#region Fields

		[SerializeField, Tooltip("Stable GUID assigned on creation - lets the per-build skip-set survive reordering / duplicate types.")]
		private string _id;

		[SerializeField, Tooltip("Author intent: where this action should run (capability gate still applies on top).")]
		private RunLocation _runLocation = RunLocation.Both;

		#endregion

		#region Properties

		/// <summary>Stable GUID, assigned on creation (see <see cref="EnsureId"/>) - identifies the action in the per-build skip-set.</summary>
		public string Id => _id;

		/// <summary>Where this action should run (author intent); the hard capability gate wins on conflict.</summary>
		public RunLocation RunLocation { get => _runLocation; set => _runLocation = value; }

		/// <summary>Human-readable name shown in the panel.</summary>
		public abstract string DisplayName { get; }

		/// <summary>The <see cref="BuildDefinition"/> type this action operates on (drives <see cref="Supports"/>).</summary>
		public abstract Type SupportedDefinition { get; }

		/// <summary>The artifact kind this action requires as input - validated against the evolving pipeline set.</summary>
		public abstract ArtifactKind Consumes { get; }

		/// <summary>The artifact kind this action contributes; <see cref="ArtifactKind.None"/> for a terminal / side-effect action.</summary>
		public abstract ArtifactKind Produces { get; }

		/// <summary>Secrets this action needs - declared in code so the requirement travels with the type.</summary>
		public virtual IEnumerable<SecretRequirement> RequiredSecrets => Array.Empty<SecretRequirement>();

		/// <summary>Environmental capabilities this action needs (tool / OS / device) - the hard run-here gate.</summary>
		public virtual IEnumerable<HostRequirement> HostRequirements => Array.Empty<HostRequirement>();

		#endregion

		#region Public Methods

		/// <summary>True when this action is valid for the given definition's concrete type. Override for a rare cross-leaf action.</summary>
		public virtual bool Supports(BuildDefinition def) => SupportedDefinition.IsAssignableFrom(def.GetType());

		/// <summary>Run the action against the already-built artifact carried by <paramref name="ctx"/>.</summary>
		public abstract Task<ActionResult> ExecuteAsync(BuildContext ctx, BuildDefinition def);

		/// <summary>Assigns a fresh stable GUID <see cref="Id"/> if one isn't set. Call when adding the action to a definition.</summary>
		public void EnsureId()
		{
			if (string.IsNullOrEmpty(_id)) _id = Guid.NewGuid().ToString("N");
		}

		#endregion
	}

	/// <summary>
	/// State-free typed layer naming the definition type an action operates on, so the typed body
	/// (<see cref="ExecuteAsync(BuildContext, TDef)"/>) skips the cast. Serialized settings stay on the
	/// non-generic base (Unity's serializer is unreliable for fields on a generic base); this layer is metadata +
	/// typed dispatch only. See build-plugin-architecture.md §10.
	/// </summary>
	public abstract class PostBuildAction<TDef> : PostBuildAction where TDef : BuildDefinition
	{
		public sealed override Type SupportedDefinition => typeof(TDef);

		public sealed override Task<ActionResult> ExecuteAsync(BuildContext ctx, BuildDefinition def) => ExecuteAsync(ctx, (TDef)def);

		/// <summary>Typed action body - no casting.</summary>
		protected abstract Task<ActionResult> ExecuteAsync(BuildContext ctx, TDef def);
	}
}

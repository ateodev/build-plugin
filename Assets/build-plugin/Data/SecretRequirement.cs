using System;

namespace Ateo.Build
{
	/// <summary>
	/// A secret a <see cref="PostBuildAction"/> (or a signing-bearing <see cref="BuildDefinition"/>) needs,
	/// declared in code so the requirement travels with the type. Feeds the UI secret-pickers + just-in-time
	/// resolution, and is reconciled by the wizard against the project's values-free secret registry (flags
	/// declared-but-unregistered / registered-but-unused). The <see cref="Key"/> is the logical name; the actual
	/// scheme-tagged <see cref="SecretRef"/> lives in the registry, never here. See build-plugin-architecture.md §11.2.
	/// </summary>
	[Serializable]
	public struct SecretRequirement
	{
		#region Fields

		/// <summary>Logical key the registry maps to a provider reference (e.g. "MATCH_PASSWORD").</summary>
		public string Key;

		/// <summary>Human-readable description shown in the secret-picker / registry reconciliation.</summary>
		public string Description;

		/// <summary>Whether the secret is a string value or a file (document).</summary>
		public SecretKind Kind;

		#endregion

		#region Constructor

		public SecretRequirement(string key, string description, SecretKind kind)
		{
			Key = key;
			Description = description;
			Kind = kind;
		}

		#endregion
	}
}

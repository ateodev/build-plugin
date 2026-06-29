using System;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// One entry in the project's values-free secret registry (committed on <see cref="ProjectConfig"/>). It maps
	/// a code-declared <see cref="SecretRequirement.Key"/> (the logical name a <see cref="PostBuildAction"/> or a
	/// signing-bearing <see cref="BuildDefinition"/> asks for) to a scheme-tagged <see cref="Reference"/> pointer
	/// (e.g. <c>op://Build Server/Steam/password</c>) that self-selects the resolving <see cref="ISecretProvider"/>.
	/// Holds only the NON-SECRET pointer plus reconciliation metadata - never a value. The wizard reconciles this
	/// list against the code-declared requirements (flagging declared-but-unregistered / registered-but-unused).
	/// See build-plugin-architecture.md §11.2.
	/// </summary>
	[Serializable]
	public sealed class SecretDeclaration
	{
		#region Fields

		[SerializeField, Tooltip("Logical key the consuming action/definition declares (e.g. \"MATCH_PASSWORD\"). The join to the code requirement.")]
		private string _logicalKey;

		[SerializeField, Tooltip("Human-readable description shown in the Secrets registry / reconciliation.")]
		private string _description;

		[SerializeField, Tooltip("Whether the secret is a string value or a file (document).")]
		private SecretKind _kind = SecretKind.String;

		[SerializeField, Tooltip("Scheme-tagged, NON-SECRET pointer to the value (e.g. \"op://Build Server/Steam/password\"). Never a value.")]
		private string _reference;

		[SerializeField, Tooltip("Logical names of the definitions/actions that use this secret (reconciliation aid; informational).")]
		private string[] _usedBy = Array.Empty<string>();

		#endregion

		#region Properties

		/// <summary>Logical key the consuming action/definition declares - the join key to a <see cref="SecretRequirement.Key"/>.</summary>
		public string LogicalKey => _logicalKey;

		/// <summary>Human-readable description shown in the registry / reconciliation.</summary>
		public string Description => _description;

		/// <summary>Whether the secret is a string value or a file (document).</summary>
		public SecretKind Kind => _kind;

		/// <summary>The scheme-tagged, non-secret reference pointer (e.g. an <c>op://...</c> string).</summary>
		public string Reference => _reference;

		/// <summary>Logical names of the definitions/actions that use this secret (informational).</summary>
		public string[] UsedBy => _usedBy;

		/// <summary>This declaration's reference as a <see cref="SecretRef"/> (carries the scheme that picks the provider, plus the <see cref="Kind"/> that routes File reads to a document fetch).</summary>
		public SecretRef Ref => new SecretRef(_reference, _kind);

		#endregion

		#region Constructor

		public SecretDeclaration()
		{
		}

		public SecretDeclaration(string logicalKey, string description, SecretKind kind, string reference, string[] usedBy = null)
		{
			_logicalKey = logicalKey;
			_description = description;
			_kind = kind;
			_reference = reference;
			_usedBy = usedBy ?? Array.Empty<string>();
		}

		#endregion
	}
}

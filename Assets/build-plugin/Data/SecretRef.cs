using System;

namespace Ateo.Build
{
	/// <summary>
	/// A scheme-tagged, NON-secret pointer to a secret - only pointers are committed, never values. The
	/// <see cref="Reference"/> carries its scheme so it self-selects the provider (<c>op://Build Secrets/Steam/password</c>
	/// dispatches to the 1Password provider, <c>openbao://kv/steam#password</c> to OpenBao); a project may mix
	/// providers, each reference picking its own. See build-plugin-architecture.md §11.1.
	/// </summary>
	[Serializable]
	public struct SecretRef
	{
		#region Fields

		/// <summary>The scheme-tagged reference, e.g. "op://vault/item/field" - or the field-less
		/// "op://vault/item" form for a <see cref="SecretKind.File"/> secret stored as a document (a document has no field).</summary>
		public string Reference;

		/// <summary>The shape of the referenced secret - lets a provider route a <see cref="SecretKind.File"/> to a document read. Defaults to <see cref="SecretKind.String"/>.</summary>
		public SecretKind Kind;

		#endregion

		#region Constructor

		public SecretRef(string reference, SecretKind kind = SecretKind.String)
		{
			Reference = reference;
			Kind = kind;
		}

		#endregion

		#region Properties

		/// <summary>The scheme - the part before "://" (e.g. "op", "openbao") - used to dispatch to a provider; null if absent.</summary>
		public string Scheme
		{
			get
			{
				if (string.IsNullOrEmpty(Reference)) return null;

				int idx = Reference.IndexOf("://", StringComparison.Ordinal);
				return idx > 0 ? Reference.Substring(0, idx) : null;
			}
		}

		#endregion
	}
}

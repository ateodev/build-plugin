namespace Ateo.Build
{
	/// <summary>
	/// What an <see cref="ISecretProvider"/> can do - drives UX because providers aren't uniform.
	/// <see cref="Offline"/> false => the panel warns "needs network for local builds"; <see cref="Presence"/>
	/// backs the panel's present?/used-by columns via <see cref="ISecretProvider.ExistsAsync"/>. (There is no
	/// Provisioning capability - write is a MANDATORY part of the contract, §11.7.) See build-plugin-architecture.md §11.1.
	/// </summary>
	public struct SecretProviderCaps
	{
		#region Fields

		/// <summary>Resolution works without network access (e.g. the 1Password desktop cache).</summary>
		public bool Offline;

		/// <summary>The provider can report whether a secret exists (backs the panel's present? columns).</summary>
		public bool Presence;

		#endregion

		#region Constructor

		public SecretProviderCaps(bool offline, bool presence)
		{
			Offline = offline;
			Presence = presence;
		}

		#endregion
	}
}

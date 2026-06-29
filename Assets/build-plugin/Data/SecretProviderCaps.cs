namespace Ateo.Build
{
	/// <summary>
	/// What an <see cref="ISecretProvider"/> can do - drives UX because providers aren't uniform.
	/// <see cref="Offline"/> false => the panel warns "needs network for local builds"; <see cref="Provisioning"/>
	/// false => "create secret" is greyed and the registry is read-only (the likely external-team shape: they
	/// manage their own vault and grant the agent read-only access); <see cref="Presence"/> backs the panel's
	/// present?/used-by columns via <see cref="ISecretProvider.ExistsAsync"/>. See build-plugin-architecture.md §11.1.
	/// </summary>
	public struct SecretProviderCaps
	{
		#region Fields

		/// <summary>Resolution works without network access (e.g. the 1Password desktop cache).</summary>
		public bool Offline;

		/// <summary>The provider can create/update secrets (gates <see cref="ISecretProvider.CreateOrUpdateAsync"/>).</summary>
		public bool Provisioning;

		/// <summary>The provider can report whether a secret exists (backs the panel's present? columns).</summary>
		public bool Presence;

		#endregion

		#region Constructor

		public SecretProviderCaps(bool offline, bool provisioning, bool presence)
		{
			Offline = offline;
			Provisioning = provisioning;
			Presence = presence;
		}

		#endregion
	}
}

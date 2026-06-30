namespace Ateo.Build
{
	/// <summary>
	/// The scheme → <see cref="ISecretProvider"/> factory (the C# half of the provider abstraction, §11.7). The
	/// plugin never <c>new</c>s a concrete provider directly: a reference's scheme (or the project's configured
	/// scheme, for writes) selects the implementation here, so swapping 1Password for OpenBao is a single case.
	/// Mirrors the agent's PowerShell scheme-dispatch front; the two share the written provider contract
	/// (<c>provider-contract.md</c>). Lives in <c>Data</c> (resolution is process/HTTP calls, server+local parity).
	/// </summary>
	public static class SecretProviders
	{
		/// <summary>The provider for <paramref name="scheme"/> with the given coordinates, or null for an unknown scheme.</summary>
		public static ISecretProvider Resolve(string scheme, string config, string account)
		{
			switch (scheme)
			{
				case OnePasswordProvider.SchemeName:
					return new OnePasswordProvider(
						string.IsNullOrEmpty(config) ? OnePasswordProvider.DefaultVault : config,
						string.IsNullOrEmpty(account) ? OnePasswordProvider.DefaultAccount : account);

				// case OpenBaoProvider.SchemeName: return new OpenBaoProvider(config, account);  // §11.4, on demand
				default:
					return null;
			}
		}

		/// <summary>
		/// The provider configured for a project (its scheme + coordinates). Used by the write/validation paths
		/// (wizard, panel) where the scheme comes from the project, not a reference. Falls back to the default
		/// 1Password provider when no project is supplied.
		/// </summary>
		public static ISecretProvider ForProject(ProjectConfig project)
		{
			if (project == null) return new OnePasswordProvider();

			string scheme = string.IsNullOrEmpty(project.SecretProviderScheme)
				? OnePasswordProvider.SchemeName
				: project.SecretProviderScheme;

			return Resolve(scheme, project.SecretProviderVault, project.SecretProviderAccount);
		}
	}
}

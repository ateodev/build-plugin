using System;

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
		/// The provider to use at BUILD time, when the scheme isn't carried by a specific reference. Coordinates
		/// are no longer on ProjectConfig (§11.7): the server's executor exports the team's coords as env
		/// (<c>UNITYBUILD_PROVIDER_SCHEME/CONFIG/ACCOUNT</c>) and this reads them; locally they're absent and the
		/// defaults apply (1Password, the dev's own signed-in session - a local build never needs the robot
		/// account). The wizard's write path passes fetched coords to <see cref="Resolve"/> directly instead.
		/// </summary>
		public static ISecretProvider ForBuild()
		{
			string scheme = Environment.GetEnvironmentVariable("UNITYBUILD_PROVIDER_SCHEME");

			return ResolveWithBuildCoords(string.IsNullOrEmpty(scheme) ? OnePasswordProvider.SchemeName : scheme);
		}

		/// <summary>
		/// The provider for a <paramref name="scheme"/> (typically carried by a reference), with coordinates from
		/// the build environment - <c>UNITYBUILD_PROVIDER_CONFIG/ACCOUNT</c> on the server, defaults locally.
		/// </summary>
		public static ISecretProvider ResolveWithBuildCoords(string scheme)
		{
			return Resolve(scheme,
				Environment.GetEnvironmentVariable("UNITYBUILD_PROVIDER_CONFIG"),
				Environment.GetEnvironmentVariable("UNITYBUILD_PROVIDER_ACCOUNT"));
		}
	}
}

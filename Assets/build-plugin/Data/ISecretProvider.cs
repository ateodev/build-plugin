using System.Threading.Tasks;

namespace Ateo.Build
{
	/// <summary>
	/// The secrets seam: the plugin never hardwires 1Password - it talks to a provider selected by a reference's
	/// <see cref="Scheme"/>, and 1Password is the default implementation. A team using something else (OpenBao,
	/// their own vault) plugs in a provider without touching the plugin. Lives in the runtime-safe Data assembly
	/// (resolution is process/HTTP calls, server+local parity). See build-plugin-architecture.md §11.1.
	/// </summary>
	public interface ISecretProvider
	{
		/// <summary>The scheme this provider serves (e.g. "op", "openbao") - matched against a <see cref="SecretRef.Scheme"/>.</summary>
		string Scheme { get; }

		/// <summary>What this provider can do - drives panel UX (offline / provisioning / presence).</summary>
		SecretProviderCaps Caps { get; }

		/// <summary>Resolve a reference to its value (string or file bytes) for the given execution side.</summary>
		Task<SecretValue> ResolveAsync(SecretRef r, ExecContext ctx);

		/// <summary>Whether the referenced secret exists - backs the panel's present? columns.</summary>
		Task<bool> ExistsAsync(SecretRef r);

		/// <summary>Create or update a secret and return its reference. OPTIONAL capability - only call when <see cref="SecretProviderCaps.Provisioning"/>.</summary>
		Task<SecretRef> CreateOrUpdateAsync(string item, string field, SecretValue value);
	}
}

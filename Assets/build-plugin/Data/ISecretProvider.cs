using System.Collections.Generic;
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

		/// <summary>
		/// Whether this provider is usable from the current machine RIGHT NOW - a cheap, synchronous, local check
		/// (e.g. its CLI is installed, its endpoint is configured). Lets the panel warn "provider not set up"
		/// AGNOSTICALLY: it asks whichever provider a project references, so no UI hardwires a provider's tooling.
		/// </summary>
		bool IsAvailable();

		/// <summary>Actionable, provider-specific setup guidance shown when <see cref="IsAvailable"/> is false.</summary>
		string UnavailableHint { get; }

		/// <summary>Resolve a reference to its value (string or file bytes) for the given execution side.</summary>
		Task<SecretValue> ResolveAsync(SecretRef r, ExecContext ctx);

		/// <summary>
		/// Read a non-secret structured record (named fields) by item key - the <c>vcs-&lt;project-key&gt;</c>
		/// checkout record (§11.7). Returns field-label -&gt; value (the agent reads the same via its own runtime).
		/// </summary>
		Task<IReadOnlyDictionary<string, string>> ReadRecordAsync(string item);

		/// <summary>Whether the referenced secret exists - backs the panel's present? columns.</summary>
		Task<bool> ExistsAsync(SecretRef r);

		/// <summary>
		/// Build the scheme-tagged reference pointer for an item/field WITHOUT writing - so consumers (wizards)
		/// never hand-assemble a provider-specific <c>op://…</c> string. Used for a reachability probe and as the
		/// fallback pointer when a write couldn't confirm the reference.
		/// </summary>
		SecretRef ReferenceFor(string item, string field, SecretKind kind = SecretKind.String);

		/// <summary>Create or update a secret/record field and return its reference. MANDATORY - the wizard self-serves onboarding through it (§11.7).</summary>
		Task<SecretRef> CreateOrUpdateAsync(string item, string field, SecretValue value);
	}
}

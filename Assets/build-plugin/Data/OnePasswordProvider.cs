using System;
using System.Threading.Tasks;

namespace Ateo.Build
{
	/// <summary>
	/// The default <see cref="ISecretProvider"/>: 1Password, backed by the <c>op</c> CLI through an
	/// <see cref="IOpCli"/> seam (so the provider is unit-testable with a fake CLI). Resolves
	/// <c>op://Build Server/&lt;item&gt;/&lt;field&gt;</c> references against the single "Build Server" vault -
	/// devs hit their unlocked desktop app (offline) locally, the agent uses a robot-account session server-side
	/// (the auth bootstrap is environment-local; this code never signs in). Provisioning is supported, so the
	/// panel can create/edit secrets. See build-plugin-architecture.md §11.1 / §11.3.
	/// </summary>
	public sealed class OnePasswordProvider : ISecretProvider
	{
		#region Constants

		/// <summary>The scheme this provider serves; references like <c>op://...</c> dispatch here.</summary>
		public const string SchemeName = "op";

		// TODO: source vault + account from ProjectConfig provider config (§11.1) instead of these constants.
		/// <summary>The single shared 1Password vault holding every build secret (§11.3).</summary>
		public const string DefaultVault = "Build Server";

		/// <summary>The 1Password account shorthand the CLI authenticates as (build-server@ateo.ch).</summary>
		public const string DefaultAccount = "ateoteam";

		#endregion

		#region Fields

		private readonly IOpCli _op;
		private readonly string _vault;
		private readonly string _account;

		#endregion

		#region Constructor

		/// <summary>Production constructor: a real <see cref="OpCli"/> against the default vault + account.</summary>
		public OnePasswordProvider() : this(new OpCli(), DefaultVault, DefaultAccount)
		{
		}

		/// <summary>Testable constructor: inject a fake <see cref="IOpCli"/> (and optionally override vault/account).</summary>
		public OnePasswordProvider(IOpCli op, string vault = DefaultVault, string account = DefaultAccount)
		{
			_op = op ?? throw new ArgumentNullException(nameof(op));
			_vault = vault;
			_account = account;
		}

		#endregion

		#region Properties

		public string Scheme => SchemeName;

		/// <summary>1Password's desktop cache makes local resolution offline-capable; the provider can also provision and report presence.</summary>
		public SecretProviderCaps Caps => new SecretProviderCaps(offline: true, provisioning: true, presence: true);

		#endregion

		#region Public Methods

		public async Task<SecretValue> ResolveAsync(SecretRef r, ExecContext ctx)
		{
			string reference = RequireOpReference(r);

			// File-kind secrets (Play SA-JSON, Apple .p8, the match deploy key) are 1Password DOCUMENTS - read as
			// raw bytes; everything else (passwords, tokens, TOTP shared_secret) reads as a string.
			if (r.Kind == SecretKind.File)
			{
				byte[] bytes = await _op.ReadDocumentAsync(reference, _account);
				return SecretValue.OfFile(bytes);
			}

			string value = await _op.ReadAsync(reference, _account);
			return SecretValue.OfString(value);
		}

		public Task<bool> ExistsAsync(SecretRef r)
		{
			string reference = RequireOpReference(r);
			(string vault, string item, string field) = ParseReference(reference);
			return _op.ItemFieldExistsAsync(vault, item, field, _account);
		}

		public async Task<SecretRef> CreateOrUpdateAsync(string item, string field, SecretValue value)
		{
			await _op.CreateOrEditItemAsync(_vault, item, field, value, _account);

			SecretKind kind = value != null && value.IsFile ? SecretKind.File : SecretKind.String;
			return new SecretRef(SchemeName + "://" + _vault + "/" + item + "/" + field, kind);
		}

		#endregion

		#region Private Methods

		private string RequireOpReference(SecretRef r)
		{
			if (string.IsNullOrEmpty(r.Reference))
			{
				throw new Exception("1Password provider got an empty secret reference.");
			}

			if (!string.Equals(r.Scheme, SchemeName, StringComparison.Ordinal))
			{
				throw new Exception("1Password provider got a non-'op' reference: '" + r.Reference + "'.");
			}

			return r.Reference;
		}

		/// <summary>Parses <c>op://&lt;vault&gt;/&lt;item&gt;/&lt;field&gt;</c> into its three segments (vault may contain spaces, e.g. "Build Server").</summary>
		private static (string Vault, string Item, string Field) ParseReference(string reference)
		{
			const string prefix = SchemeName + "://";
			if (!reference.StartsWith(prefix, StringComparison.Ordinal))
			{
				throw new Exception("Not an op:// reference: '" + reference + "'.");
			}

			string body = reference.Substring(prefix.Length);
			string[] parts = body.Split('/');
			if (parts.Length != 3 || Array.Exists(parts, string.IsNullOrEmpty))
			{
				throw new Exception("Malformed op reference '" + reference +
					"'. Expected op://<vault>/<item>/<field>.");
			}

			return (parts[0], parts[1], parts[2]);
		}

		#endregion
	}
}

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ateo.Build
{
	/// <summary>
	/// A thin seam over the 1Password <c>op</c> CLI so the <see cref="OnePasswordProvider"/> is unit-testable: a
	/// fake implementation returns canned values, the real <see cref="OpCli"/> shells out to <c>op.exe</c>. Every
	/// call carries the account shorthand (1Password supports multiple accounts on one machine, e.g. a dev's own
	/// account locally vs a dedicated robot account on the server - see build-plugin-architecture.md §11.3). No
	/// method attempts an interactive sign-in; a session is assumed to already exist (the desktop app unlocked
	/// locally, or a non-interactive <c>op signin</c> bootstrap on the agent).
	/// </summary>
	public interface IOpCli
	{
		/// <summary>Read a string field via an <c>op://vault/item/field</c> reference (<c>op read</c>).</summary>
		Task<string> ReadAsync(string opRef, string account);

		/// <summary>Read a document/file via an <c>op://vault/item/field</c> reference as raw bytes (<c>op read</c>, no newline mangling).</summary>
		Task<byte[]> ReadDocumentAsync(string opRef, string account);

		/// <summary>Whether the given field exists on the given item in the given vault (backs presence checks).</summary>
		Task<bool> ItemFieldExistsAsync(string vault, string item, string field, string account);

		/// <summary>Whether the item exists at all in the given vault - backs presence checks for field-less
		/// document references (a document has no field to probe).</summary>
		Task<bool> ItemExistsAsync(string vault, string item, string account);

		/// <summary>Read every field (label -&gt; value) of an item - backs <see cref="ISecretProvider.ReadRecordAsync"/> (the <c>&lt;project-key&gt;_vcs</c> record).</summary>
		Task<IReadOnlyDictionary<string, string>> GetItemFieldsAsync(string vault, string item, string account);

		/// <summary>All item titles in the given vault (<c>op item list</c>) - backs <see cref="ISecretProvider.ListItemsAsync"/>.
		/// Prefix filtering is the provider's job: the seam stays a 1:1 mirror of op commands, and op has no server-side filter.</summary>
		Task<IReadOnlyList<string>> ListItemTitlesAsync(string vault, string account);

		/// <summary>Create the item if absent, otherwise edit it, setting <paramref name="field"/> to <paramref name="value"/>
		/// (string or document). String fields are always written as plain text (dev1 decision: vault access protects
		/// values, not field masking); File values are stored as documents.</summary>
		Task CreateOrEditItemAsync(string vault, string item, string field, SecretValue value, string account);
	}
}

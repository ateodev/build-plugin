using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build.Tests
{
	/// <summary>
	/// TEST-ONLY headless harness for <see cref="OnePasswordProvider"/>, driven by a FAKE <see cref="IOpCli"/> so it
	/// needs no real 1Password session. Proves the provider parses an <c>op://Build Server/&lt;item&gt;/&lt;field&gt;</c>
	/// reference, returns the resolved value, that <c>ExistsAsync</c> splits the reference into vault/item/field, that
	/// a File-kind reference routes to <c>ReadDocumentAsync</c> (not the string read), and the File/document round-trip:
	/// CreateOrUpdateAsync(File) returns the field-less <c>op://&lt;vault&gt;/&lt;item&gt;</c> document reference, which
	/// resolves as a document and whose presence is checked at ITEM level, that <c>ListItemsAsync</c> filters
	/// vault item titles by prefix without stripping them, and that <c>DeleteItemAsync</c> forwards the verbatim
	/// item title to the CLI's item delete. Run from CI/CLI with:
	///   Unity ... -batchmode -quit -executeMethod Ateo.Build.Tests.SecretProviderTest.RunSecretProviderTests
	/// Exits 0 when every check passes, 1 otherwise. Mirrors PipelineSmokeTest's style (sample, not a shipped type).
	/// </summary>
	public static class SecretProviderTest
	{
		#region Fake op CLI (test-only)

		/// <summary>A canned <see cref="IOpCli"/> that records what it was asked and returns fixed values.</summary>
		private sealed class FakeOpCli : IOpCli
		{
			public string LastReadRef;
			public string LastReadAccount;
			public bool ReadStringCalled;

			public string LastDocumentRef;
			public bool ReadDocumentCalled;

			public string LastExistsVault;
			public string LastExistsItem;
			public string LastExistsField;

			public string LastCreateVault;
			public string LastCreateItem;
			public string LastCreateField;
			public SecretValue LastCreateValue;

			public string StringResult = "hunter2";
			public byte[] DocumentResult = Encoding.UTF8.GetBytes("{\"type\":\"service_account\"}");
			public bool ExistsResult = true;

			public Task<string> ReadAsync(string opRef, string account)
			{
				ReadStringCalled = true;
				LastReadRef = opRef;
				LastReadAccount = account;
				return Task.FromResult(StringResult);
			}

			public Task<byte[]> ReadDocumentAsync(string opRef, string account)
			{
				ReadDocumentCalled = true;
				LastDocumentRef = opRef;
				LastReadAccount = account;
				return Task.FromResult(DocumentResult);
			}

			public Task<bool> ItemFieldExistsAsync(string vault, string item, string field, string account)
			{
				LastExistsVault = vault;
				LastExistsItem = item;
				LastExistsField = field;
				LastReadAccount = account;
				return Task.FromResult(ExistsResult);
			}

			public string LastItemExistsVault;
			public string LastItemExistsItem;
			public bool ItemExistsCalled;
			public bool ItemExistsResult = true;

			public Task<bool> ItemExistsAsync(string vault, string item, string account)
			{
				ItemExistsCalled = true;
				LastItemExistsVault = vault;
				LastItemExistsItem = item;
				LastReadAccount = account;
				return Task.FromResult(ItemExistsResult);
			}

			public System.Collections.Generic.IReadOnlyDictionary<string, string> RecordResult =
				new System.Collections.Generic.Dictionary<string, string> { ["repoUrl"] = "git@example.com:o/r.git" };

			public string LastFieldsItem;

			public Task<System.Collections.Generic.IReadOnlyDictionary<string, string>> GetItemFieldsAsync(string vault, string item, string account)
			{
				LastFieldsItem = item;
				LastReadAccount = account;
				return Task.FromResult(RecordResult);
			}

			public string LastListVault;
			public System.Collections.Generic.IReadOnlyList<string> ListTitlesResult =
				new System.Collections.Generic.List<string> { "cred-team-github", "cred-uvcs-ci-bot", "vcs-dungeon-clawler", "unity-licenses" };

			public Task<System.Collections.Generic.IReadOnlyList<string>> ListItemTitlesAsync(string vault, string account)
			{
				LastListVault = vault;
				LastReadAccount = account;
				return Task.FromResult(ListTitlesResult);
			}

			public Task CreateOrEditItemAsync(string vault, string item, string field, SecretValue value, string account)
			{
				LastCreateVault = vault;
				LastCreateItem = item;
				LastCreateField = field;
				LastCreateValue = value;
				LastReadAccount = account;
				return Task.CompletedTask;
			}

			public string LastDeleteVault;
			public string LastDeleteItem;
			public bool DeleteCalled;

			public Task DeleteItemAsync(string vault, string item, string account)
			{
				DeleteCalled = true;
				LastDeleteVault = vault;
				LastDeleteItem = item;
				LastReadAccount = account;
				return Task.CompletedTask;
			}
		}

		#endregion

		#region Public Methods

		public static void RunSecretProviderTests()
		{
			int failures = 0;

			failures += ResolveStringSecret();
			failures += ResolveFileSecret();
			failures += ExistsParsesReference();
			failures += CreateRoundTrips();
			failures += CreateFileSecretReturnsDocumentRef();
			failures += ResolveDocumentRef();
			failures += ExistsChecksItemForDocumentRef();
			failures += ListItemsFiltersByPrefix();
			failures += DeleteForwardsItemTitle();

			Debug.Log(failures == 0 ? "[SecretProviderTest] RESULT: ALL PASS" : "[SecretProviderTest] RESULT: FAILURES=" + failures);
			if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
		}

		#endregion

		#region Private Methods - Cases

		/// <summary>A string ref resolves via ReadAsync, returns the value, and passes the exact ref + account through.</summary>
		private static int ResolveStringSecret()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli { StringResult = "s3cret-token" };
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			SecretRef reference = new SecretRef("op://Build Server/Steam/password");
			SecretValue value = provider.ResolveAsync(reference, ExecContext.Server).GetAwaiter().GetResult();

			failures += Check(value != null && !value.IsFile, "string secret resolves to a non-file value");
			failures += Check(value != null && value.StringValue == "s3cret-token", "string value is returned verbatim");
			failures += Check(fake.ReadStringCalled && !fake.ReadDocumentCalled, "string ref routed to ReadAsync (not document)");
			failures += Check(fake.LastReadRef == "op://Build Server/Steam/password", "the full op reference is passed through unparsed");
			failures += Check(fake.LastReadAccount == OnePasswordProvider.DefaultAccount, "the default account shorthand is used");

			return failures;
		}

		/// <summary>A File-kind ref routes to ReadDocumentAsync and yields file bytes.</summary>
		private static int ResolveFileSecret()
		{
			int failures = 0;
			byte[] doc = Encoding.UTF8.GetBytes("PLAY-SA-JSON-BYTES");
			FakeOpCli fake = new FakeOpCli { DocumentResult = doc };
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			SecretRef reference = new SecretRef("op://Build Server/PlayServiceAccount/credential", SecretKind.File);
			SecretValue value = provider.ResolveAsync(reference, ExecContext.Server).GetAwaiter().GetResult();

			failures += Check(fake.ReadDocumentCalled && !fake.ReadStringCalled, "File ref routed to ReadDocumentAsync (not string)");
			failures += Check(value != null && value.IsFile, "File secret resolves to a file value");
			failures += Check(value != null && BytesEqual(value.FileBytes, doc), "file bytes are returned verbatim");
			failures += Check(fake.LastDocumentRef == "op://Build Server/PlayServiceAccount/credential", "document ref passed through");

			return failures;
		}

		/// <summary>ExistsAsync parses op://vault/item/field into its three parts (vault keeps its space).</summary>
		private static int ExistsParsesReference()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli { ExistsResult = true };
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			bool exists = provider.ExistsAsync(new SecretRef("op://Build Server/Steam/password")).GetAwaiter().GetResult();

			failures += Check(exists, "ExistsAsync returns the CLI's presence result");
			failures += Check(fake.LastExistsVault == "Build Server", "vault segment parsed (with its space)");
			failures += Check(fake.LastExistsItem == "Steam", "item segment parsed");
			failures += Check(fake.LastExistsField == "password", "field segment parsed");

			return failures;
		}

		/// <summary>CreateOrUpdateAsync forwards to the CLI and returns a reference into the configured vault.</summary>
		private static int CreateRoundTrips()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli();
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			SecretRef created = provider.CreateOrUpdateAsync("Steam", "password", SecretValue.OfString("new-pw"))
				.GetAwaiter().GetResult();

			failures += Check(fake.LastCreateItem == "Steam" && fake.LastCreateField == "password", "create forwards item/field to the CLI");
			failures += Check(fake.LastCreateVault == OnePasswordProvider.DefaultVault, "create targets the configured vault");
			failures += Check(created.Reference == "op://Build Server/Steam/password", "returned reference points at the new secret");

			return failures;
		}

		/// <summary>A File value is stored as a DOCUMENT and returns the field-less op://vault/item reference (the
		/// document form the agent fetches by item name), not a dead op://vault/item/field pointer.</summary>
		private static int CreateFileSecretReturnsDocumentRef()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli();
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			SecretRef created = provider
				.CreateOrUpdateAsync("cred-team", "credential", SecretValue.OfFile(Encoding.UTF8.GetBytes("KEY-BYTES")))
				.GetAwaiter().GetResult();

			failures += Check(fake.LastCreateItem == "cred-team", "create forwards the item to the CLI");
			failures += Check(fake.LastCreateValue != null && fake.LastCreateValue.IsFile, "the File value reaches the CLI as a file");
			failures += Check(created.Reference == "op://Build Server/cred-team", "File secret returns the field-less document reference");
			failures += Check(created.Kind == SecretKind.File, "the returned reference keeps its File kind");

			return failures;
		}

		/// <summary>A field-less document ref resolves via ReadDocumentAsync - a document is addressed by item alone.</summary>
		private static int ResolveDocumentRef()
		{
			int failures = 0;
			byte[] doc = Encoding.UTF8.GetBytes("PRIVATE-KEY-BYTES");
			FakeOpCli fake = new FakeOpCli { DocumentResult = doc };
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			SecretRef reference = new SecretRef("op://Build Server/cred-team", SecretKind.File);
			SecretValue value = provider.ResolveAsync(reference, ExecContext.Server).GetAwaiter().GetResult();

			failures += Check(fake.ReadDocumentCalled && !fake.ReadStringCalled, "field-less ref routed to ReadDocumentAsync");
			failures += Check(value != null && value.IsFile && BytesEqual(value.FileBytes, doc), "document bytes returned verbatim");
			failures += Check(fake.LastDocumentRef == "op://Build Server/cred-team", "the field-less ref is passed through unmodified");

			return failures;
		}

		/// <summary>ListItemsAsync returns only the prefix-matching titles, IN FULL - never trimmed (callers
		/// consume titles verbatim; the prefix is an opaque filter string, here just a fixture namespace).</summary>
		private static int ListItemsFiltersByPrefix()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli();
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			System.Collections.Generic.IReadOnlyList<string> items =
				provider.ListItemsAsync("cred-").GetAwaiter().GetResult();

			failures += Check(items != null && items.Count == 2, "only the prefix-matching titles are returned");
			failures += Check(items != null && items.Count == 2 && items[0] == "cred-team-github" && items[1] == "cred-uvcs-ci-bot",
				"titles come back in full - the prefix is not stripped");
			failures += Check(fake.LastListVault == OnePasswordProvider.DefaultVault, "listing targets the configured vault");
			failures += Check(fake.LastReadAccount == OnePasswordProvider.DefaultAccount, "the account shorthand is passed through");

			return failures;
		}

		/// <summary>DeleteItemAsync forwards the VERBATIM item title (and the configured vault) to the CLI - the
		/// delete verb targets a title, never a reference, and nothing may rewrite it on the way down.</summary>
		private static int DeleteForwardsItemTitle()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli();
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			provider.DeleteItemAsync("build-plugin-test_steam-user").GetAwaiter().GetResult();

			failures += Check(fake.DeleteCalled, "delete routed to the CLI's item delete");
			failures += Check(fake.LastDeleteItem == "build-plugin-test_steam-user", "the item title is forwarded verbatim");
			failures += Check(fake.LastDeleteVault == OnePasswordProvider.DefaultVault, "delete targets the configured vault");
			failures += Check(fake.LastReadAccount == OnePasswordProvider.DefaultAccount, "the account shorthand is passed through");

			return failures;
		}

		/// <summary>ExistsAsync on a field-less document ref checks ITEM existence - a document has no field to probe.</summary>
		private static int ExistsChecksItemForDocumentRef()
		{
			int failures = 0;
			FakeOpCli fake = new FakeOpCli { ItemExistsResult = true };
			OnePasswordProvider provider = new OnePasswordProvider(fake);

			bool exists = provider.ExistsAsync(new SecretRef("op://Build Server/cred-team", SecretKind.File))
				.GetAwaiter().GetResult();

			failures += Check(exists, "ExistsAsync returns the CLI's item-presence result");
			failures += Check(fake.ItemExistsCalled, "field-less ref routed to ItemExistsAsync");
			failures += Check(fake.LastItemExistsVault == "Build Server" && fake.LastItemExistsItem == "cred-team", "vault/item parsed from the document ref");
			failures += Check(fake.LastExistsItem == null, "no field-level probe was attempted");

			return failures;
		}

		#endregion

		#region Private Methods - Helpers

		private static int Check(bool condition, string label)
		{
			if (condition) Debug.Log("[SecretProviderTest] PASS: " + label);
			else Debug.LogError("[SecretProviderTest] FAIL: " + label);

			return condition ? 0 : 1;
		}

		private static bool BytesEqual(byte[] a, byte[] b)
		{
			if (a == null || b == null || a.Length != b.Length) return false;

			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i]) return false;
			}

			return true;
		}

		#endregion
	}
}

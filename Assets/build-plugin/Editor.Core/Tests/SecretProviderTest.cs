using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build.Tests
{
	/// <summary>
	/// TEST-ONLY headless harness for <see cref="OnePasswordProvider"/>, driven by a FAKE <see cref="IOpCli"/> so it
	/// needs no real 1Password session. Proves the provider parses an <c>op://Build Server/&lt;item&gt;/&lt;field&gt;</c>
	/// reference, returns the resolved value, that <c>ExistsAsync</c> splits the reference into vault/item/field, and
	/// that a File-kind reference routes to <c>ReadDocumentAsync</c> (not the string read). Run from CI/CLI with:
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

			public System.Collections.Generic.IReadOnlyDictionary<string, string> RecordResult =
				new System.Collections.Generic.Dictionary<string, string> { ["repoUrl"] = "git@example.com:o/r.git" };

			public string LastFieldsItem;

			public Task<System.Collections.Generic.IReadOnlyDictionary<string, string>> GetItemFieldsAsync(string vault, string item, string account)
			{
				LastFieldsItem = item;
				LastReadAccount = account;
				return Task.FromResult(RecordResult);
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

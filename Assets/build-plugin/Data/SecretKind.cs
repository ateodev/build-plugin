namespace Ateo.Build
{
	/// <summary>
	/// The shape of a secret. <see cref="String"/> values resolve to a plain string (passwords, tokens);
	/// <see cref="File"/> values resolve to bytes materialized to a transient path on use and wiped afterward
	/// (Play service-account JSON, Apple ASC <c>.p8</c>, console signing keys, the match deploy key - stored as
	/// provider documents). See build-plugin-architecture.md §11.2.
	/// </summary>
	public enum SecretKind
	{
		String,
		File
	}
}

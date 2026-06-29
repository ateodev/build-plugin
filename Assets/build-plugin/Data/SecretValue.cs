namespace Ateo.Build
{
	/// <summary>
	/// A resolved secret VALUE - the output of <see cref="ISecretProvider.ResolveAsync"/>. Holds EITHER a string
	/// (passwords, tokens) OR file bytes (documents materialized to a transient path on use, then wiped),
	/// distinguished by <see cref="IsFile"/>. Never committed and never logged; constructed only at resolution
	/// time. See build-plugin-architecture.md §11.
	/// </summary>
	public sealed class SecretValue
	{
		#region Fields

		/// <summary>True when this is a file/document (use <see cref="FileBytes"/>); false for a plain string (use <see cref="StringValue"/>).</summary>
		public bool IsFile;

		/// <summary>The string value, when <see cref="IsFile"/> is false.</summary>
		public string StringValue;

		/// <summary>The file bytes, when <see cref="IsFile"/> is true.</summary>
		public byte[] FileBytes;

		#endregion

		#region Public Methods

		/// <summary>A string-kind secret value.</summary>
		public static SecretValue OfString(string value)
		{
			return new SecretValue { IsFile = false, StringValue = value };
		}

		/// <summary>A file-kind secret value.</summary>
		public static SecretValue OfFile(byte[] bytes)
		{
			return new SecretValue { IsFile = true, FileBytes = bytes };
		}

		#endregion
	}
}

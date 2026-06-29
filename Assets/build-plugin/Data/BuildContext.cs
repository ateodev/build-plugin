using System;
using System.Collections.Generic;
using System.IO;

namespace Ateo.Build
{
	/// <summary>
	/// State threaded through a build: the definition being built, the resolved project config, and the
	/// values computed before the player build (output path, stamped version). Passed to every pre/post
	/// <see cref="BuildStep"/> so steps can read context and contribute to the artifact. Plain mutable
	/// data-carrier - <see cref="BuildRunner"/> and steps write to it, so the fields are public.
	/// </summary>
	public sealed class BuildContext
	{
		#region Fields

		public BuildDefinition Definition;
		public ProjectConfig Project;

		/// <summary>True when running headless under CI (batchmode), false for an in-editor local build.</summary>
		public bool IsBatchMode;

		/// <summary>Absolute path the player build will be written to.</summary>
		public string OutputPath;

		/// <summary>Marketing version stamped for this build (see <see cref="VersionStamp"/>).</summary>
		public string VersionName;

		/// <summary>Version/build code stamped for this build.</summary>
		public int VersionCode;

		/// <summary>Absolute path to the build folder the post-build pipeline operates within.</summary>
		public string BuildFolder;

		/// <summary>The evolving artifact set the post-build pipeline hands off file-based (see build-plugin-architecture.md §10.1).</summary>
		public List<string> ArtifactPaths = new List<string>();

		/// <summary>Logger hook - actions call it to emit phase text (-> ##teamcity[progressMessage] on the server).</summary>
		public Action<string> Log;

		/// <summary>
		/// Secrets resolved for this build, keyed by the logical key a <see cref="PostBuildAction"/> declared in its
		/// <see cref="PostBuildAction.RequiredSecrets"/>. Populated by <see cref="BuildRunner"/> just before each
		/// action runs (resolved through the project's <see cref="ISecretProvider"/>); post-build actions read it via
		/// <see cref="GetSecretString"/> / <see cref="GetSecretFilePath"/>. Never logged.
		/// </summary>
		public Dictionary<string, SecretValue> Secrets = new Dictionary<string, SecretValue>();

		/// <summary>Cache of File-kind secrets already materialized to a temp path, so repeated reads reuse one file.</summary>
		private readonly Dictionary<string, string> _materializedSecretFiles = new Dictionary<string, string>();

		#endregion

		#region Properties

		public BuildPlatform Platform => Definition != null ? Definition.Platform : BuildPlatform.Android;

		#endregion

		#region Public Methods

		/// <summary>
		/// The resolved STRING value of a secret by its logical key. Throws when the key wasn't resolved (a missing
		/// registry entry or provider error fails the action before it runs) or when it is a File secret (use
		/// <see cref="GetSecretFilePath"/>).
		/// </summary>
		public string GetSecretString(string logicalKey)
		{
			SecretValue value = RequireSecret(logicalKey);
			if (value.IsFile)
			{
				throw new Exception("Secret '" + logicalKey + "' is a File secret - call GetSecretFilePath instead.");
			}

			return value.StringValue;
		}

		/// <summary>
		/// The path to a FILE secret, materialized to a transient file on first access and cached for the rest of
		/// the build (so an action that needs the path repeatedly gets one stable file). Throws when the key wasn't
		/// resolved or is a string secret (use <see cref="GetSecretString"/>). The file lives under the OS temp dir;
		/// the build is responsible for not persisting it beyond the run.
		/// </summary>
		public string GetSecretFilePath(string logicalKey)
		{
			SecretValue value = RequireSecret(logicalKey);
			if (!value.IsFile)
			{
				throw new Exception("Secret '" + logicalKey + "' is a String secret - call GetSecretString instead.");
			}

			if (_materializedSecretFiles.TryGetValue(logicalKey, out string existing) && File.Exists(existing))
			{
				return existing;
			}

			string path = Path.Combine(Path.GetTempPath(), "ateo-secret-" + Guid.NewGuid().ToString("N"));
			File.WriteAllBytes(path, value.FileBytes ?? Array.Empty<byte>());
			_materializedSecretFiles[logicalKey] = path;
			return path;
		}

		#endregion

		#region Private Methods

		private SecretValue RequireSecret(string logicalKey)
		{
			if (Secrets != null && Secrets.TryGetValue(logicalKey, out SecretValue value) && value != null) return value;

			throw new Exception("Secret '" + logicalKey + "' was not resolved for this build.");
		}

		#endregion
	}
}

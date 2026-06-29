using System;
using System.Collections.Generic;

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

		#endregion

		#region Properties

		public BuildPlatform Platform => Definition != null ? Definition.Platform : BuildPlatform.Android;

		#endregion
	}
}

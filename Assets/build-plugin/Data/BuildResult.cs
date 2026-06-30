using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Machine-readable outcome of a build. Written to disk as JSON (<see cref="WriteJson"/>) so CI can
	/// publish/parse it, and returned in-process to local callers and post-build steps. Serializable for
	/// <see cref="JsonUtility"/>, so the fields are public.
	/// </summary>
	[Serializable]
	public sealed class BuildResult
	{
		#region Types

		/// <summary>One key/value pair contributed by a post-build action (JsonUtility can't serialize a Dictionary).</summary>
		[Serializable]
		public struct MetaEntry
		{
			public string Key;
			public string Value;
		}

		#endregion

		#region Fields

		public bool Success;
		public string DefinitionName;
		public string Platform;          // server token, e.g. "Android"
		public string ArtifactPath;      // absolute path to the produced artifact (empty on failure)
		public string VersionName;       // marketing version actually stamped
		public int VersionCode;          // version/build code actually stamped
		public string BuildName;         // optional free-text build-name suffix (§12.2), raw/unsanitized
		public long DurationSeconds;
		public string Error;             // populated on failure

		/// <summary>Metadata merged from the post-build-action pipeline (e.g. a TestFlight / store URL).</summary>
		public List<MetaEntry> Metadata = new List<MetaEntry>();

		#endregion

		#region Public Methods

		public static BuildResult Failed(string definitionName, string error)
		{
			return new BuildResult { Success = false, DefinitionName = definitionName, Error = error };
		}

		/// <summary>Serialize to a JSON file for CI to publish/parse. Never throws.</summary>
		public void WriteJson(string path)
		{
			try
			{
				File.WriteAllText(path, JsonUtility.ToJson(this, true));
				Debug.Log("[Build] result written: " + path);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Build] could not write result JSON (" + path + "): " + e.Message);
			}
		}

		#endregion
	}
}

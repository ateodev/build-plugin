using System;
using UnityEngine;

namespace Ateo.Build
{
    /// <summary>
    /// Machine-readable outcome of a build. Written to disk as JSON (<see cref="WriteJson"/>) so CI can
    /// publish/parse it, and returned in-process to local callers and post-build steps. Serializable for
    /// <see cref="JsonUtility"/>.
    /// </summary>
    [Serializable]
    public class BuildResult
    {
        public bool success;
        public string definitionName;
        public string platform;          // server token, e.g. "Android"
        public string artifactPath;      // absolute path to the produced artifact (empty on failure)
        public string versionName;       // marketing version actually stamped
        public int versionCode;          // version/build code actually stamped
        public long durationSeconds;
        public string error;             // populated on failure

        public static BuildResult Failed(string definitionName, string error)
            => new BuildResult { success = false, definitionName = definitionName, error = error };

        /// <summary>Serialize to a JSON file for CI to publish/parse. Never throws.</summary>
        public void WriteJson(string path)
        {
            try
            {
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(this, true));
                Debug.Log("[Build] result written: " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Build] could not write result JSON (" + path + "): " + e.Message);
            }
        }
    }
}

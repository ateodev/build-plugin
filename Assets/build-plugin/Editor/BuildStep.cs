using UnityEngine;

namespace Ateo.Build
{
    /// <summary>
    /// A pre/post build step. Each step is a small ScriptableObject so a definition can hold an ordered,
    /// drag-reorderable list of them. Projects add custom steps with ZERO changes to this package or to CI -
    /// extensibility is data + interface, not an enumerated option list. Built-in steps (version-stamp,
    /// scripting-define injection, apply-keystore-from-env, Slack notify, post-build copy) ship in later
    /// versions; this base type + the run hooks are the v1 framework they plug into.
    /// </summary>
    public abstract class BuildStep : ScriptableObject
    {
        /// <summary>Runs before the player build. Throw to abort the build.</summary>
        public virtual void OnPreBuild(BuildContext context) { }

        /// <summary>Runs after the player build (success or failure - check <paramref name="result"/>).</summary>
        public virtual void OnPostBuild(BuildContext context, BuildResult result) { }
    }
}

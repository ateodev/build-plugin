namespace Ateo.Build
{
	/// <summary>A build's state as reported by the server (the panel's view of a run).</summary>
	public sealed class BuildStatus
	{
		#region Fields

		public long Id;
		public string Number;
		public string State;              // queued | running | finished
		public string Status;             // SUCCESS | FAILURE | UNKNOWN (meaningful once finished)
		public string StatusText;         // human-readable phase / result text
		public int PercentageComplete;
		public string WebUrl;

		// Populated for the Activity / in-flight views from the build's unitybuild.* properties + REST attributes.
		public string Project;            // unitybuild.project property (null when not recorded)
		public string DefinitionId;       // unitybuild.definitionId property - the definition asset's GUID (null when not recorded)
		public string Definition;         // unitybuild.definition property - the display label, human-facing only (null when not recorded)
		public string BuildTypeId;        // owning executor config id
		public string Agent;              // running agent name (null while queued)
		public int QueuePosition;         // 1-based position while queued (0 when not applicable)

		// Build identity (§12.2), recorded by BuildRunner via unitybuild.version.* - drives the on-disk download
		// folder + local/server correlation. Null/0 for builds that predate identity recording.
		public string VersionName;        // unitybuild.version.name resulting property
		public int VersionCode;           // unitybuild.version.code resulting property
		public string BuildName;          // unitybuild.buildName resulting property (optional, raw)

		#endregion

		#region Properties

		public bool IsQueued => State == "queued";
		public bool IsRunning => State == "running";
		public bool IsFinished => State == "finished";
		public bool IsSuccess => IsFinished && Status == "SUCCESS";
		public bool IsFailure => IsFinished && Status == "FAILURE";

		#endregion
	}

	/// <summary>One downloadable artifact file on a build.</summary>
	public struct ArtifactFile
	{
		public string Name;
		public long Size;

		public ArtifactFile(string name, long size)
		{
			Name = name;
			Size = size;
		}
	}
}

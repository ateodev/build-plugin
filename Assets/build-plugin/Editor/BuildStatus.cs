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

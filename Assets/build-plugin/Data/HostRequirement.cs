using System;

namespace Ateo.Build
{
	/// <summary>
	/// An environmental capability a <see cref="PostBuildAction"/> needs in order to run here at all (the hard
	/// "can it run?" gate, distinct from the author-intent <see cref="RunLocation"/>). Probed by
	/// <see cref="HostProbe"/>: the Build Panel auto-disables a local action with an unmet requirement, and the
	/// runner fails a still-enabled one before executing it. Examples: <c>AdbInstall</c> needs a connected
	/// <see cref="HostKind.Device"/> (impossible on the headless server); <c>Notarize</c>/<c>BuildIPA</c> need
	/// <see cref="HostKind.OperatingSystem"/> "macOS" + the <see cref="HostKind.Tool"/> "xcodebuild". See
	/// build-plugin-architecture.md §10.
	/// </summary>
	[Serializable]
	public struct HostRequirement
	{
		#region Types

		/// <summary>The category of capability a <see cref="HostRequirement"/> expresses.</summary>
		public enum HostKind
		{
			Tool,
			OperatingSystem,
			Device
		}

		#endregion

		#region Fields

		/// <summary>Which category of capability this requirement is about.</summary>
		public HostKind Kind;

		/// <summary>The required value within the category (e.g. Tool:"xcodebuild", OS:"macOS", Device:"android").</summary>
		public string Value;

		#endregion

		#region Constructor

		public HostRequirement(HostKind kind, string value)
		{
			Kind = kind;
			Value = value;
		}

		#endregion

		#region Public Methods

		/// <summary>A required command-line tool (e.g. "xcodebuild", "steamcmd", "adb").</summary>
		public static HostRequirement Tool(string value) => new HostRequirement(HostKind.Tool, value);

		/// <summary>A required host operating system (e.g. "macOS", "Windows").</summary>
		public static HostRequirement OS(string value) => new HostRequirement(HostKind.OperatingSystem, value);

		/// <summary>A required connected device (e.g. "android", "ios").</summary>
		public static HostRequirement Device(string value) => new HostRequirement(HostKind.Device, value);

		#endregion
	}
}

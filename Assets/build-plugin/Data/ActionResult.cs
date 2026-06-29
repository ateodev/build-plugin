using System.Collections.Generic;

namespace Ateo.Build
{
	/// <summary>
	/// The outcome of a <see cref="PostBuildAction"/>. <see cref="Success"/> reports whether it worked;
	/// <see cref="Fatal"/> (on failure) decides whether the ordered pipeline stops (fatal) or continues
	/// (non-fatal). <see cref="Metadata"/> is recorded on the build (e.g. a TestFlight / store URL). See
	/// build-plugin-architecture.md §10.
	/// </summary>
	public sealed class ActionResult
	{
		#region Fields

		public bool Success;

		/// <summary>On failure, stop the rest of the pipeline; ignored when <see cref="Success"/> is true.</summary>
		public bool Fatal;

		public string Message;

		/// <summary>Arbitrary key/value pairs recorded on the build (e.g. {"storeUrl": "..."}).</summary>
		public Dictionary<string, string> Metadata = new Dictionary<string, string>();

		#endregion

		#region Public Methods

		/// <summary>A successful result, optionally with a message.</summary>
		public static ActionResult Ok(string message = null)
		{
			return new ActionResult { Success = true, Fatal = false, Message = message };
		}

		/// <summary>A failed result; <paramref name="fatal"/> stops the pipeline (default) or lets it continue.</summary>
		public static ActionResult Fail(string message, bool fatal = true)
		{
			return new ActionResult { Success = false, Fatal = fatal, Message = message };
		}

		#endregion
	}
}

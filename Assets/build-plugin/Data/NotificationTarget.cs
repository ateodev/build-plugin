using System;

namespace Ateo.Build
{
	/// <summary>
	/// Parsing/validation for the scheme-tagged notification target strings committed on
	/// <see cref="ProjectConfig"/> / <see cref="BuildDefinition"/> (<c>slack:C0123ABC456</c>). AUTHORING-TIME
	/// only: the plugin stays delivery-ignorant - the scheme is an opaque routing tag the server side resolves,
	/// so no Slack/Discord API knowledge lives here (or anywhere in C#). An untagged value is INVALID by design
	/// (no bare-channel fallback): the wizard and views fail early instead of any consumer guessing a scheme.
	/// Empty means "no notifications" and is handled by callers, not treated as valid here.
	/// </summary>
	public static class NotificationTarget
	{
		#region Fields

		/// <summary>Schemes the server side currently routes; grows here (one place) when a new delivery lands.</summary>
		public static readonly string[] KnownSchemes = { "slack", "discord" };

		#endregion

		#region Public Methods

		/// <summary>True when <paramref name="target"/> is a well-formed scheme-tagged target with a known scheme.</summary>
		public static bool IsValid(string target)
		{
			if (!TryParseScheme(target, out string scheme)) return false;

			foreach (string known in KnownSchemes)
			{
				if (string.Equals(scheme, known, StringComparison.Ordinal)) return true;
			}

			return false;
		}

		/// <summary>
		/// Extracts the scheme tag from <c>&lt;scheme&gt;:&lt;target&gt;</c>. False for empty input, a missing
		/// or empty scheme, or an empty remainder - any scheme is parsed (known-ness is <see cref="IsValid"/>'s job).
		/// </summary>
		public static bool TryParseScheme(string target, out string scheme)
		{
			scheme = null;
			if (string.IsNullOrEmpty(target)) return false;

			int separator = target.IndexOf(':');
			// Both halves must be non-empty: ":C012" has no scheme, "slack:" has no target.
			if (separator <= 0 || separator == target.Length - 1) return false;

			scheme = target.Substring(0, separator);
			return true;
		}

		/// <summary>Shared human hint for validation errors, so every surface names the same expected form.</summary>
		public static string ExpectedFormHint()
		{
			return "expected <scheme>:<target> with a known scheme (" + string.Join(", ", KnownSchemes) +
				"), e.g. slack:C0123ABC456";
		}

		#endregion
	}
}

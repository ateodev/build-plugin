using System;

namespace Ateo.Build
{
	/// <summary>
	/// Marks a string field that holds a LOGICAL SECRET KEY (a <see cref="SecretRequirement.Key"/>-style name
	/// like "MATCH_PASSWORD" that the project's secret registry maps to a provider reference). The Editor.UI
	/// drawer turns such a field into a dropdown of the registry's keys (filtered by <see cref="Kind"/>) plus a
	/// "Register new..." entry, so users pick real registered keys instead of typing free text.
	///
	/// Deliberately a PLAIN C# attribute living in Data - not an Odin attribute, and not an
	/// OdinAttributeProcessor keying on field names from Editor.UI. Data must stay editor/Odin-free (this
	/// assembly ships in builds), and a name-keyed processor couples Editor.UI to private field NAMES in
	/// another assembly - a rename would silently drop the drawer. Declared at the field itself, the marker is
	/// discoverable in place and any future Data type gets the dropdown by tagging its field, with zero
	/// Editor.UI changes. Consumed by <c>SecretKeyFieldDrawer</c> (Editor.UI); a no-op at runtime.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
	public sealed class SecretKeyFieldAttribute : Attribute
	{
		#region Properties

		/// <summary>The kind of secret the field's key must reference - filters the dropdown's registry keys.</summary>
		public SecretKind Kind { get; }

		#endregion

		#region Constructor

		public SecretKeyFieldAttribute(SecretKind kind = SecretKind.String)
		{
			Kind = kind;
		}

		#endregion
	}
}

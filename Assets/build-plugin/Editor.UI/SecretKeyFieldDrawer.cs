using System;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Draws any string field tagged <see cref="SecretKeyFieldAttribute"/> (a plain Data-side marker - see its
	/// doc for why the attribute lives there and not in an Odin processor) as a dropdown of the project
	/// registry's logical keys, filtered by the attribute's <see cref="SecretKind"/>, plus a "Register new"
	/// entry that opens the shared <see cref="SecretRegisterDialog"/> pre-filled - so secret-key fields are
	/// picked from what actually exists instead of free-typed (#29). The current value stays selectable even
	/// when unregistered (marked), so opening a definition never silently rewrites committed data.
	/// </summary>
	internal sealed class SecretKeyFieldDrawer : OdinAttributeDrawer<SecretKeyFieldAttribute, string>
	{
		#region Fields

		// Menu callbacks (and the register dialog's onRegistered) fire OUTSIDE the IMGUI pass, where writing
		// ValueEntry directly is unsafe - the choice parks here and is applied at the start of the next draw.
		private string _pendingValue;

		#endregion

		#region OdinAttributeDrawer

		protected override void DrawPropertyLayout(GUIContent label)
		{
			if (_pendingValue != null)
			{
				ValueEntry.SmartValue = _pendingValue;
				_pendingValue = null;
			}

			string current = ValueEntry.SmartValue;
			Rect rect = EditorGUILayout.GetControlRect();
			if (label != null) rect = EditorGUI.PrefixLabel(rect, label);

			GUIContent content = new GUIContent(string.IsNullOrEmpty(current) ? "(none)" : current,
				"Logical secret key (" + Attribute.Kind + ") - pick a registered key, or register a new one.");
			if (EditorGUI.DropdownButton(rect, content, FocusType.Keyboard))
			{
				BuildMenu(current).DropDown(rect);
			}
		}

		#endregion

		#region Private Methods

		private GenericMenu BuildMenu(string current)
		{
			GenericMenu menu = new GenericMenu();
			ProjectConfig project = SecretProvisioner.FindProjectConfig();

			bool currentListed = false;
			if (project != null && project.SecretRegistry != null)
			{
				foreach (SecretDeclaration declaration in project.SecretRegistry)
				{
					if (declaration == null || string.IsNullOrEmpty(declaration.LogicalKey)) continue;
					if (declaration.Kind != Attribute.Kind) continue;

					string key = declaration.LogicalKey;
					if (key == current) currentListed = true;
					menu.AddItem(new GUIContent(key), key == current, () => _pendingValue = key);
				}
			}

			// The committed value may predate the registry (or the entry was removed) - keep it selectable,
			// flagged, so the dropdown can always represent (and restore) what is actually serialized.
			if (!string.IsNullOrEmpty(current) && !currentListed)
			{
				menu.AddItem(new GUIContent(current + "  (unregistered)"), true, () => _pendingValue = current);
			}

			if (menu.GetItemCount() > 0) menu.AddSeparator("");

			if (project != null)
			{
				ProjectConfig capturedProject = project;
				string prefill = current;
				menu.AddItem(new GUIContent("Register new"), false, () =>
					SecretRegisterDialog.OpenForRegister(capturedProject, prefill, Attribute.Kind,
						description: "", usedBy: Array.Empty<string>(), keyEditable: true,
						onRegistered: key => _pendingValue = key, onDone: null));
			}
			else
			{
				menu.AddDisabledItem(new GUIContent("Register new  (no ProjectConfig - run the setup wizard)"));
			}

			return menu;
		}

		#endregion
	}
}

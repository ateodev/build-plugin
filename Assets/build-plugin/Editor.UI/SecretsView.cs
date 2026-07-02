using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The Secrets registry pane (§12.6 / §11.2), demand-driven: rows are the UNION of the committed registry
	/// entries and the keys the project's definitions actually need (<see cref="SecretDemand"/>), so a key an
	/// action declares but nobody registered shows up here (greyed) instead of failing the next build. One
	/// STATUS column is also the action: "OK" (+ an Edit affordance opening the three-verb manage dialog),
	/// "Fix value" when the reference does not resolve in the vault, "Register" for needed-but-unregistered
	/// keys, and "OK - unused" (+ Remove) for orphan entries. Registered, needed rows additionally carry a right-click
	/// context menu (Change reference / Copy reference) - repointing an entry has no column of its own.
	/// Provider status is stated ONCE in the header; when the
	/// provider is unavailable every status renders "unknown" and all write buttons are disabled with the
	/// reason. Presence is probed async via the provider's <see cref="ISecretProvider.ExistsAsync"/>; a
	/// signed-out provider is tolerated (status "unknown", never an error).
	/// </summary>
	internal sealed class SecretsView : IPanelView
	{
		#region Fields

		private BuildPanel _owner;
		private ProjectConfig _project;
		private List<SecretDemand.Row> _rows = new List<SecretDemand.Row>();

		// Logical key -> vault presence: true = resolves, false = does not, null = unknown (unprobeable).
		private readonly Dictionary<string, bool?> _presence = new Dictionary<string, bool?>();
		private bool _checking;

		#endregion

		#region IPanelView

		public bool HasActiveBuild => false;

		public void Refresh(BuildPanel owner)
		{
			_owner = owner;
			_project = owner.Project;
			_rows = SecretDemand.Classify(_project, SecretDemand.CollectDefinitions());
			CheckPresence();
		}

		#endregion

		#region Drawing

		[OnInspectorGUI]
		private void Draw()
		{
			if (_owner == null) return;

			// The build-time provider is the panel's status anchor (cheap, sync, local check - §11.1). Rows with
			// a different scheme still probe through their own provider; this header states the common case once.
			ISecretProvider provider = SecretProviders.ForBuild();
			bool available = provider != null && provider.IsAvailable();

			SirenixEditorGUI.BeginBox("Secret registry");
			{
				DrawProviderStatus(provider, available);

				// Disabled while a check is in flight: CheckPresence() would silently no-op on its _checking guard.
				using (new EditorGUI.DisabledScope(_checking))
				{
					if (GUILayout.Button(new GUIContent("Refresh",
						"Recompute needed keys from the project's definitions and re-probe the vault."))) Refresh(_owner);
				}

				if (_project == null)
				{
					EditorGUILayout.LabelField("No ProjectConfig found - run the project-setup wizard first.", EditorStyles.miniLabel);
					SirenixEditorGUI.EndBox();
					return;
				}

				if (_rows.Count == 0)
				{
					EditorGUILayout.LabelField("No secrets registered - and nothing in the project needs one.", EditorStyles.miniLabel);
					SirenixEditorGUI.EndBox();
					return;
				}

				DrawHeaderRow();
				SirenixEditorGUI.DrawThickHorizontalSeparator(1, 1);

				foreach (SecretDemand.Row row in _rows)
				{
					DrawRow(row, available);
				}

				if (_checking)
				{
					GUILayout.Space(4);
					EditorGUILayout.LabelField("Probing the vault...", EditorStyles.miniLabel);
				}
			}
			SirenixEditorGUI.EndBox();
		}

		private static void DrawProviderStatus(ISecretProvider provider, bool available)
		{
			if (provider == null)
			{
				EditorGUILayout.HelpBox("No secret provider is resolvable - set the UNITYBUILD_PROVIDER_* environment " +
					"or the team's provider params.", MessageType.Error);
				return;
			}

			if (available)
			{
				EditorGUILayout.LabelField("Provider '" + provider.Scheme + "': available", EditorStyles.miniLabel);
				return;
			}

			// One loud line up here instead of per-row noise; the per-row statuses just read "unknown".
			EditorGUILayout.HelpBox("Provider '" + provider.Scheme + "': not available - sign in to probe or write.\n" +
				provider.UnavailableHint, MessageType.Warning);
		}

		private static void DrawHeaderRow()
		{
			using (new EditorGUILayout.HorizontalScope())
			{
				EditorGUILayout.LabelField("Key", EditorStyles.boldLabel, GUILayout.Width(170));
				EditorGUILayout.LabelField("Kind", EditorStyles.boldLabel, GUILayout.Width(44));
				EditorGUILayout.LabelField("Status", EditorStyles.boldLabel, GUILayout.Width(130));
				EditorGUILayout.LabelField("Action", EditorStyles.boldLabel, GUILayout.Width(110));
				EditorGUILayout.LabelField("Needed by", EditorStyles.boldLabel);
				GUILayout.Space(64); // the Remove column
			}
		}

		private void DrawRow(SecretDemand.Row row, bool providerAvailable)
		{
			// Named scope: its rect (the full row's horizontal extent) anchors the right-click context menu.
			using (EditorGUILayout.HorizontalScope rowScope = new EditorGUILayout.HorizontalScope())
			{
				// Needed-but-unregistered rows are greyed: they exist only as demand, not as committed data yet.
				using (new EditorGUI.DisabledScope(row.State == SecretDemand.State.NeededUnregistered))
				{
					EditorGUILayout.LabelField(new GUIContent(row.Key, row.Description), GUILayout.Width(170));
				}

				EditorGUILayout.LabelField(row.Kind.ToString(), GUILayout.Width(44));

				DrawStatusCell(row, providerAvailable);
				DrawActionCell(row, providerAvailable);

				string neededBy = row.Consumers != null && row.Consumers.Count > 0
					? string.Join(", ", row.Consumers)
					: row.Declaration != null && row.Declaration.UsedBy != null
						? string.Join(", ", row.Declaration.UsedBy)
						: "";
				EditorGUILayout.LabelField(new GUIContent(neededBy, neededBy), EditorStyles.miniLabel);

				DrawRemoveButton(row);
				HandleRowContextMenu(rowScope.rect, row, providerAvailable);
			}
		}

		/// <summary>
		/// Right-click verbs for REGISTERED, needed rows: "Change reference" (repoint/rewrite via the full
		/// register dialog in overwrite mode - a one-click shortcut past the Edit dialog's Reassign verb, kept
		/// for muscle memory) and "Copy reference" (the reference is a POINTER, never the secret value - safe
		/// on the clipboard, and it has no home in any dialog). Attached as a plain ContextClick check over the
		/// row scope's rect - no extra controls, no layout cost. Unregistered and unused rows get NO menu:
		/// their verbs (Register / Remove) already exist, and neither has a reference worth copying or
		/// repointing. A dangling (not resolving) row keeps the menu - its Fix value button opens the same
		/// dialog, but Copy reference has no other home there.
		/// </summary>
		private void HandleRowContextMenu(Rect rowRect, SecretDemand.Row row, bool providerAvailable)
		{
			if (row.Declaration == null || row.State == SecretDemand.State.RegisteredUnused) return;

			Event current = Event.current;
			if (current.type != EventType.ContextClick || !rowRect.Contains(current.mousePosition)) return;

			GenericMenu menu = new GenericMenu();
			if (providerAvailable)
			{
				menu.AddItem(new GUIContent("Change reference"), false, () => OpenChangeReferenceDialog(row));
			}
			else
			{
				// Same rule as the write buttons: no provider, no writes (the disabled entry keeps the verb discoverable).
				menu.AddDisabledItem(new GUIContent("Change reference"));
			}

			menu.AddItem(new GUIContent("Copy reference"), false,
				() => EditorGUIUtility.systemCopyBuffer = row.Declaration.Reference);

			menu.ShowAsContext();
			current.Use();
		}

		/// <summary>
		/// The Status cell is TEXT ONLY (dev1: buttons sharing the status column looked bad) - the row's verb
		/// lives in the separate Action column (<see cref="DrawActionCell"/>). States: "OK" (resolves),
		/// "not resolving" (registered, vault has nothing at the reference), "not registered" (demand-only row),
		/// "OK - unused" (orphan - nothing broken), "unknown" (unprobeable).
		/// </summary>
		private void DrawStatusCell(SecretDemand.Row row, bool providerAvailable)
		{
			const float width = 130f;
			bool? present = providerAvailable && _presence.TryGetValue(row.Key, out bool? probed) ? probed : null;

			string text;
			string tooltip;
			if (row.State == SecretDemand.State.NeededUnregistered)
			{
				text = "not registered";
				tooltip = "Needed by the project but has no registry entry yet - use Register to create one.";
			}
			else if (row.State == SecretDemand.State.RegisteredUnused)
			{
				text = present == true ? "OK - unused" : present == false ? "unused (not resolving)" : "unused (unknown)";
				tooltip = "Registered but nothing in the project needs it. Remove deletes only the registry entry - the vault is untouched.";
			}
			else if (present == true)
			{
				text = "OK";
				tooltip = "The reference resolves in the vault.";
			}
			else if (present == false)
			{
				text = "not resolving";
				tooltip = "The reference does not resolve in the vault - use Fix value to write a fresh value " +
					"or point it at a different vault item.";
			}
			else
			{
				text = "unknown";
				tooltip = providerAvailable
					? "Presence could not be probed."
					: "Presence could not be probed (provider signed out / offline).";
			}

			EditorGUILayout.LabelField(new GUIContent(text, tooltip), EditorStyles.miniLabel, GUILayout.Width(width));
		}

		/// <summary>
		/// The Action column - one button per row, verb matched to the status: Register (not registered),
		/// Edit (OK/unknown - opens the three-verb manage dialog: change value / reassign / delete), Fix value
		/// (not resolving; the label signals action is REQUIRED, per dev1). Fix value opens the FULL register
		/// dialog in overwrite mode, not the manage surface: a dangling reference may point at a DELETED item,
		/// and an in-place value write can never fix that - the fix must lead with repointing (dev1 got locked
		/// into a deleted item). Unused rows get no action here - theirs is the Remove column. All write
		/// buttons disable with the reason when the provider is down.
		/// </summary>
		private void DrawActionCell(SecretDemand.Row row, bool providerAvailable)
		{
			const float width = 110f;
			string disabledReason = providerAvailable ? null : "Provider not available - sign in to probe or write.";

			using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
			{
				if (row.State == SecretDemand.State.RegisteredUnused)
				{
					GUILayout.Space(width); // no action - Remove (trailing column) is this row's verb
					return;
				}

				using (new EditorGUI.DisabledScope(!providerAvailable))
				{
					if (row.State == SecretDemand.State.NeededUnregistered)
					{
						if (GUILayout.Button(new GUIContent("Register",
							disabledReason ?? ("Create or select the vault secret for '" + row.Key + "' and record its reference.")),
							GUILayout.Width(width)))
						{
							OpenRegisterDialog(row);
						}
						return;
					}

					bool? present = providerAvailable && _presence.TryGetValue(row.Key, out bool? probed) ? probed : null;
					if (present == false)
					{
						if (GUILayout.Button(new GUIContent("Fix value",
							"The reference is dangling (nothing in the vault behind it) - write a fresh value, " +
							"or point the entry at a different vault item."), GUILayout.Width(width)))
						{
							OpenChangeReferenceDialog(row);
						}
					}
					else
					{
						if (GUILayout.Button(new GUIContent("Edit",
							disabledReason ?? "Manage this secret - change its value, reassign it, or delete the assignment."),
							GUILayout.Width(width)))
						{
							OpenManageDialog(row);
						}
					}
				}
			}
		}

		/// <summary>
		/// Remove on every REGISTERED row - including NEEDED keys (dev1: the old hard demand-guard was
		/// redundant - removal makes the row visibly fall back to needed-unregistered, so the confirm states
		/// the consequences instead of a block). The tooltip stays informative (who needs it); removal only
		/// ever deletes the registry ENTRY - the vault item is untouched.
		/// </summary>
		private void DrawRemoveButton(SecretDemand.Row row)
		{
			if (row.Declaration == null)
			{
				GUILayout.Space(64);
				return;
			}

			bool needed = row.State != SecretDemand.State.RegisteredUnused;
			string tooltip = needed
				? "Remove this registry entry (the secret in the vault is untouched). Still needed by " +
					string.Join("; ", row.Consumers) + " - the row falls back to 'not registered' until re-registered."
				: "Remove this registry entry (the secret in the vault is untouched).";

			if (GUILayout.Button(new GUIContent("Remove", tooltip), GUILayout.Width(60))) ConfirmRemove(row);
		}

		/// <summary>
		/// One confirm for both row flavors, message built by the shared
		/// <see cref="SecretDemand.RemoveConfirmMessage"/> (identical wording to the manage dialog's Delete
		/// verb): a NEEDED key gets the consequences-stating form under the 'Delete secret assignment?' title,
		/// an unused one keeps the simpler entry-only form.
		/// </summary>
		private void ConfirmRemove(SecretDemand.Row row)
		{
			bool needed = row.State != SecretDemand.State.RegisteredUnused;
			bool confirmed = EditorUtility.DisplayDialog(
				needed ? "Delete secret assignment?" : "Remove registry entry",
				SecretDemand.RemoveConfirmMessage(row.Key, needed ? row.Consumers : null),
				needed ? "Delete" : "Remove", "Cancel");
			if (!confirmed) return;

			SecretProvisioner.RemoveSecret(_project, row.Key);
			Refresh(_owner);
		}

		private void OpenRegisterDialog(SecretDemand.Row row)
		{
			SecretRegisterDialog.OpenForRegister(_project, row.Key, row.Kind, row.Description,
				row.Consumers != null ? row.Consumers.ToArray() : Array.Empty<string>(),
				keyEditable: false, onRegistered: null, onDone: () => Refresh(_owner));
		}

		private void OpenManageDialog(SecretDemand.Row row)
		{
			SecretRegisterDialog.OpenForSetValue(_project, row.Declaration, onDone: () => Refresh(_owner));
		}

		private void OpenChangeReferenceDialog(SecretDemand.Row row)
		{
			SecretRegisterDialog.OpenForChangeReference(_project, row.Declaration,
				row.Consumers != null ? row.Consumers.ToArray() : Array.Empty<string>(),
				onDone: () => Refresh(_owner));
		}

		#endregion

		#region Async

		private void CheckPresence()
		{
			if (_owner == null || _project == null || _checking) return;

			_checking = true;
			_owner.RunAsync(CheckPresenceAsync(), () => _checking = false);
		}

		private async Task CheckPresenceAsync()
		{
			_presence.Clear();

			// Snapshot the rows: the registry can be edited (Inspector, dialogs) while probes are in flight, and
			// enumerating a live list across awaits would throw "Collection was modified" into the panel status.
			List<SecretDemand.Row> snapshot = new List<SecretDemand.Row>(_rows);

			foreach (SecretDemand.Row row in snapshot)
			{
				if (row.Declaration == null || string.IsNullOrEmpty(row.Key)) continue;

				_presence[row.Key] = await ProbeAsync(row.Declaration);
			}
		}

		private static async Task<bool?> ProbeAsync(SecretDeclaration declaration)
		{
			// Per-reference provider (the scheme picks it), coordinates from the build environment / defaults (§11.7).
			ISecretProvider provider = SecretProviders.ResolveWithBuildCoords(declaration.Ref.Scheme);
			if (provider == null || !provider.IsAvailable() || !provider.Caps.Presence) return null;

			try
			{
				return await provider.ExistsAsync(declaration.Ref);
			}
			catch (Exception)
			{
				// Provider not signed in / offline - tolerate and report unknown (§12.6).
				return null;
			}
		}

		#endregion
	}
}

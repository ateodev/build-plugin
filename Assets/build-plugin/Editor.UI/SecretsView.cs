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
	/// STATUS column is also the action: "OK" (+ a Set value... rotation affordance), "Fix value..." when the
	/// reference does not resolve in the vault, "Register..." for needed-but-unregistered keys, and
	/// "OK - unused" (+ Remove) for orphan entries. Provider status is stated ONCE in the header; when the
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
				EditorGUILayout.LabelField("Status", EditorStyles.boldLabel, GUILayout.Width(200));
				EditorGUILayout.LabelField("Needed by", EditorStyles.boldLabel);
				GUILayout.Space(64); // the Remove column
			}
		}

		private void DrawRow(SecretDemand.Row row, bool providerAvailable)
		{
			using (new EditorGUILayout.HorizontalScope())
			{
				// Needed-but-unregistered rows are greyed: they exist only as demand, not as committed data yet.
				using (new EditorGUI.DisabledScope(row.State == SecretDemand.State.NeededUnregistered))
				{
					EditorGUILayout.LabelField(new GUIContent(row.Key, row.Description), GUILayout.Width(170));
				}

				EditorGUILayout.LabelField(row.Kind.ToString(), GUILayout.Width(44));

				DrawStatusCell(row, providerAvailable);

				string neededBy = row.Consumers != null && row.Consumers.Count > 0
					? string.Join(", ", row.Consumers)
					: row.Declaration != null && row.Declaration.UsedBy != null
						? string.Join(", ", row.Declaration.UsedBy)
						: "";
				EditorGUILayout.LabelField(new GUIContent(neededBy, neededBy), EditorStyles.miniLabel);

				DrawRemoveButton(row);
			}
		}

		/// <summary>
		/// The single Status cell - text and action in one place. Registered rows: "OK" + Set value... (rotation)
		/// when the reference resolves; "Fix value..." AS the status when it does not (the label itself signals
		/// action is required); "unknown" when unprobeable. Unregistered needed rows: the Register... button IS
		/// the status. Unused rows: an "OK - unused" tag (nothing is broken - the entry is merely orphaned; its
		/// action is the Remove column). All write buttons disable with the reason when the provider is down.
		/// </summary>
		private void DrawStatusCell(SecretDemand.Row row, bool providerAvailable)
		{
			const float width = 200f;
			string disabledReason = providerAvailable ? null : "Provider not available - sign in to probe or write.";

			using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
			{
				if (row.State == SecretDemand.State.NeededUnregistered)
				{
					using (new EditorGUI.DisabledScope(!providerAvailable))
					{
						if (GUILayout.Button(new GUIContent("Register...",
							disabledReason ?? ("Create or select the vault secret for '" + row.Key + "' and record its reference.")),
							GUILayout.Width(110)))
						{
							OpenRegisterDialog(row);
						}
					}

					GUILayout.FlexibleSpace();
					return;
				}

				bool? present = providerAvailable && _presence.TryGetValue(row.Key, out bool? probed) ? probed : null;

				if (row.State == SecretDemand.State.RegisteredUnused)
				{
					string tag = present == true ? "OK - unused" : present == false ? "unused (not resolving)" : "unused (unknown)";
					EditorGUILayout.LabelField(new GUIContent(tag,
						"Registered but nothing in the project needs it. Remove deletes only the registry entry - the vault is untouched."),
						EditorStyles.miniLabel, GUILayout.Width(150));
					GUILayout.FlexibleSpace();
					return;
				}

				if (present == true)
				{
					EditorGUILayout.LabelField(new GUIContent("OK", "The reference resolves in the vault."), GUILayout.Width(30));
					if (GUILayout.Button(new GUIContent("Set value...", "Write a NEW value (rotation). The current value is never shown."),
						GUILayout.Width(90)))
					{
						OpenSetValueDialog(row);
					}
				}
				else if (present == false)
				{
					// The button IS the status: registered, but the vault has nothing at the reference.
					if (GUILayout.Button(new GUIContent("Fix value...",
						"The reference does not resolve in the vault - write its value now."), GUILayout.Width(110)))
					{
						OpenSetValueDialog(row);
					}
				}
				else
				{
					EditorGUILayout.LabelField(new GUIContent("unknown",
						disabledReason ?? "Presence could not be probed (provider signed out / offline)."), GUILayout.Width(70));
					using (new EditorGUI.DisabledScope(!providerAvailable))
					{
						if (GUILayout.Button(new GUIContent("Set value...",
							disabledReason ?? "Write a NEW value. The current value is never shown."), GUILayout.Width(90)))
						{
							OpenSetValueDialog(row);
						}
					}
				}

				GUILayout.FlexibleSpace();
			}
		}

		/// <summary>
		/// Remove on every REGISTERED row, demand-guarded: a needed key's entry cannot be removed (disabled with
		/// who needs it), an unused one can. Removes only the registry ENTRY - the vault item is untouched.
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
				? "Required by " + string.Join("; ", row.Consumers) + " - remove the consumer first."
				: "Remove this registry entry (the secret in the vault is untouched).";

			using (new EditorGUI.DisabledScope(needed))
			{
				if (GUILayout.Button(new GUIContent("Remove", tooltip), GUILayout.Width(60))) ConfirmRemove(row);
			}
		}

		private void ConfirmRemove(SecretDemand.Row row)
		{
			bool confirmed = EditorUtility.DisplayDialog("Remove registry entry",
				"Remove '" + row.Key + "' from the project's secret registry?\n\n" +
				"Only the registry entry is removed - the secret in the vault is untouched.",
				"Remove", "Cancel");
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

		private void OpenSetValueDialog(SecretDemand.Row row)
		{
			SecretRegisterDialog.OpenForSetValue(_project, row.Declaration, onDone: () => Refresh(_owner));
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

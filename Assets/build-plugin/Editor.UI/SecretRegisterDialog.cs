using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The one reusable secrets dialog (Secrets UX spec #3/#4): REGISTER a logical key (create a new vault item
	/// by convention and write its value now, or point the registry at an existing vault item - no free-text
	/// reference mode), MANAGE an assigned key (dev1: the one-stop surface behind the Secrets view's Edit
	/// button - CHANGE VALUE writes a new value to the existing reference, plus the verb row REASSIGN (morphs
	/// this same window into change-reference mode) / UNASSIGN (removes only the registry entry, vault
	/// untouched) / DELETE (destroys the vault item itself AND the assignment), each behind a consequences
	/// confirm), and CHANGE REFERENCE (the register UI over an EXISTING entry, key fixed - completing it
	/// OVERWRITES the entry's reference, the escape hatch when the current one dangles or should point
	/// elsewhere). Strictly WRITE-ONLY: values are entered masked and never read back or displayed. Opened from the
	/// Secrets view, the definition banner and the secret-key dropdown drawer; all writes go through the shared
	/// <see cref="SecretProvisioner"/> so item naming and registry rules exist once. Provider-agnostic: only
	/// <see cref="ISecretProvider"/> verbs are used.
	/// </summary>
	internal sealed class SecretRegisterDialog : EditorWindow
	{
		#region Open

		/// <summary>
		/// Open in REGISTER mode for a logical key. <paramref name="keyEditable"/> is true only for the
		/// dropdown drawer's "Register new" (the key is the user's to invent there); demand-driven callers
		/// (Secrets view, definition banner) pass false - the key is fixed by the code that declared it.
		/// </summary>
		public static void OpenForRegister(ProjectConfig project, string logicalKey, SecretKind kind, string description,
			string[] usedBy, bool keyEditable, Action<string> onRegistered, Action onDone)
		{
			SecretRegisterDialog window = CreateInstance<SecretRegisterDialog>();
			window.titleContent = new GUIContent("Register Secret");
			window._project = project;
			window._logicalKey = logicalKey ?? "";
			window._kind = kind;
			window._description = description ?? "";
			window._usedBy = usedBy ?? Array.Empty<string>();
			window._keyEditable = keyEditable;
			window._onRegistered = onRegistered;
			window._onDone = onDone;
			window.Initialize(setValueOnly: false, declaration: null);
			window.ShowUtility();
		}

		/// <summary>
		/// Open in CHANGE REFERENCE mode: the full register UI (create-new by convention / use-existing) over an
		/// ALREADY-registered entry - the key is fixed, and completing either tab REPLACES the entry's reference
		/// (<see cref="SecretProvisioner.RegisterSecret"/> with overwrite; the entry is updated in place, never
		/// duplicated). This is how a DANGLING reference gets fixed - write a fresh value under the convention
		/// name, or repoint at a different vault item - and how a healthy one gets deliberately repointed.
		/// </summary>
		public static void OpenForChangeReference(ProjectConfig project, SecretDeclaration declaration, string[] usedBy,
			Action onDone)
		{
			SecretRegisterDialog window = CreateInstance<SecretRegisterDialog>();
			string logicalKey = declaration != null ? declaration.LogicalKey : "";
			// The title carries the key: with the register UI reused, only the title says this UPDATES an entry.
			window.titleContent = new GUIContent("Change reference - " + logicalKey);
			window._project = project;
			window._logicalKey = logicalKey;
			window._kind = declaration != null ? declaration.Kind : SecretKind.String;
			window._description = declaration != null ? declaration.Description : "";
			window._usedBy = usedBy ?? Array.Empty<string>();
			window._keyEditable = false;
			window._onDone = onDone;
			window._changeReference = true;
			window.Initialize(setValueOnly: false, declaration: declaration);
			window.ShowUtility();
		}

		/// <summary>
		/// Open in MANAGE mode (the Secrets view's Edit button on a healthy row): the one-stop surface for an
		/// ASSIGNED key. Primary verb is CHANGE VALUE (write a new value to <paramref name="declaration"/>'s
		/// EXISTING reference - rotation; no naming, no registry change); the verb row is REASSIGN (morphs this
		/// window into change-reference mode, see <see cref="OpenForChangeReference"/>), UNASSIGN (removes the
		/// registry entry only - vault untouched) and DELETE (destroys the vault item AND the assignment), the
		/// latter two behind consequences confirms.
		/// </summary>
		public static void OpenForSetValue(ProjectConfig project, SecretDeclaration declaration, Action onDone)
		{
			SecretRegisterDialog window = CreateInstance<SecretRegisterDialog>();
			string logicalKey = declaration != null ? declaration.LogicalKey : "";
			window.titleContent = new GUIContent("Manage secret - " + logicalKey);
			window._project = project;
			window._logicalKey = logicalKey;
			window._kind = declaration != null ? declaration.Kind : SecretKind.String;
			window._description = declaration != null ? declaration.Description : "";
			window._usedBy = Array.Empty<string>();
			window._keyEditable = false;
			window._onDone = onDone;
			window.Initialize(setValueOnly: true, declaration: declaration);
			window.ShowUtility();
		}

		#endregion

		#region Fields

		[NonSerialized] private ProjectConfig _project;
		[NonSerialized] private string _logicalKey = "";
		[NonSerialized] private SecretKind _kind = SecretKind.String;
		[NonSerialized] private string _description = "";
		[NonSerialized] private string[] _usedBy = Array.Empty<string>();
		[NonSerialized] private bool _keyEditable;
		[NonSerialized] private Action<string> _onRegistered;
		[NonSerialized] private Action _onDone;

		[NonSerialized] private bool _setValueOnly;
		[NonSerialized] private bool _changeReference;
		[NonSerialized] private SecretDeclaration _declaration;
		[NonSerialized] private string _writeItem;
		[NonSerialized] private string _writeField;
		[NonSerialized] private bool _writeCoordsOk;

		[NonSerialized] private ISecretProvider _provider;
		[NonSerialized] private bool _providerUsable;

		[NonSerialized] private SourceMode _mode = SourceMode.CreateNew;

		// Value input (write-only; never pre-filled from the vault).
		[NonSerialized] private string _stringValue = "";
		[NonSerialized] private string _filePath = "";

		// Use-existing pickers (#17 select-existing pattern: enumerate through the provider seam).
		[NonSerialized] private List<string> _items;
		[NonSerialized] private List<string> _fields;
		[NonSerialized] private int _itemIndex = -1;
		[NonSerialized] private int _fieldIndex = -1;

		[NonSerialized] private string _error = "";

		#endregion

		#region Initialization

		private void Initialize(bool setValueOnly, SecretDeclaration declaration)
		{
			// Manage mode carries the extra verb row (Reassign / Unassign / Delete) below the value input.
			minSize = new Vector2(460, setValueOnly ? 240 : 320);
			_setValueOnly = setValueOnly;
			_declaration = declaration;

			ResolveProvider();

			if (_setValueOnly && _declaration != null)
			{
				// One guarded parse feeds BOTH item-addressed verbs: Change value writes to (_writeItem,
				// _writeField), Delete targets _writeItem's TITLE (SecretProvisioner.TryGetItemName is the same
				// parse) - so a foreign-convention reference disables both alike.
				_writeCoordsOk = _provider != null &&
					SecretProvisioner.TryGetWriteCoordinates(_provider, _declaration, out _writeItem, out _writeField);
			}
		}

		/// <summary>
		/// Resolve the provider ONCE, on open (a user action - ResolveTeamProvider may fetch team coords).
		/// Register writes go to the team's provider; Set Value must write where the EXISTING reference lives, so
		/// when the declaration's scheme differs from the team provider's, the scheme picks the provider instead
		/// (with build-env/default coordinates) - the round-trip check in TryGetWriteCoordinates then guards
		/// against coordinate mismatches.
		/// </summary>
		private void ResolveProvider()
		{
			_provider = SecretProvisioner.ResolveTeamProvider(_project);

			if (_setValueOnly && _declaration != null)
			{
				string scheme = _declaration.Ref.Scheme;
				if (!string.IsNullOrEmpty(scheme) && (_provider == null || _provider.Scheme != scheme))
				{
					_provider = SecretProviders.ResolveWithBuildCoords(scheme);
				}
			}

			_providerUsable = _provider != null && _provider.IsAvailable();
		}

		#endregion

		#region GUI

		private void OnGUI()
		{
			if (_project == null && !_setValueOnly)
			{
				EditorGUILayout.HelpBox("No ProjectConfig - run the project-setup wizard first.", MessageType.Error);
				return;
			}

			DrawHeader();
			GUILayout.Space(6);

			if (!_providerUsable)
			{
				EditorGUILayout.HelpBox(_provider == null
					? "No secret provider is resolvable - check the team's provider params (or the UNITYBUILD_PROVIDER_* environment)."
					: _provider.UnavailableHint, MessageType.Error);
			}

			// Manage mode scopes provider-availability itself: UNASSIGN is a registry-only edit and must stay
			// usable with the provider signed out (the view's Unassign button works without one too).
			if (_setValueOnly)
			{
				DrawManage();
			}
			else
			{
				using (new EditorGUI.DisabledScope(!_providerUsable))
				{
					DrawRegister();
				}
			}

			if (!string.IsNullOrEmpty(_error))
			{
				GUILayout.Space(4);
				EditorGUILayout.HelpBox(_error, MessageType.Error);
			}
		}

		private void DrawHeader()
		{
			if (_keyEditable)
			{
				_logicalKey = EditorGUILayout.TextField(
					new GUIContent("Logical key", "The registry key consuming code/fields resolve (e.g. MATCH_PASSWORD)."),
					_logicalKey);
			}
			else
			{
				EditorGUILayout.LabelField("Logical key", _logicalKey, EditorStyles.boldLabel);
			}

			EditorGUILayout.LabelField("Kind", _kind.ToString());
			if (!string.IsNullOrEmpty(_description))
			{
				EditorGUILayout.LabelField("Description", _description, EditorStyles.wordWrappedMiniLabel);
			}

			// Change-reference mode: show what is being REPLACED - the one visual difference from plain register.
			if (_changeReference && _declaration != null && !string.IsNullOrEmpty(_declaration.Reference))
			{
				string current = _declaration.Reference;
				EditorGUILayout.LabelField(
					new GUIContent("Current reference", "The entry's reference today - completing this dialog replaces it."),
					new GUIContent(current, current), EditorStyles.miniLabel);
			}
		}

		// --- Register mode ----------------------------------------------------------------------------------

		private void DrawRegister()
		{
			GUILayout.Space(4);
			_mode = (SourceMode)GUILayout.Toolbar((int)_mode, new[]
			{
				new GUIContent("Create new", "Create a vault item by convention and write its value now."),
				new GUIContent("Use existing", "Point the registry at an item already in the vault (no value written).")
			});
			GUILayout.Space(6);

			if (_mode == SourceMode.CreateNew) DrawCreateNew();
			else DrawUseExisting();
		}

		private void DrawCreateNew()
		{
			// The reference is DERIVED (provider convention via ReferenceFor over the conventional item name) -
			// shown so the user knows where the value will land, but never hand-editable (no free-text references).
			string item = SecretProvisioner.ItemNameFor(_project, _logicalKey);
			string preview = _provider != null
				? _provider.ReferenceFor(item, SecretProvisioner.ValueField, _kind).Reference
				: "(no provider)";
			EditorGUILayout.LabelField(new GUIContent("Reference", "Derived from the provider's convention - not editable."),
				new GUIContent(preview, preview), EditorStyles.miniLabel);

			GUILayout.Space(4);
			DrawValueInput();

			GUILayout.Space(8);
			using (new EditorGUI.DisabledScope(!CanCreate()))
			{
				// In change-reference mode the verb says UPDATE: same write+register path, but the user must know
				// the existing entry is repointed (RegisterSecret overwrites in place), not a new one added.
				string verb = _changeReference ? "Write and update reference" : "Create and register";
				if (GUILayout.Button(verb, GUILayout.Height(26))) CreateAndRegister(item);
			}
		}

		private void DrawUseExisting()
		{
			if (_items == null)
			{
				EditorGUILayout.LabelField("Pick an existing vault item to point '" + _logicalKey + "' at.",
					EditorStyles.wordWrappedMiniLabel);
				if (GUILayout.Button("Load vault items")) LoadItems();
				return;
			}

			if (_items.Count == 0)
			{
				EditorGUILayout.HelpBox("The provider listed no items (or cannot enumerate). Use 'Create new' instead.",
					MessageType.Info);
				return;
			}

			int newItemIndex = EditorGUILayout.Popup("Vault item", _itemIndex, _items.ToArray());
			if (newItemIndex != _itemIndex)
			{
				_itemIndex = newItemIndex;
				_fields = null; // field list belongs to the previously selected item
				_fieldIndex = -1;
				if (_kind == SecretKind.String) LoadFields(_items[_itemIndex]);
			}

			// A File secret is a field-less document - the item IS the reference. String secrets need a field,
			// enumerated via ReadRecordAsync; ONLY its field LABELS are used, values are never rendered (write-only).
			if (_kind == SecretKind.String && _itemIndex >= 0)
			{
				if (_fields == null)
				{
					EditorGUILayout.LabelField("Fields", "loading...", EditorStyles.miniLabel);
				}
				else if (_fields.Count == 0)
				{
					EditorGUILayout.HelpBox("This item has no readable fields (none holds a value) - pick another item " +
						"or use 'Create new'.", MessageType.Warning);
				}
				else
				{
					_fieldIndex = EditorGUILayout.Popup("Field", _fieldIndex, _fields.ToArray());
				}
			}

			string reference = SelectedExistingReference();
			if (!string.IsNullOrEmpty(reference))
			{
				EditorGUILayout.LabelField(new GUIContent("Reference"), new GUIContent(reference, reference), EditorStyles.miniLabel);
			}

			GUILayout.Space(8);
			using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(_logicalKey)))
			{
				string verb = _changeReference ? "Update reference (no value written)" : "Register (no value written)";
				if (GUILayout.Button(verb, GUILayout.Height(26))) RegisterExisting(reference);
			}
		}

		// --- Manage mode (assigned key: change value / reassign / unassign / delete) ------------------------

		private void DrawManage()
		{
			string reference = _declaration != null ? _declaration.Reference : "";
			EditorGUILayout.LabelField(new GUIContent("Reference"), new GUIContent(reference, reference), EditorStyles.miniLabel);

			if (!_writeCoordsOk)
			{
				// No early return: an unrecoverable item target only kills CHANGE VALUE and DELETE - Reassign
				// is the remedy and Unassign never needs coordinates, so the verb row below must still render.
				EditorGUILayout.HelpBox("This reference does not follow the provider's own convention (or points at " +
					"different provider coordinates), so the item behind it cannot be recovered safely - the value " +
					"cannot be changed here and Delete cannot target the vault item. Use Reassign to repoint the " +
					"entry (or manage the item in the vault directly).", MessageType.Warning);
			}
			else
			{
				GUILayout.Space(4);
				using (new EditorGUI.DisabledScope(!_providerUsable))
				{
					DrawValueInput();
				}
			}

			GUILayout.Space(8);
			DrawManageVerbs();
		}

		/// <summary>
		/// The manage verb row (dev1 layout): the primary write button first, then - visually separated - the
		/// entry-level verbs [Reassign][Unassign][Delete], the destructive one last. Each verb gates on exactly
		/// what it needs: Change value on a usable provider AND recoverable write coordinates, Reassign on the
		/// provider only (it opens the register write UI), Unassign on nothing (a registry-only edit that must
		/// survive a signed-out provider), Delete on a usable provider AND a recoverable item title (it destroys
		/// the vault item itself).
		/// </summary>
		private void DrawManageVerbs()
		{
			using (new EditorGUILayout.HorizontalScope())
			{
				using (new EditorGUI.DisabledScope(!_providerUsable || !_writeCoordsOk || !HasValueInput()))
				{
					if (GUILayout.Button(new GUIContent("Change value",
						"Write the entered value to the existing reference (rotation). The current value is never shown."),
						GUILayout.Height(26)))
					{
						WriteExistingValue();
					}
				}

				GUILayout.FlexibleSpace();

				using (new EditorGUI.DisabledScope(!_providerUsable))
				{
					if (GUILayout.Button(new GUIContent("Reassign",
						"Point '" + _logicalKey + "' at a different vault item (or write a fresh one by convention) - " +
						"switches this window to the change-reference UI."), GUILayout.Height(26)))
					{
						MorphToChangeReference();
					}
				}

				if (GUILayout.Button(new GUIContent("Unassign",
					"Remove the registry entry for '" + _logicalKey + "' - the secret in the vault is untouched."),
					GUILayout.Height(26)))
				{
					UnassignEntry();
				}

				// The disabled tooltip carries the REASON (dev1 rule: a dead button must say why) - and points
				// at Unassign, which stays available for the registry-only half of the intent.
				string deleteBlocked = !_providerUsable
					? "Provider not available - sign in to delete the vault item (Unassign works without it)."
					: !_writeCoordsOk
						? "The vault item behind this reference cannot be determined safely (non-convention " +
							"reference) - delete it in the vault directly, or Unassign the entry here."
						: null;
				using (new EditorGUI.DisabledScope(deleteBlocked != null))
				{
					if (GUILayout.Button(new GUIContent("Delete",
						deleteBlocked ?? ("Delete the vault item '" + _writeItem + "' AND remove the registry " +
							"entry for '" + _logicalKey + "'.")), GUILayout.Height(26)))
					{
						DeleteVaultSecret();
					}
				}
			}
		}

		// --- Shared value input (write-only: masked string / file path, never a read-back) --------------------

		private void DrawValueInput()
		{
			if (_kind == SecretKind.String)
			{
				_stringValue = EditorGUILayout.PasswordField(
					new GUIContent("Value", "Entered write-only; the current value is never shown."), _stringValue);
				return;
			}

			using (new EditorGUILayout.HorizontalScope())
			{
				_filePath = EditorGUILayout.TextField(
					new GUIContent("File", "The file whose bytes become the secret document."), _filePath);
				if (GUILayout.Button("Browse", GUILayout.Width(76)))
				{
					string picked = EditorUtility.OpenFilePanel("Secret file", "", "");
					if (!string.IsNullOrEmpty(picked)) _filePath = picked;
					GUI.FocusControl(null); // drop the text field's edit buffer so the picked path shows immediately
				}
			}
		}

		#endregion

		#region Actions

		private bool CanCreate()
		{
			return !string.IsNullOrEmpty(_logicalKey) && HasValueInput();
		}

		private bool HasValueInput()
		{
			return _kind == SecretKind.String ? !string.IsNullOrEmpty(_stringValue) : File.Exists(_filePath);
		}

		private void CreateAndRegister(string item)
		{
			_error = "";
			try
			{
				SecretValue value = _kind == SecretKind.String
					? SecretValue.OfString(_stringValue)
					: SecretValue.OfFile(File.ReadAllBytes(_filePath));

				// Write first, register the WRITE-CONFIRMED reference (never a fabricated pointer): a failed
				// write throws and nothing is registered.
				SecretRef written = SecretProvisioner.WriteSecret(_provider, item, SecretProvisioner.ValueField, value);
				SecretProvisioner.RegisterSecret(_project, _logicalKey, _description, _kind, written.Reference,
					_usedBy, overwriteExisting: true);

				Finish();
			}
			catch (Exception exception)
			{
				_error = "Could not create the secret: " + exception.Message;
			}
		}

		private void RegisterExisting(string reference)
		{
			_error = "";
			try
			{
				SecretProvisioner.RegisterSecret(_project, _logicalKey, _description, _kind, reference,
					_usedBy, overwriteExisting: true);
				Finish();
			}
			catch (Exception exception)
			{
				_error = "Could not register: " + exception.Message;
			}
		}

		private void WriteExistingValue()
		{
			_error = "";
			try
			{
				SecretValue value = _kind == SecretKind.String
					? SecretValue.OfString(_stringValue)
					: SecretValue.OfFile(File.ReadAllBytes(_filePath));

				SecretProvisioner.WriteSecret(_provider, _writeItem, _writeField, value);
				Finish();
			}
			catch (Exception exception)
			{
				_error = "Could not write the value: " + exception.Message;
			}
		}

		/// <summary>
		/// The REASSIGN verb: MORPH this window into the change-reference UI - the exact surface
		/// <see cref="OpenForChangeReference"/> opens, over the same entry - instead of spawning a second
		/// window (dev1). Mode flags flip, the title now says what the window is, the typed-but-unwritten
		/// value is dropped, and the provider is re-resolved: manage mode may have picked the declaration's
		/// scheme provider, but register writes go to the TEAM provider.
		/// </summary>
		private void MorphToChangeReference()
		{
			_setValueOnly = false;
			_changeReference = true;
			// Same consumer labels OpenForChangeReference receives from the view - merged into UsedBy on register.
			_usedBy = CurrentConsumers().ToArray();
			_stringValue = "";
			_filePath = "";
			_error = "";
			titleContent = new GUIContent("Change reference - " + _logicalKey);
			minSize = new Vector2(460, 320);
			ResolveProvider();
			Repaint();
		}

		/// <summary>
		/// The UNASSIGN verb: removes the REGISTRY ENTRY only - the vault item behind the reference is
		/// deliberately untouched (<see cref="SecretProvisioner.RemoveSecret"/>) - behind a consequences
		/// confirm. Consumers are computed FRESH from the project's definitions (not a stale row snapshot),
		/// so the confirm names exactly who still needs the key right now. On confirm: remove (saves the
		/// asset), notify the opener (the Secrets view refreshes via onDone) and close.
		/// </summary>
		private void UnassignEntry()
		{
			bool confirmed = EditorUtility.DisplayDialog("Unassign secret?",
				SecretDemand.UnassignConfirmMessage(_logicalKey, CurrentConsumers()),
				"Unassign", "Cancel");
			if (!confirmed) return;

			SecretProvisioner.RemoveSecret(_project, _logicalKey);
			_onDone?.Invoke();
			Close();
		}

		/// <summary>
		/// The DELETE verb: destroys the VAULT ITEM behind the reference (dev1's expectation: after Delete,
		/// 'build-plugin-test_steam-user' is GONE from the vault) and removes the registry entry with it.
		/// Ordering is deliberate - vault first, registry second: a failed vault delete leaves the entry and the
		/// vault exactly as they were, with the error surfaced; nothing is ever half-deleted. The confirm names
		/// the item verbatim and carries a CAUTION when the item looks shared (<see cref="SharedItemReason"/>).
		/// </summary>
		private void DeleteVaultSecret()
		{
			bool confirmed = EditorUtility.DisplayDialog("Delete vault secret?",
				SecretDemand.DeleteConfirmMessage(_logicalKey, _writeItem, SharedItemReason(_writeItem), CurrentConsumers()),
				"Delete", "Cancel");
			if (!confirmed) return;

			_error = "";
			try
			{
				// WizardShell.RunSync is Task<T>-shaped; wrap the void delete so it still runs off the Editor
				// main thread (same deadlock rationale as every other provider call here).
				WizardShell.RunSync(async () =>
				{
					await _provider.DeleteItemAsync(_writeItem);
					return true;
				});

				SecretProvisioner.RemoveSecret(_project, _logicalKey);
				_onDone?.Invoke();
				Close();
			}
			catch (Exception exception)
			{
				_error = "Could not delete the vault item: " + exception.Message + " Nothing was removed.";
			}
		}

		/// <summary>
		/// Why deleting <paramref name="item"/> could break MORE than this project's entry - null when it looks
		/// safely project-owned. Two shared shapes exist: another registry entry resolves to the SAME item (two
		/// logical keys, one item - deleting for one silently breaks the other), detected by recovering every
		/// other entry's item title through the same guarded parse; or the title lacks the '&lt;project-key&gt;_'
		/// prefix - by the naming convention (<see cref="SecretProvisioner.ItemNameFor(ProjectConfig,string)"/>)
		/// only team-level/reusable items are bare-named, and OTHER projects may consume those.
		/// </summary>
		private string SharedItemReason(string item)
		{
			if (_project != null && _project.SecretRegistry != null)
			{
				foreach (SecretDeclaration other in _project.SecretRegistry)
				{
					if (other == null || string.Equals(other.LogicalKey, _logicalKey, StringComparison.Ordinal)) continue;
					if (SecretProvisioner.TryGetItemName(_provider, other, out string otherItem) &&
						string.Equals(otherItem, item, StringComparison.Ordinal))
					{
						return "the registry entry '" + other.LogicalKey + "' points at the same item";
					}
				}
			}

			// ItemNameFor with an empty type key yields exactly the project prefix ('<project-key>_') - reused
			// so the kebab-folding rule keeps its one home.
			string projectPrefix = SecretProvisioner.ItemNameFor(_project, string.Empty);
			if (!item.StartsWith(projectPrefix, StringComparison.Ordinal))
			{
				return "its bare name (no '" + projectPrefix + "' prefix) marks it as team-level, not project-owned";
			}

			return null;
		}

		/// <summary>Who needs this key RIGHT NOW, recomputed from the project's definitions - the dialog holds no
		/// row snapshot, and demand can change while it is open.</summary>
		private List<string> CurrentConsumers()
		{
			Dictionary<string, SecretDemand.NeededSecret> needed =
				SecretDemand.ComputeNeeded(SecretDemand.CollectDefinitions());

			return needed.TryGetValue(_logicalKey, out SecretDemand.NeededSecret demand)
				? demand.Consumers
				: new List<string>();
		}

		private void Finish()
		{
			_onRegistered?.Invoke(_logicalKey);
			_onDone?.Invoke();
			Close();
		}

		private void LoadItems()
		{
			_error = "";
			try
			{
				// Enumerate through the provider seam (empty prefix = everything the provider lists). RunSync
				// keeps the provider chain off the main thread's SynchronizationContext (Editor deadlock).
				IReadOnlyList<string> titles = WizardShell.RunSync(() => _provider.ListItemsAsync(""));
				_items = titles != null ? new List<string>(titles) : new List<string>();
			}
			catch (Exception exception)
			{
				_items = new List<string>();
				_error = "Could not list vault items: " + exception.Message;
			}
		}

		private void LoadFields(string item)
		{
			try
			{
				// ReadRecordAsync returns label -> value; only the LABELS are kept - values are discarded
				// immediately and never rendered (the dialog is write-only by contract). A value's LENGTH is
				// checked, though: 1Password items carry built-in EMPTY fields (e.g. 'password' on a
				// Login/Password-category item), and offering those phantoms alongside the real field made the
				// picker a guessing game - only fields that actually hold a value are real choices here.
				IReadOnlyDictionary<string, string> record = WizardShell.RunSync(() => _provider.ReadRecordAsync(item));
				_fields = new List<string>();
				if (record != null)
				{
					foreach (KeyValuePair<string, string> field in record)
					{
						if (string.IsNullOrEmpty(field.Key) || field.Key == "notesPlain") continue;
						if (string.IsNullOrEmpty(field.Value)) continue; // a phantom built-in - nothing to point at

						_fields.Add(field.Key);
					}
				}

				// One real field = no choice to make - preselect it so the reference preview appears immediately.
				if (_fields.Count == 1) _fieldIndex = 0;
			}
			catch (Exception exception)
			{
				_fields = new List<string>();
				_error = "Could not read the item's fields: " + exception.Message;
			}

			Repaint();
		}

		private string SelectedExistingReference()
		{
			if (_provider == null || _itemIndex < 0 || _itemIndex >= (_items != null ? _items.Count : 0)) return null;

			string item = _items[_itemIndex];
			if (_kind == SecretKind.File) return _provider.ReferenceFor(item, null, SecretKind.File).Reference;
			if (_fields == null || _fieldIndex < 0 || _fieldIndex >= _fields.Count) return null;

			return _provider.ReferenceFor(item, _fields[_fieldIndex], SecretKind.String).Reference;
		}

		#endregion

		#region Nested Types

		private enum SourceMode
		{
			CreateNew,
			UseExisting
		}

		#endregion
	}
}

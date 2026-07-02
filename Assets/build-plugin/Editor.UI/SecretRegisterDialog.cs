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
	/// reference mode), and SET VALUE (write a new value to an already-registered reference; key + reference
	/// fixed). Strictly WRITE-ONLY: values are entered masked and never read back or displayed. Opened from the
	/// Secrets view, the definition banner and the secret-key dropdown drawer; all writes go through the shared
	/// <see cref="SecretProvisioner"/> so item naming and registry rules exist once. Provider-agnostic: only
	/// <see cref="ISecretProvider"/> verbs are used.
	/// </summary>
	internal sealed class SecretRegisterDialog : EditorWindow
	{
		#region Open

		/// <summary>
		/// Open in REGISTER mode for a logical key. <paramref name="keyEditable"/> is true only for the
		/// dropdown drawer's "Register new..." (the key is the user's to invent there); demand-driven callers
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

		/// <summary>Open in SET VALUE mode: write a new value to <paramref name="declaration"/>'s EXISTING
		/// reference (rotation / fixing a non-resolving entry). No naming, no mode choice, no registry change.</summary>
		public static void OpenForSetValue(ProjectConfig project, SecretDeclaration declaration, Action onDone)
		{
			SecretRegisterDialog window = CreateInstance<SecretRegisterDialog>();
			window.titleContent = new GUIContent("Set Secret Value");
			window._project = project;
			window._logicalKey = declaration != null ? declaration.LogicalKey : "";
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
			minSize = new Vector2(460, setValueOnly ? 220 : 320);
			_setValueOnly = setValueOnly;
			_declaration = declaration;

			ResolveProvider();

			if (_setValueOnly && _declaration != null)
			{
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

			using (new EditorGUI.DisabledScope(!_providerUsable))
			{
				if (_setValueOnly) DrawSetValue();
				else DrawRegister();
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
				if (GUILayout.Button("Create and register", GUILayout.Height(26))) CreateAndRegister(item);
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
					EditorGUILayout.HelpBox("Could not enumerate the item's fields - pick another item or use 'Create new'.",
						MessageType.Warning);
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
				if (GUILayout.Button("Register (no value written)", GUILayout.Height(26))) RegisterExisting(reference);
			}
		}

		// --- Set-value mode ---------------------------------------------------------------------------------

		private void DrawSetValue()
		{
			string reference = _declaration != null ? _declaration.Reference : "";
			EditorGUILayout.LabelField(new GUIContent("Reference"), new GUIContent(reference, reference), EditorStyles.miniLabel);

			if (!_writeCoordsOk)
			{
				EditorGUILayout.HelpBox("This reference does not follow the provider's own convention (or points at " +
					"different provider coordinates), so its write target cannot be recovered safely. Edit the value " +
					"in the vault directly, or re-register the key.", MessageType.Warning);
				return;
			}

			GUILayout.Space(4);
			DrawValueInput();

			GUILayout.Space(8);
			using (new EditorGUI.DisabledScope(!HasValueInput()))
			{
				if (GUILayout.Button("Write value", GUILayout.Height(26))) WriteExistingValue();
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
				if (GUILayout.Button("Browse...", GUILayout.Width(76)))
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
				// immediately and never rendered (the dialog is write-only by contract).
				IReadOnlyDictionary<string, string> record = WizardShell.RunSync(() => _provider.ReadRecordAsync(item));
				_fields = new List<string>();
				if (record != null)
				{
					foreach (string label in record.Keys)
					{
						if (!string.IsNullOrEmpty(label) && label != "notesPlain") _fields.Add(label);
					}
				}
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

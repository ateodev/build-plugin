using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The project-level Settings pane (§12.6), reached from the top menu bar. Edits the per-user connection
	/// (the server URL + access token, stored in <see cref="BuildServerSettings"/>) and, inline, the committed
	/// <see cref="ProjectConfig"/> (team, notification target, secret registry).
	/// </summary>
	internal sealed class SettingsView : IPanelView
	{
		#region Fields

		private BuildPanel _owner;

		// The committed ProjectConfig, drawn inline (null until one exists / the wizard creates it).
		[ShowInInspector, InlineEditor, HideLabel, PropertyOrder(1)]
		private ProjectConfig _project;

		#endregion

		#region IPanelView

		public bool HasActiveBuild => false;

		public void Refresh(BuildPanel owner)
		{
			_owner = owner;
			_project = owner.Project;
		}

		#endregion

		#region Drawing

		[OnInspectorGUI, PropertyOrder(0)]
		private void DrawConnection()
		{
			if (_owner == null) return;

			SirenixEditorGUI.BeginBox("Connection (this machine only)");
			{
				// Delayed (commit on Enter/focus-loss), not per-keystroke: the setting falls back to the
				// canonical default when empty, so a live field would snap back mid-edit the moment the
				// user cleared it to type a new URL. The getter's default also serves as the display value,
				// so a fresh machine shows the canonical server instead of an empty prompt.
				EditorGUI.BeginChangeCheck();
				string serverUrl = EditorGUILayout.DelayedTextField(
					new GUIContent("Server URL", "TeamCity base URL the plugin talks to, e.g. https://build.ateonet.work " +
						"or http://localhost:8111. A per-machine setting - stored on this machine only, never committed."),
					BuildServerSettings.ServerBaseUrl);
				if (EditorGUI.EndChangeCheck()) BuildServerSettings.ServerBaseUrl = serverUrl;

				using (new EditorGUILayout.HorizontalScope())
				{
					// Only write the EditorPrefs-backed token on an actual edit, not on every repaint.
					EditorGUI.BeginChangeCheck();
					string token = EditorGUILayout.PasswordField(
						new GUIContent("Access token", "Your permission-scoped TeamCity token. Never an admin token, never committed."),
						BuildServerSettings.Token);
					if (EditorGUI.EndChangeCheck()) BuildServerSettings.Token = token;

					// A PasswordField masks its content, so an explicit reset is the only confident way to
					// retire a stale/mistyped token.
					if (GUILayout.Button(new GUIContent("Clear", "Forget the token stored on this machine"), GUILayout.Width(50f)))
					{
						BuildServerSettings.Token = string.Empty;
						// Drop the focused field's own edit buffer, or it re-shows (and could re-commit) the old value.
						GUI.FocusControl(null);
					}
				}

				if (GUILayout.Button("Test Connection")) _owner.RunAsync(TestConnectionAsync());
			}
			SirenixEditorGUI.EndBox();

			GUILayout.Space(4);

			if (_project == null)
			{
				SirenixEditorGUI.MessageBox(
					"No ProjectConfig asset found - this project hasn't been onboarded yet. Run the Project Setup " +
					"Wizard to create one.", MessageType.Warning);

				if (GUILayout.Button("Open Project Setup Wizard")) ProjectSetupWizard.Open(_owner);
			}
		}

		#endregion

		#region Async

		private async Task TestConnectionAsync()
		{
			await _owner.DiscoverExecutorsAsync();
			_owner.SetStatus("Connected. Discovered " + _owner.Executors.Count + " executor(s): " +
				string.Join(", ", _owner.Executors.Keys));
		}

		#endregion
	}
}

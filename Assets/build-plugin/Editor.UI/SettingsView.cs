using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The project-level Settings pane (§12.6), reached from the top menu bar. Edits the per-user connection
	/// (the access token, stored in <see cref="BuildServerSettings"/>) and, inline, the committed
	/// <see cref="ProjectConfig"/> (server URL, team, Slack channel, secret registry).
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
				// Only write the EditorPrefs-backed token on an actual edit, not on every repaint.
				EditorGUI.BeginChangeCheck();
				string token = EditorGUILayout.PasswordField(
					new GUIContent("Access token", "Your permission-scoped TeamCity token. Never an admin token, never committed."),
					BuildServerSettings.Token);
				if (EditorGUI.EndChangeCheck()) BuildServerSettings.Token = token;

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

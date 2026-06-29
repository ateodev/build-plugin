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
	/// The Secrets registry pane (§12.6 / §11.2): one row per committed <see cref="SecretDeclaration"/> showing
	/// the logical key, its scheme (scope), kind, and presence. Server-present is probed via the provider's
	/// <see cref="ISecretProvider.ExistsAsync"/> against the shared vault; local-present would come from a local
	/// key-index (not yet implemented - shown as "unknown"). A provider that isn't signed in is tolerated: the
	/// presence shows "unknown" rather than erroring.
	/// </summary>
	internal sealed class SecretsView : IPanelView
	{
		#region Fields

		private BuildPanel _owner;
		private ProjectConfig _project;
		private readonly Dictionary<string, string> _serverPresence = new Dictionary<string, string>();
		private bool _checking;

		#endregion

		#region IPanelView

		public bool HasActiveBuild => false;

		public void Refresh(BuildPanel owner)
		{
			_owner = owner;
			_project = owner.Project;
			CheckPresence();
		}

		#endregion

		#region Drawing

		[OnInspectorGUI]
		private void Draw()
		{
			if (_owner == null) return;

			SirenixEditorGUI.BeginBox("Secret registry");
			{
				if (GUILayout.Button("Re-check presence")) CheckPresence();

				if (_project == null || _project.SecretRegistry == null || _project.SecretRegistry.Count == 0)
				{
					EditorGUILayout.LabelField("No secrets registered on the project's ProjectConfig.", EditorStyles.miniLabel);
					SirenixEditorGUI.EndBox();
					return;
				}

				DrawHeaderRow();
				SirenixEditorGUI.DrawThickHorizontalSeparator(1, 1);

				foreach (SecretDeclaration declaration in _project.SecretRegistry)
				{
					if (declaration == null) continue;

					DrawRow(declaration);
				}

				GUILayout.Space(4);
				EditorGUILayout.LabelField(
					_checking ? "Checking presence..." : "Local-present needs a local key-index (not yet implemented) - shown as 'unknown'.",
					EditorStyles.miniLabel);
			}
			SirenixEditorGUI.EndBox();
		}

		private static void DrawHeaderRow()
		{
			using (new EditorGUILayout.HorizontalScope())
			{
				EditorGUILayout.LabelField("Key", EditorStyles.boldLabel, GUILayout.Width(160));
				EditorGUILayout.LabelField("Scope", EditorStyles.boldLabel, GUILayout.Width(70));
				EditorGUILayout.LabelField("Kind", EditorStyles.boldLabel, GUILayout.Width(50));
				EditorGUILayout.LabelField("Server", EditorStyles.boldLabel, GUILayout.Width(70));
				EditorGUILayout.LabelField("Local", EditorStyles.boldLabel, GUILayout.Width(70));
				EditorGUILayout.LabelField("Used by", EditorStyles.boldLabel);
			}
		}

		private void DrawRow(SecretDeclaration declaration)
		{
			string scheme = declaration.Ref.Scheme ?? "(none)";
			string server = _serverPresence.TryGetValue(declaration.LogicalKey, out string presence) ? presence : "unknown";

			using (new EditorGUILayout.HorizontalScope())
			{
				EditorGUILayout.LabelField(new GUIContent(declaration.LogicalKey, declaration.Description), GUILayout.Width(160));
				EditorGUILayout.LabelField(scheme, GUILayout.Width(70));
				EditorGUILayout.LabelField(declaration.Kind.ToString(), GUILayout.Width(50));
				EditorGUILayout.LabelField(server, GUILayout.Width(70));
				EditorGUILayout.LabelField("unknown", GUILayout.Width(70));
				EditorGUILayout.LabelField(declaration.UsedBy != null ? string.Join(", ", declaration.UsedBy) : "");
			}
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
			_serverPresence.Clear();
			if (_project.SecretRegistry == null) return;

			foreach (SecretDeclaration declaration in _project.SecretRegistry)
			{
				if (declaration == null || string.IsNullOrEmpty(declaration.LogicalKey)) continue;

				_serverPresence[declaration.LogicalKey] = await ProbeAsync(declaration);
			}
		}

		private static async Task<string> ProbeAsync(SecretDeclaration declaration)
		{
			ISecretProvider provider = ProviderFor(declaration.Ref.Scheme);
			if (provider == null || !provider.Caps.Presence) return "unknown";

			try
			{
				bool exists = await provider.ExistsAsync(declaration.Ref);
				return exists ? "yes" : "no";
			}
			catch (Exception)
			{
				// Provider not signed in / offline - tolerate and report unknown (§12.6).
				return "unknown";
			}
		}

		private static ISecretProvider ProviderFor(string scheme)
		{
			switch (scheme)
			{
				case OnePasswordProvider.SchemeName:
					return new OnePasswordProvider();
				default:
					return null;
			}
		}

		#endregion
	}
}

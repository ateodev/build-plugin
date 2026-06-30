using System.Collections.Generic;
using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The team-wide Activity pane (§12.4), pinned at the bottom of the sidebar with a live-count badge. Merges
	/// the queue (<c>/buildQueue</c>, with position) and everything running, token-scoped so cross-team builds are
	/// invisible. Each row: game · definition · status · agent; actions: open, cancel, and (for this project's game)
	/// jump-to-definition. Polled while open and non-empty.
	/// </summary>
	internal sealed class ActivityView : IPanelView
	{
		#region Fields

		private BuildPanel _owner;
		private List<BuildStatus> _inFlight = new List<BuildStatus>();
		private bool _loading;

		#endregion

		#region IPanelView

		public bool HasActiveBuild => _inFlight != null && _inFlight.Count > 0;

		public void Refresh(BuildPanel owner)
		{
			_owner = owner;
			if (_loading) return;

			_loading = true;
			owner.RunAsync(LoadAsync(owner), () => _loading = false);
		}

		#endregion

		#region Drawing

		[OnInspectorGUI]
		private void Draw()
		{
			if (_owner == null) return;

			SirenixEditorGUI.BeginBox("Activity - team in-flight builds" + (_loading ? "  (loading...)" : ""));
			{
				if (GUILayout.Button("Reload")) Refresh(_owner);

				if (string.IsNullOrEmpty(_owner.Token))
				{
					EditorGUILayout.LabelField("Set an access token in Settings to see in-flight builds.", EditorStyles.miniLabel);
					SirenixEditorGUI.EndBox();
					return;
				}

				if (_inFlight.Count == 0)
				{
					EditorGUILayout.LabelField(_loading ? "Loading..." : "No queued or running builds.", EditorStyles.miniLabel);
				}

				foreach (BuildStatus build in _inFlight)
				{
					DrawRow(build);
				}
			}
			SirenixEditorGUI.EndBox();
		}

		private void DrawRow(BuildStatus build)
		{
			SirenixEditorGUI.BeginBox();
			using (new EditorGUILayout.HorizontalScope())
			{
				string project = string.IsNullOrEmpty(build.Project) ? "?" : build.Project;
				string definition = string.IsNullOrEmpty(build.Definition) ? "?" : build.Definition;
				EditorGUILayout.LabelField(project + " · " + definition, EditorStyles.boldLabel, GUILayout.Width(220));
				EditorGUILayout.LabelField(Describe(build), EditorStyles.miniLabel);

				GUILayout.FlexibleSpace();

				if (!string.IsNullOrEmpty(build.WebUrl) && GUILayout.Button("Open", GUILayout.Width(50)))
				{
					Application.OpenURL(_owner.ResolveServerLink(build.WebUrl));
				}

				bool isThisProject = _owner.Project != null && build.Definition != null &&
					(string.IsNullOrEmpty(build.Project) || build.Project == _owner.Project.ProjectKey);
				if (isThisProject && GUILayout.Button("Jump", GUILayout.Width(50)))
				{
					_owner.SelectDefinitionByName(build.Definition);
				}

				if (GUILayout.Button("Cancel", GUILayout.Width(60))) CancelBuild(build);
			}
			SirenixEditorGUI.EndBox();
		}

		#endregion

		#region Async

		private async Task LoadAsync(BuildPanel owner)
		{
			if (string.IsNullOrEmpty(owner.Token))
			{
				_inFlight = new List<BuildStatus>();
				owner.SetActivityCount(0);
				return;
			}

			using (TeamCityClient client = owner.NewClient())
			{
				_inFlight = await client.ListInFlightAsync();
			}

			owner.SetActivityCount(_inFlight.Count);
		}

		private void CancelBuild(BuildStatus build)
		{
			_owner.RunAsync(CancelAsync(build), () => Refresh(_owner));
		}

		private async Task CancelAsync(BuildStatus build)
		{
			using (TeamCityClient client = _owner.NewClient())
			{
				await client.CancelAsync(build.Id, build.IsQueued);
				_owner.SetStatus("Cancelled build " + build.Id + ".");
			}
		}

		#endregion

		#region Helpers

		private static string Describe(BuildStatus build)
		{
			if (build.IsRunning)
			{
				string agent = string.IsNullOrEmpty(build.Agent) ? "" : "  @" + build.Agent;
				return "running " + build.PercentageComplete + "%  " + build.StatusText + agent;
			}

			if (build.IsQueued) return "queued (#" + build.QueuePosition + ")  " + build.StatusText;
			return build.State + "  " + build.StatusText;
		}

		#endregion
	}
}

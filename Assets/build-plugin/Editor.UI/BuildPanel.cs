using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The v2 control plane (§12). An <see cref="OdinMenuEditorWindow"/> whose left menu tree IS the sidebar:
	/// the project's <see cref="BuildDefinition"/> assets grouped by target, with a pinned <c>Activity</c> view
	/// (live count badge). The top menu bar adds a definition (wizard placeholder, P2.D) and swaps the right pane
	/// to the project-level Settings / Secrets views. The right pane is master-detail: a definition shows
	/// Build / Configure tabs (<see cref="DefinitionView"/>); Settings / Secrets / Activity are single views.
	/// Refresh is on-select + a manual reload + a focused periodic poll while the open view has an active build.
	/// </summary>
	public sealed class BuildPanel : OdinMenuEditorWindow
	{
		#region Fields

		private readonly Dictionary<BuildDefinition, DefinitionView> _definitionViews = new Dictionary<BuildDefinition, DefinitionView>();
		private SettingsView _settingsView;
		private SecretsView _secretsView;
		private ActivityView _activityView;

		private BuildDefinition[] _definitions = Array.Empty<BuildDefinition>();
		private ProjectConfig _project;
		private string _baseUrl = "";
		private Dictionary<string, string> _executors = new Dictionary<string, string>();

		private object _paneOverride;
		private object _pendingSelect;
		private string _status = "";
		private int _activityCount;

		private bool _pollingHooked;
		private double _lastPoll;

		#endregion

		#region Properties

		/// <summary>The project's resolved <see cref="ProjectConfig"/> (null until found / created).</summary>
		internal ProjectConfig Project => _project;

		/// <summary>Base URL of the TeamCity server the panel talks to.</summary>
		internal string BaseUrl => _baseUrl;

		/// <summary>The user's permission-scoped access token (machine-local).</summary>
		internal string Token => BuildServerSettings.Token;

		/// <summary>The discovered platform-token -&gt; executor map (populated by <see cref="DiscoverExecutorsAsync"/>).</summary>
		internal IReadOnlyDictionary<string, string> Executors => _executors;

		/// <summary>The Unity project root (parent of Assets) - the on-disk Builds/ layout lives here.</summary>
		internal static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

		#endregion

		#region MenuItem

		[MenuItem("Window/Build Panel")]
		private static void Open()
		{
			BuildPanel window = GetWindow<BuildPanel>("Build Panel");
			window.MenuWidth = 240;
			window.Show();
		}

		#endregion

		#region OdinMenuEditorWindow

		protected override OdinMenuTree BuildMenuTree()
		{
			Reload();

			OdinMenuTree tree = new OdinMenuTree(false);
			tree.Config.DrawSearchToolbar = true;

			foreach (BuildDefinition definition in _definitions)
			{
				if (definition == null) continue;

				DefinitionView view = GetOrCreateView(definition);
				string path = GroupLabel(definition.Platform) + "/" + definition.DefinitionName;
				tree.Add(path, view);
			}

			if (_activityView == null) _activityView = new ActivityView();
			string activityName = _activityCount > 0 ? "Activity  (" + _activityCount + ")" : "Activity";
			tree.Add(activityName, _activityView);

			tree.Selection.SelectionChanged += OnSelectionChanged;
			return tree;
		}

		protected override IEnumerable<object> GetTargets()
		{
			if (_paneOverride != null) return new[] { _paneOverride };
			return base.GetTargets();
		}

		protected override void OnBeginDrawEditors()
		{
			SirenixEditorGUI.BeginHorizontalToolbar();
			{
				if (SirenixEditorGUI.ToolbarButton(new GUIContent("  + Add Build Definition")))
				{
					CreateDefinitionWizard.Open(this);
				}

				GUILayout.FlexibleSpace();

				if (SirenixEditorGUI.ToolbarButton(new GUIContent("Reload"))) ForceRebuild();
				if (SirenixEditorGUI.ToolbarButton(new GUIContent("⚙ Settings"))) ShowOverride(_settingsView ??= new SettingsView());
				if (SirenixEditorGUI.ToolbarButton(new GUIContent("\U0001F511 Secrets"))) ShowOverride(_secretsView ??= new SecretsView());
			}
			SirenixEditorGUI.EndHorizontalToolbar();

			if (!string.IsNullOrEmpty(_status))
			{
				SirenixEditorGUI.MessageBox(_status, MessageType.Info);
			}

			if (_project == null)
			{
				SirenixEditorGUI.MessageBox(
					"No ProjectConfig found - this project hasn't been onboarded yet. Run the project-setup wizard to create one.",
					MessageType.Warning);

				if (GUILayout.Button("Open Project Setup Wizard")) ProjectSetupWizard.Open(this);
			}
		}

		protected override void OnImGUI()
		{
			EnsurePolling();
			base.OnImGUI();
		}

		protected override void OnDestroy()
		{
			if (_pollingHooked)
			{
				EditorApplication.update -= Poll;
				_pollingHooked = false;
			}

			base.OnDestroy();
		}

		#endregion

		#region Internal API (used by the views)

		/// <summary>A fresh REST client bound to the current server URL + token. Caller disposes.</summary>
		internal TeamCityClient NewClient()
		{
			return new TeamCityClient(_baseUrl, Token);
		}

		/// <summary>Run an editor-async task, surfacing any error into the panel status and repainting on completion.</summary>
		internal async void RunAsync(Task task, Action onDone = null)
		{
			try
			{
				await task;
			}
			catch (Exception exception)
			{
				_status = "Error: " + exception.Message;
			}
			finally
			{
				onDone?.Invoke();
				Repaint();
			}
		}

		/// <summary>Set the panel status line (shown under the toolbar).</summary>
		internal void SetStatus(string status)
		{
			_status = status;
			Repaint();
		}

		/// <summary>
		/// The executor config id for a definition's platform: the discovered map first, the manual fallback second.
		/// Empty when neither is known (the caller turns that into an actionable message).
		/// </summary>
		internal string ResolveExecutor(BuildDefinition definition)
		{
			string token = definition.Platform.ToServerToken();
			if (_executors != null && _executors.TryGetValue(token, out string id) && !string.IsNullOrEmpty(id)) return id;
			return BuildServerSettings.BuildTypeId;
		}

		/// <summary>Discover the platform -&gt; executor map (called by views before triggering / listing server builds).</summary>
		internal async Task DiscoverExecutorsAsync()
		{
			if (string.IsNullOrEmpty(Token)) return;

			using (TeamCityClient client = NewClient())
			{
				_executors = await client.DiscoverExecutorsAsync();
			}
		}

		/// <summary>Update the Activity sidebar badge; rebuilds the tree (preserving selection) when the count changes.</summary>
		internal void SetActivityCount(int count)
		{
			if (count == _activityCount) return;

			_activityCount = count;
			ForceRebuild();
		}

		/// <summary>Jump to a definition's Build tab (Activity's "open in project" action).</summary>
		internal void SelectDefinition(BuildDefinition definition)
		{
			if (definition == null || !_definitionViews.TryGetValue(definition, out DefinitionView view)) return;

			_paneOverride = null;
			TrySelectMenuItemWithObject(view);
		}

		/// <summary>Rebuild the sidebar (re-scanning assets) and select the newly-created definition - the create-definition wizard's completion hook.</summary>
		internal void RefreshAndSelect(string definitionName)
		{
			_paneOverride = null;
			ForceMenuTreeRebuild();
			if (!string.IsNullOrEmpty(definitionName)) SelectDefinitionByName(definitionName);
			Repaint();
		}

		/// <summary>Rebuild the sidebar (re-scanning for the new ProjectConfig) and show Settings - the project-setup wizard's completion hook.</summary>
		internal void RefreshProject()
		{
			ForceMenuTreeRebuild();
			ShowOverride(_settingsView ??= new SettingsView());
		}

		/// <summary>Jump to a definition by its name (Activity's jump action for this project's game).</summary>
		internal void SelectDefinitionByName(string definitionName)
		{
			if (string.IsNullOrEmpty(definitionName)) return;

			foreach (BuildDefinition definition in _definitions)
			{
				if (definition != null && definition.DefinitionName == definitionName)
				{
					SelectDefinition(definition);
					return;
				}
			}
		}

		#endregion

		#region Private Methods

		private void OnSelectionChanged(SelectionChangedType type)
		{
			if (type != SelectionChangedType.ItemAdded) return;

			_paneOverride = null;
			_status = "";

			object selected = MenuTree?.Selection?.SelectedValue;
			if (selected is IPanelView view) view.Refresh(this);
		}

		private void ShowOverride(IPanelView view)
		{
			_paneOverride = view;
			_status = "";
			view.Refresh(this);
			Repaint();
		}

		private void ForceRebuild()
		{
			_pendingSelect = _paneOverride ?? MenuTree?.Selection?.SelectedValue;
			ForceMenuTreeRebuild();
			if (_pendingSelect != null && _paneOverride == null) TrySelectMenuItemWithObject(_pendingSelect);
			Repaint();
		}

		private DefinitionView GetOrCreateView(BuildDefinition definition)
		{
			if (!_definitionViews.TryGetValue(definition, out DefinitionView view))
			{
				view = new DefinitionView(definition, this);
				_definitionViews[definition] = view;
			}

			return view;
		}

		private void Reload()
		{
			List<BuildDefinition> definitions = new List<BuildDefinition>();
			foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BuildDefinition)))
			{
				BuildDefinition definition = AssetDatabase.LoadAssetAtPath<BuildDefinition>(AssetDatabase.GUIDToAssetPath(guid));
				if (definition != null) definitions.Add(definition);
			}

			definitions.Sort((a, b) =>
			{
				int byPlatform = string.CompareOrdinal(GroupLabel(a.Platform), GroupLabel(b.Platform));
				return byPlatform != 0 ? byPlatform : string.CompareOrdinal(a.DefinitionName, b.DefinitionName);
			});
			_definitions = definitions.ToArray();

			string[] projectGuids = AssetDatabase.FindAssets("t:" + nameof(ProjectConfig));
			_project = projectGuids.Length > 0
				? AssetDatabase.LoadAssetAtPath<ProjectConfig>(AssetDatabase.GUIDToAssetPath(projectGuids[0]))
				: null;

			if (string.IsNullOrEmpty(_baseUrl))
			{
				_baseUrl = _project != null ? _project.ServerBaseUrl : "https://build.ateonet.work";
			}

			// Drop cached views whose asset was deleted, so the dictionary doesn't leak.
			List<BuildDefinition> stale = new List<BuildDefinition>();
			foreach (KeyValuePair<BuildDefinition, DefinitionView> pair in _definitionViews)
			{
				if (pair.Key == null || Array.IndexOf(_definitions, pair.Key) < 0) stale.Add(pair.Key);
			}

			foreach (BuildDefinition key in stale) _definitionViews.Remove(key);
		}

		private void EnsurePolling()
		{
			if (_pollingHooked) return;

			_pollingHooked = true;
			EditorApplication.update += Poll;
		}

		private void Poll()
		{
			if (!hasFocus) return; // pause when unfocused (§12.4)
			if (EditorApplication.timeSinceStartup - _lastPoll < 3.0) return;

			_lastPoll = EditorApplication.timeSinceStartup;

			object current = _paneOverride ?? MenuTree?.Selection?.SelectedValue;
			if (current is IPanelView view && view.HasActiveBuild) view.Refresh(this);
		}

		private static string GroupLabel(BuildPlatform platform)
		{
			switch (platform)
			{
				case BuildPlatform.Android:           return "Android";
				case BuildPlatform.iOS:               return "iOS";
				case BuildPlatform.WindowsStandalone: return "Windows";
				case BuildPlatform.MacStandalone:     return "macOS";
				case BuildPlatform.LinuxStandalone:   return "Linux";
				case BuildPlatform.LinuxServer:       return "Linux Server";
				case BuildPlatform.WebGL:             return "WebGL";
				default:                              return platform.ToString();
			}
		}

		#endregion
	}

	/// <summary>A right-pane view that can refresh from the server and report whether it is watching a live build.</summary>
	internal interface IPanelView
	{
		/// <summary>Re-load this view's state (called on selection, manual reload, and the focused poll).</summary>
		void Refresh(BuildPanel owner);

		/// <summary>True while this view is showing a queued/running build - drives the focused periodic poll.</summary>
		bool HasActiveBuild { get; }
	}

	/// <summary>One row in a definition's unified Builds list - a server build, an on-disk build, or both.</summary>
	internal sealed class BuildRow
	{
		#region Fields

		public string Title;
		public string Detail;
		public bool OnServer;
		public bool LocalPresent;
		public string LocalPath;
		public long ServerId;
		public string WebUrl;
		public bool Live;

		#endregion
	}
}

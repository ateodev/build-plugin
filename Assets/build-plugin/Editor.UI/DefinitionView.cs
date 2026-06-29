using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The right-pane master-detail view for one <see cref="BuildDefinition"/> (§12.6): a <b>Build</b> tab
	/// (header, the unified Builds list, version/build-number/changeset overrides, and the Build Local / Build on
	/// Server trigger with <see cref="RunLocation"/>-filtered, disable-only action toggles) and a <b>Configure</b>
	/// tab (the definition's settings + its <c>[SerializeReference]</c> post-build-action list with a filtered,
	/// prerequisite-aware add-picker + signing references). Drawn by Odin via tab groups, custom IMGUI and an
	/// inline editor.
	/// </summary>
	internal sealed class DefinitionView : IPanelView
	{
		#region Fields

		private static readonly FieldInfo ActionsField =
			typeof(BuildDefinition).GetField("_postBuildActions", BindingFlags.NonPublic | BindingFlags.Instance);

		// Drawn inline in the Configure tab (settings + the [SerializeReference] action list + signing references).
		[TabGroup("Tabs", "Configure"), ShowInInspector, InlineEditor, HideLabel, PropertyOrder(1)]
		private readonly BuildDefinition _definition;

		private readonly BuildPanel _owner;

		// Disable-only B5 override (§10): keyed by action GUID, defaults enabled, persists across local<->remote switch.
		private readonly Dictionary<string, bool> _actionEnabled = new Dictionary<string, bool>();
		private TriggerTarget _target = TriggerTarget.Server;

		private string _versionOverride = "";
		private string _changeset = "";

		private List<BuildRow> _builds = new List<BuildRow>();
		private bool _loading;
		private string _localStatus = "";

		#endregion

		#region Constructor

		public DefinitionView(BuildDefinition definition, BuildPanel owner)
		{
			_definition = definition;
			_owner = owner;
		}

		#endregion

		#region IPanelView

		public bool HasActiveBuild => _builds != null && _builds.Any(row => row.Live);

		public void Refresh(BuildPanel owner)
		{
			if (_loading) return;

			_loading = true;
			owner.RunAsync(LoadBuildsAsync(owner), () => _loading = false);
		}

		#endregion

		#region Build Tab

		[TabGroup("Tabs", "Build"), OnInspectorGUI, PropertyOrder(0)]
		private void DrawBuildTab()
		{
			DrawHeader();
			GUILayout.Space(6);
			DrawBuildsList();
			GUILayout.Space(6);
			DrawOverrides();
			GUILayout.Space(6);
			DrawTrigger();
		}

		private void DrawHeader()
		{
			SirenixEditorGUI.BeginBox();
			{
				EditorGUILayout.LabelField(_definition.DefinitionName, EditorStyles.largeLabel);
				using (new EditorGUILayout.HorizontalScope())
				{
					EditorGUILayout.LabelField("Platform", _definition.Platform.ToServerToken());
				}

#if UNITY_6000_0_OR_NEWER
				string profile = _definition.Profile != null ? _definition.Profile.name : "(none - legacy scene list)";
				EditorGUILayout.LabelField("Active profile", profile);
#endif
			}
			SirenixEditorGUI.EndBox();
		}

		private void DrawBuildsList()
		{
			SirenixEditorGUI.BeginBox("Builds" + (_loading ? "  (loading...)" : ""));
			{
				if (GUILayout.Button("Reload builds")) Refresh(_owner);

				if (_builds.Count == 0)
				{
					EditorGUILayout.LabelField(_loading ? "Loading..." : "No builds found (local or server).", EditorStyles.miniLabel);
				}

				foreach (BuildRow row in _builds)
				{
					SirenixEditorGUI.BeginBox();
					using (new EditorGUILayout.HorizontalScope())
					{
						string tag = (row.Live ? "● " : "") + row.Title;
						EditorGUILayout.LabelField(tag, row.Live ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(200));
						EditorGUILayout.LabelField(row.Detail, EditorStyles.miniLabel);

						GUILayout.FlexibleSpace();
						EditorGUILayout.LabelField(row.LocalPresent ? "local" : "", EditorStyles.miniLabel, GUILayout.Width(40));
						EditorGUILayout.LabelField(row.OnServer ? "server" : "", EditorStyles.miniLabel, GUILayout.Width(46));

						if (!string.IsNullOrEmpty(row.WebUrl) && GUILayout.Button("Open", GUILayout.Width(50)))
						{
							Application.OpenURL(_owner.ResolveServerLink(row.WebUrl));
						}

						if (row.OnServer && !row.LocalPresent && row.ServerId > 0 && GUILayout.Button("Download", GUILayout.Width(80)))
						{
							DownloadBuild(row);
						}
					}
					SirenixEditorGUI.EndBox();
				}
			}
			SirenixEditorGUI.EndBox();
		}

		private void DrawOverrides()
		{
			SirenixEditorGUI.BeginBox("Overrides");
			{
				_versionOverride = EditorGUILayout.TextField(
					new GUIContent("Version name", "Override marketing version (unitybuild.version.name). " +
						"No-op until the BUILD_VERSION_NAME task lands (§15) - empty = committed PlayerSettings value."),
					_versionOverride);

				using (new EditorGUILayout.HorizontalScope())
				{
					int current = CurrentBuildNumber();
					if (current >= 0)
					{
						EditorGUILayout.LabelField("Build number", current.ToString());
						if (GUILayout.Button("Bump", GUILayout.Width(60))) BumpBuildNumber();
					}
					else
					{
						EditorGUILayout.LabelField("Build number", "n/a (iOS / Android only)");
					}
				}

				using (new EditorGUI.DisabledScope(_target == TriggerTarget.Local))
				{
					_changeset = EditorGUILayout.TextField(
						new GUIContent("Changeset / ref", "Server-only: git SHA/branch or Plastic changeset (unitybuild.vcs.ref). Empty = default branch HEAD."),
						_changeset);
				}
			}
			SirenixEditorGUI.EndBox();
		}

		private void DrawTrigger()
		{
			SirenixEditorGUI.BeginBox("Trigger");
			{
				_target = (TriggerTarget)EditorGUILayout.EnumPopup(
					new GUIContent("Run location", "Filters which post-build actions apply; your enable/disable choices persist across the switch."),
					_target);

				DrawActionToggles();

				GUILayout.Space(4);
				using (new EditorGUILayout.HorizontalScope())
				{
					if (_target == TriggerTarget.Local)
					{
						if (GUILayout.Button("Build Local", GUILayout.Height(26))) BuildLocal();
					}
					else
					{
						if (GUILayout.Button("Build on Server", GUILayout.Height(26))) RunTrigger();
					}
				}

				if (!string.IsNullOrEmpty(_localStatus)) EditorGUILayout.HelpBox(_localStatus, MessageType.None);
			}
			SirenixEditorGUI.EndBox();
		}

		private void DrawActionToggles()
		{
			IReadOnlyList<PostBuildAction> actions = _definition.PostBuildActions;
			if (actions == null || actions.Count == 0)
			{
				EditorGUILayout.LabelField("Post-build actions", "none", EditorStyles.miniLabel);
				return;
			}

			RunLocation here = _target == TriggerTarget.Local ? RunLocation.Local : RunLocation.Remote;
			EditorGUILayout.LabelField("Post-build actions (uncheck to skip this run)", EditorStyles.miniLabel);

			foreach (PostBuildAction action in actions)
			{
				if (action == null) continue;

				bool applicable = action.RunLocation == RunLocation.Both || action.RunLocation == here;
				using (new EditorGUI.DisabledScope(!applicable))
				{
					bool enabled = !_actionEnabled.TryGetValue(action.Id, out bool stored) || stored;
					bool toggled = EditorGUILayout.ToggleLeft(
						"   " + action.DisplayName + "  (" + action.RunLocation + ")" + (applicable ? "" : "  - not for " + here),
						applicable && enabled);

					// Persist the choice only for applicable actions, so a local<->remote switch never clobbers it.
					if (applicable) _actionEnabled[action.Id] = toggled;
				}
			}
		}

		#endregion

		#region Configure Tab

		[TabGroup("Tabs", "Configure"), OnInspectorGUI, PropertyOrder(0)]
		private void DrawConfigureHeader()
		{
			SirenixEditorGUI.BeginBox("Post-build actions");
			{
				EditorGUILayout.LabelField(
					"Add an action (only those valid for a " + _definition.Platform.ToServerToken() +
					" definition are offered; a missing prerequisite is auto-inserted).", EditorStyles.wordWrappedMiniLabel);

				if (GUILayout.Button("+ Add Action")) ShowAddActionMenu();
			}
			SirenixEditorGUI.EndBox();
			GUILayout.Space(4);
		}

		private void ShowAddActionMenu()
		{
			GenericMenu menu = new GenericMenu();
			List<PostBuildAction> candidates = SupportedActions();

			if (candidates.Count == 0)
			{
				menu.AddDisabledItem(new GUIContent("(no actions support this definition)"));
			}

			foreach (PostBuildAction candidate in candidates)
			{
				PostBuildAction local = candidate;
				menu.AddItem(new GUIContent(local.DisplayName), false, () => AddAction(local.GetType()));
			}

			menu.ShowAsContext();
		}

		private void AddAction(Type type)
		{
			PostBuildAction action = (PostBuildAction)Activator.CreateInstance(type);
			HashSet<ArtifactKind> available = ComputeAvailable();

			if (!action.CanConsume(available))
			{
				Type prerequisite = SupportedActions().FirstOrDefault(a => a.Produces == action.Consumes)?.GetType();
				if (prerequisite != null)
				{
					bool addBoth = EditorUtility.DisplayDialog(
						"Missing prerequisite",
						action.DisplayName + " needs " + action.Consumes + ", which nothing in the chain produces yet.\n\n" +
						"Add " + DisplayNameOf(prerequisite) + " first?",
						"Add both", "Cancel");
					if (!addBoth) return;

					AppendAction((PostBuildAction)Activator.CreateInstance(prerequisite));
				}
				else
				{
					bool addAnyway = EditorUtility.DisplayDialog(
						"Invalid chain",
						action.DisplayName + " needs " + action.Consumes + " but no available action produces it.\n\n" +
						"Add anyway? (the build will fail validation until the chain is fixed.)",
						"Add anyway", "Cancel");
					if (!addAnyway) return;
				}
			}

			AppendAction(action);
		}

		private void AppendAction(PostBuildAction action)
		{
			List<PostBuildAction> list = ActionsList();
			if (list == null) return;

			Undo.RecordObject(_definition, "Add post-build action");
			action.EnsureId();
			list.Add(action);
			EditorUtility.SetDirty(_definition);
			AssetDatabase.SaveAssetIfDirty(_definition);
		}

		#endregion

		#region Async Loads

		private async Task LoadBuildsAsync(BuildPanel owner)
		{
			List<BuildRow> rows = new List<BuildRow>();

			// Server builds (filtered by game + definition's executor) - live rows first.
			if (!string.IsNullOrEmpty(owner.Token))
			{
				if (owner.Executors == null || owner.Executors.Count == 0) await owner.DiscoverExecutorsAsync();

				string executor = owner.ResolveExecutor(_definition);
				string game = owner.Project != null ? owner.Project.GameToken : null;
				if (!string.IsNullOrEmpty(executor))
				{
					using (TeamCityClient client = owner.NewClient())
					{
						List<BuildStatus> builds = await client.ListBuildsAsync(executor, game, 10);
						foreach (BuildStatus build in builds)
						{
							if (build.Definition != null && build.Definition != _definition.DefinitionName) continue;

							rows.Add(new BuildRow
							{
								Title = "#" + build.Number,
								Detail = DescribeState(build),
								OnServer = true,
								ServerId = build.Id,
								WebUrl = build.WebUrl,
								Live = build.IsQueued || build.IsRunning
							});
						}
					}
				}
			}

			// On-disk builds (Builds/<definition>/<version>[_<buildNumber>]/).
			string dir = Path.Combine(BuildPanel.ProjectRoot, "Builds", _definition.DefinitionName);
			if (Directory.Exists(dir))
			{
				foreach (string sub in Directory.GetDirectories(dir))
				{
					rows.Add(new BuildRow
					{
						Title = Path.GetFileName(sub),
						Detail = "on disk",
						LocalPresent = true,
						LocalPath = sub
					});
				}
			}

			_builds = rows;
		}

		private void DownloadBuild(BuildRow row)
		{
			_owner.RunAsync(DownloadBuildAsync(row));
		}

		private async Task DownloadBuildAsync(BuildRow row)
		{
			using (TeamCityClient client = _owner.NewClient())
			{
				List<ArtifactFile> files = await client.ListArtifactsAsync(row.ServerId);
				string destDir = Path.Combine(BuildPanel.ProjectRoot, "Builds", _definition.DefinitionName, "server_" + row.ServerId);
				foreach (ArtifactFile file in files)
				{
					await client.DownloadArtifactAsync(row.ServerId, file.Name, Path.Combine(destDir, file.Name));
				}

				_owner.SetStatus("Downloaded " + files.Count + " artifact(s) -> " + destDir);
				Refresh(_owner);
			}
		}

		private void RunTrigger()
		{
			_owner.RunAsync(TriggerAsync());
		}

		private async Task TriggerAsync()
		{
			if (string.IsNullOrEmpty(_owner.Token))
			{
				_owner.SetStatus("Set an access token in Settings before triggering a server build.");
				return;
			}

			if (_owner.Executors == null || _owner.Executors.Count == 0) await _owner.DiscoverExecutorsAsync();

			string executor = _owner.ResolveExecutor(_definition);
			if (string.IsNullOrEmpty(executor))
			{
				_owner.SetStatus("No executor for platform '" + _definition.Platform.ToServerToken() + "'.");
				return;
			}

			Dictionary<string, string> properties = new Dictionary<string, string>
			{
				{ "unitybuild.game", _owner.Project != null ? _owner.Project.GameToken : "" },
				{ "unitybuild.definition", _definition.DefinitionName }
			};

#if UNITY_6000_0_OR_NEWER
			if (_definition.Profile != null) properties["unitybuild.buildProfile"] = AssetDatabase.GetAssetPath(_definition.Profile);
#endif
			if (!string.IsNullOrEmpty(_versionOverride)) properties["unitybuild.version.name"] = _versionOverride;
			if (!string.IsNullOrEmpty(_changeset))
			{
				properties["unitybuild.vcs.ref"] = _changeset;
				properties["unitybuild.vcs.refType"] = _owner.Project != null && _owner.Project.Vcs == VcsKind.Plastic ? "changeset" : "commit";
			}

			string skip = SkipSet();
			if (!string.IsNullOrEmpty(skip)) properties["unitybuild.actions.skip"] = skip;

			using (TeamCityClient client = _owner.NewClient())
			{
				long id = await client.TriggerBuildAsync(executor, properties);
				_owner.SetStatus("Queued build " + id + " for '" + _definition.DefinitionName + "'.");
				await LoadBuildsAsync(_owner);
			}
		}

		private void BuildLocal()
		{
			string skip = SkipSet();
			string previous = Environment.GetEnvironmentVariable("BUILD_ACTIONS_SKIP");
			try
			{
				Environment.SetEnvironmentVariable("BUILD_ACTIONS_SKIP", skip);
				BuildResult result = BuildRunner.RunDefinition(_definition);
				_localStatus = (result.Success ? "Local build OK: " + result.ArtifactPath : "Local build FAILED: " + result.Error);
			}
			catch (Exception exception)
			{
				_localStatus = "Local build threw: " + exception.Message;
			}
			finally
			{
				Environment.SetEnvironmentVariable("BUILD_ACTIONS_SKIP", previous);
				Refresh(_owner);
			}
		}

		#endregion

		#region Helpers

		private string SkipSet()
		{
			List<string> disabled = new List<string>();
			foreach (KeyValuePair<string, bool> pair in _actionEnabled)
			{
				if (!pair.Value && !string.IsNullOrEmpty(pair.Key)) disabled.Add(pair.Key);
			}

			return string.Join(",", disabled);
		}

		private HashSet<ArtifactKind> ComputeAvailable()
		{
			HashSet<ArtifactKind> available = new HashSet<ArtifactKind> { _definition.OutputKind };
			foreach (PostBuildAction action in _definition.PostBuildActions)
			{
				if (action != null && action.Produces != ArtifactKind.None) available.Add(action.Produces);
			}

			return available;
		}

		private List<PostBuildAction> SupportedActions()
		{
			List<PostBuildAction> result = new List<PostBuildAction>();
			foreach (Type type in TypeCache.GetTypesDerivedFrom<PostBuildAction>())
			{
				if (type.IsAbstract || type.IsGenericTypeDefinition) continue;

				PostBuildAction instance;
				try
				{
					instance = (PostBuildAction)Activator.CreateInstance(type);
				}
				catch (Exception)
				{
					continue;
				}

				if (instance.Supports(_definition)) result.Add(instance);
			}

			result.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
			return result;
		}

		private static string DisplayNameOf(Type type)
		{
			try
			{
				return ((PostBuildAction)Activator.CreateInstance(type)).DisplayName;
			}
			catch (Exception)
			{
				return type.Name;
			}
		}

		private List<PostBuildAction> ActionsList()
		{
			return ActionsField != null ? ActionsField.GetValue(_definition) as List<PostBuildAction> : null;
		}

		private int CurrentBuildNumber()
		{
			switch (_definition.Platform)
			{
				case BuildPlatform.Android:
					return PlayerSettings.Android.bundleVersionCode;
				case BuildPlatform.iOS:
					return int.TryParse(PlayerSettings.iOS.buildNumber, out int value) ? value : 0;
				default:
					return -1;
			}
		}

		private void BumpBuildNumber()
		{
			switch (_definition.Platform)
			{
				case BuildPlatform.Android:
					PlayerSettings.Android.bundleVersionCode += 1;
					break;
				case BuildPlatform.iOS:
					int current = int.TryParse(PlayerSettings.iOS.buildNumber, out int value) ? value : 0;
					PlayerSettings.iOS.buildNumber = (current + 1).ToString();
					break;
				default:
					return;
			}

			_localStatus = "Bumped build number to " + CurrentBuildNumber() + " - commit + push the PlayerSettings change to make it a server build number.";
		}

		private static string DescribeState(BuildStatus build)
		{
			if (build.IsRunning) return "running " + build.PercentageComplete + "%  " + build.StatusText;
			if (build.IsQueued) return "queued #" + build.Number;
			return build.Status + "  " + build.StatusText;
		}

		#endregion

		#region Nested Types

		private enum TriggerTarget
		{
			Local,
			Server
		}

		#endregion
	}
}

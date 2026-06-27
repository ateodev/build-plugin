using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The in-Editor control plane (L3): lists the project's build definitions, builds them locally through
	/// the same <see cref="BuildRunner"/> code path as CI, or triggers them on the build server via
	/// <see cref="TeamCityClient"/>, and shows recent server builds with artifact download. Thin REST client
	/// over the user's own token - no admin access, no server-side coupling.
	/// </summary>
	public sealed class BuildPanel : EditorWindow
	{
		#region Fields

		private BuildDefinition[] _definitions = Array.Empty<BuildDefinition>();
		private ProjectConfig _project;
		private string _baseUrl = "";
		private string _status = "";
		private Vector2 _scroll;
		private bool _busy;

		private List<BuildStatus> _builds = new List<BuildStatus>();
		private readonly Dictionary<long, List<ArtifactFile>> _artifacts = new Dictionary<long, List<ArtifactFile>>();
		private Dictionary<string, string> _executors = new Dictionary<string, string>();

		#endregion

		#region MenuItem

		[MenuItem("Window/Build Panel")]
		private static void Open()
		{
			GetWindow<BuildPanel>("Build Panel").Show();
		}

		#endregion

		#region Unity

		private void OnEnable()
		{
			Reload();
		}

		private void OnGUI()
		{
			_scroll = EditorGUILayout.BeginScrollView(_scroll);

			DrawServer();
			EditorGUILayout.Space();
			DrawDefinitions();
			EditorGUILayout.Space();
			DrawBuilds();

			if (!string.IsNullOrEmpty(_status))
			{
				EditorGUILayout.Space();
				EditorGUILayout.HelpBox(_status, MessageType.None);
			}

			EditorGUILayout.EndScrollView();
		}

		#endregion

		#region GUI Sections

		private void DrawServer()
		{
			EditorGUILayout.LabelField("Build Server", EditorStyles.boldLabel);
			using (new EditorGUI.DisabledScope(_busy))
			{
				_baseUrl = EditorGUILayout.TextField("Server URL", _baseUrl);
				BuildServerSettings.Token = EditorGUILayout.PasswordField("Access Token", BuildServerSettings.Token);
				BuildServerSettings.BuildTypeId = EditorGUILayout.TextField("Executor (history / fallback)", BuildServerSettings.BuildTypeId);
				if (_executors.Count > 0) EditorGUILayout.LabelField(" ", "Auto-discovered: " + string.Join(", ", _executors.Keys), EditorStyles.miniLabel);

				using (new EditorGUILayout.HorizontalScope())
				{
					if (GUILayout.Button("Test Connection")) RunAsync(TestConnectionAsync());
					if (GUILayout.Button("Refresh Builds")) RunAsync(RefreshBuildsAsync());
					if (GUILayout.Button("Reload Definitions")) Reload();
				}
			}
		}

		private void DrawDefinitions()
		{
			EditorGUILayout.LabelField("Definitions (" + _definitions.Length + ")", EditorStyles.boldLabel);
			if (_definitions.Length == 0)
			{
				EditorGUILayout.HelpBox("No BuildDefinition assets found. Create one via Create > Build > Build Definition.", MessageType.Info);
				return;
			}

			foreach (BuildDefinition definition in _definitions)
			{
				using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
				{
					EditorGUILayout.LabelField(definition.DefinitionName + "  (" + definition.Platform + ")");
					using (new EditorGUI.DisabledScope(_busy))
					{
						if (GUILayout.Button("Build Local", GUILayout.Width(90))) BuildLocal(definition);
						if (GUILayout.Button("Build on Server", GUILayout.Width(120))) RunAsync(TriggerAsync(definition));
					}
				}
			}
		}

		private void DrawBuilds()
		{
			EditorGUILayout.LabelField("Recent Server Builds", EditorStyles.boldLabel);
			foreach (BuildStatus build in _builds)
			{
				using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
				{
					using (new EditorGUILayout.HorizontalScope())
					{
						EditorGUILayout.LabelField("#" + build.Number + "  " + DescribeState(build));
						if (!string.IsNullOrEmpty(build.WebUrl) && GUILayout.Button("Open", GUILayout.Width(60))) Application.OpenURL(build.WebUrl);
						using (new EditorGUI.DisabledScope(_busy))
						{
							if (build.IsFinished && GUILayout.Button("Artifacts", GUILayout.Width(80))) RunAsync(LoadArtifactsAsync(build.Id));
						}
					}

					DrawArtifacts(build.Id);
				}
			}
		}

		private void DrawArtifacts(long buildId)
		{
			if (!_artifacts.TryGetValue(buildId, out List<ArtifactFile> files)) return;

			foreach (ArtifactFile file in files)
			{
				using (new EditorGUILayout.HorizontalScope())
				{
					EditorGUILayout.LabelField("   " + file.Name + "  (" + (file.Size / 1048576.0).ToString("F1") + " MiB)");
					using (new EditorGUI.DisabledScope(_busy))
					{
						if (GUILayout.Button("Download", GUILayout.Width(90))) RunAsync(DownloadAsync(buildId, file.Name));
					}
				}
			}
		}

		#endregion

		#region Actions

		private void Reload()
		{
			List<BuildDefinition> definitions = new List<BuildDefinition>();
			foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BuildDefinition)))
			{
				BuildDefinition definition = AssetDatabase.LoadAssetAtPath<BuildDefinition>(AssetDatabase.GUIDToAssetPath(guid));
				if (definition != null) definitions.Add(definition);
			}

			_definitions = definitions.ToArray();

			string[] projectGuids = AssetDatabase.FindAssets("t:" + nameof(ProjectConfig));
			_project = projectGuids.Length > 0
				? AssetDatabase.LoadAssetAtPath<ProjectConfig>(AssetDatabase.GUIDToAssetPath(projectGuids[0]))
				: null;

			if (string.IsNullOrEmpty(_baseUrl)) _baseUrl = _project != null ? _project.ServerBaseUrl : "https://build.ateonet.work";
		}

		private void BuildLocal(BuildDefinition definition)
		{
			try
			{
				BuildResult result = BuildRunner.RunDefinition(definition);
				_status = (result.Success ? "Local build OK: " : "Local build FAILED: ") +
					(result.Success ? result.ArtifactPath : result.Error);
			}
			catch (Exception exception)
			{
				_status = "Local build threw: " + exception.Message;
			}
		}

		private async Task TestConnectionAsync()
		{
			using (TeamCityClient client = NewClient())
			{
				_executors = await client.DiscoverExecutorsAsync();
				string map = _executors.Count == 0 ? "(none)" : string.Join(", ", _executors.Select(pair => pair.Key + "->" + pair.Value));
				_status = "Connected. Discovered " + _executors.Count + " executor(s): " + map;
			}
		}

		private async Task TriggerAsync(BuildDefinition definition)
		{
			string buildTypeId = ResolveExecutor(definition);
			if (string.IsNullOrEmpty(buildTypeId))
			{
				_status = "No executor for platform '" + definition.Platform.ToServerToken() + "'. Test Connection to discover, or set a fallback.";
				return;
			}

			Dictionary<string, string> properties = new Dictionary<string, string>
			{
				{ "unitybuild.game", _project != null ? _project.GameToken : "" },
				{ "unitybuild.definition", definition.DefinitionName },
				{ "unitybuild.platform", definition.Platform.ToServerToken() }
			};
#if UNITY_6000_0_OR_NEWER
			if (definition.Profile != null) properties["unitybuild.buildProfile"] = AssetDatabase.GetAssetPath(definition.Profile);
#endif

			using (TeamCityClient client = NewClient())
			{
				long id = await client.TriggerBuildAsync(buildTypeId, properties);
				_status = "Queued build " + id + " on '" + buildTypeId + "' for '" + definition.DefinitionName + "'.";
				await RefreshBuildsAsync();
			}
		}

		private string ResolveExecutor(BuildDefinition definition)
		{
			string token = definition.Platform.ToServerToken();
			if (_executors.TryGetValue(token, out string id) && !string.IsNullOrEmpty(id)) return id;
			return BuildServerSettings.BuildTypeId;
		}

		private async Task RefreshBuildsAsync()
		{
			using (TeamCityClient client = NewClient())
			{
				_builds = await client.ListBuildsAsync(BuildServerSettings.BuildTypeId, null, 20);
				_status = "Loaded " + _builds.Count + " build(s).";
			}
		}

		private async Task LoadArtifactsAsync(long buildId)
		{
			using (TeamCityClient client = NewClient())
			{
				_artifacts[buildId] = await client.ListArtifactsAsync(buildId);
				_status = _artifacts[buildId].Count + " artifact(s) on build " + buildId + ".";
			}
		}

		private async Task DownloadAsync(long buildId, string artifactName)
		{
			string destination = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds", "Downloaded", artifactName);
			using (TeamCityClient client = NewClient())
			{
				await client.DownloadArtifactAsync(buildId, artifactName, destination);
				_status = "Downloaded -> " + destination;
			}
		}

		#endregion

		#region Helpers

		private TeamCityClient NewClient()
		{
			return new TeamCityClient(_baseUrl, BuildServerSettings.Token);
		}

		private async void RunAsync(Task task)
		{
			_busy = true;
			Repaint();
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
				_busy = false;
				Repaint();
			}
		}

		private static string DescribeState(BuildStatus build)
		{
			if (build.IsRunning) return "running " + build.PercentageComplete + "%  " + build.StatusText;
			if (build.IsQueued) return "queued";
			return build.Status + "  " + build.StatusText;
		}

		#endregion
	}
}

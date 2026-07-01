using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
		private string _buildName = "";

		private List<BuildRow> _builds = new List<BuildRow>();
		private bool _loading;
		private string _localStatus = "";

		private static GUIStyle _rightMini;
		private static GUIStyle RightMini => _rightMini ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

		#endregion

		#region Constructor

		public DefinitionView(BuildDefinition definition, BuildPanel owner)
		{
			_definition = definition;
			_owner = owner;
			_buildName = EditorPrefs.GetString(BuildNamePrefKey, ""); // per-definition, machine-local, never committed
		}

		// Build-name field persists its last value per definition in EditorPrefs (a per-trigger convenience), so
		// bumping "-4" -> "-5" survives reselect/reload but never touches the committed asset.
		private string BuildNamePrefKey => "Ateo.Build.BuildName." + _definition.DefinitionName;

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

				foreach (BuildRow row in _builds) DrawBuildRow(row);
			}
			SirenixEditorGUI.EndBox();
		}

		/// <summary>
		/// One build row, laid out by explicit rects so it never forces a content-pane minimum width: the action
		/// buttons are anchored to the right edge and the title/detail take the remaining space (truncating when
		/// narrow) instead of pushing the buttons off-screen. A downloaded server build is a single row (server +
		/// local), so its Download is replaced by "Go to folder".
		/// </summary>
		private void DrawBuildRow(BuildRow row)
		{
			const float pad = 4f;
			Rect rect = EditorGUILayout.GetControlRect(false, 24f);

			if (Event.current.type == EventType.Repaint)
			{
				EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), new Color(0f, 0f, 0f, 0.2f));
			}

			float btnH = 20f;
			float btnY = rect.y + (rect.height - btnH) * 0.5f;
			float right = rect.xMax - pad;

			// Right-anchored actions, drawn right-to-left.
			if (row.LocalPresent && !string.IsNullOrEmpty(row.LocalPath))
			{
				right = DrawRowButton(ref right, btnY, btnH, 96f, new GUIContent("Go to folder", row.LocalPath),
					() => EditorUtility.RevealInFinder(row.LocalPath));
			}
			else if (row.OnServer && row.ServerId > 0)
			{
				right = DrawRowButton(ref right, btnY, btnH, 84f, new GUIContent("Download", "Download this build's artifacts"),
					() => DownloadBuild(row));
			}

			if (row.OnServer && !string.IsNullOrEmpty(row.WebUrl))
			{
				right = DrawRowButton(ref right, btnY, btnH, 116f, new GUIContent("Open in TeamCity", "Open this build in the TeamCity web UI"),
					() => Application.OpenURL(_owner.ResolveServerLink(row.WebUrl)));
			}

			// Source tag (server · local), right-aligned in the space left of the buttons.
			string source = row.OnServer && row.LocalPresent ? "server · local" : row.OnServer ? "server" : "local";
			GUIContent sourceContent = new GUIContent(source);
			float sourceW = EditorStyles.miniLabel.CalcSize(sourceContent).x + 6f;
			GUI.Label(new Rect(right - sourceW, rect.y, sourceW, rect.height), sourceContent, RightMini);
			right = right - sourceW - pad;

			// Title (build identity) + detail fill what remains (truncating before the buttons). The identity can be
			// long (1.0-test-locomotion-4), so give it a wider column and a tooltip with the full string.
			float titleW = Mathf.Min(160f, Mathf.Max(56f, (right - rect.x) * 0.55f));
			Rect titleRect = new Rect(rect.x + pad, rect.y, titleW, rect.height);
			GUI.Label(titleRect, new GUIContent((row.Live ? "● " : "") + row.Title, row.Title),
				row.Live ? EditorStyles.boldLabel : EditorStyles.label);

			float detailW = Mathf.Max(0f, right - titleRect.xMax - pad);
			GUI.Label(new Rect(titleRect.xMax + pad, rect.y, detailW, rect.height),
				new GUIContent(row.Detail, row.Detail), EditorStyles.miniLabel);
		}

		private static float DrawRowButton(ref float right, float y, float height, float width, GUIContent content, Action onClick)
		{
			Rect r = new Rect(right - width, y, width, height);
			if (GUI.Button(r, content)) onClick();
			return r.x - 4f;
		}

		private void DrawOverrides()
		{
			SirenixEditorGUI.BeginBox("Overrides");
			{
				_versionOverride = EditorGUILayout.TextField(
					new GUIContent("Version name", "Override marketing version (unitybuild.version.name). " +
						"No-op until the BUILD_VERSION_NAME task lands (§15) - empty = committed PlayerSettings value."),
					_versionOverride);

				string newBuildName = EditorGUILayout.TextField(
					new GUIContent("Build name (optional)", "Free-text label appended to this build's on-disk identity " +
						"(e.g. 1.0-test-locomotion-4), so you can take many builds at the same version without overwriting. " +
						"Applies to both local and server builds. Sanitized to filesystem-safe characters (whitespace -> '-')."),
					_buildName);
				if (newBuildName != _buildName)
				{
					_buildName = newBuildName;
					EditorPrefs.SetString(BuildNamePrefKey, _buildName ?? "");
				}

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
						new GUIContent("Changeset / ref", "Server-only: git SHA/branch or Plastic changeset (unitybuild.vcs.ref). " +
							"Empty = the definition's Default Branch (or, if that is empty too, the repo's default branch)."),
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

			// Server builds keyed by their §12.2 identity folder, so a local copy at the same path folds in
			// (one row per build) instead of appearing twice.
			Dictionary<string, BuildRow> byFolder = new Dictionary<string, BuildRow>(StringComparer.OrdinalIgnoreCase);

			if (!string.IsNullOrEmpty(owner.Token))
			{
				if (owner.Executors == null || owner.Executors.Count == 0) await owner.DiscoverExecutorsAsync();

				string executor = owner.ResolveExecutor(_definition);
				string projectKey = owner.Project != null ? owner.Project.ProjectKey : null;
				if (!string.IsNullOrEmpty(executor))
				{
					using (TeamCityClient client = owner.NewClient())
					{
						List<BuildStatus> builds = await client.ListBuildsAsync(executor, projectKey, _definition.DefinitionName, 10);
						foreach (BuildStatus build in builds)
						{
							BuildRow row = new BuildRow
							{
								Title = IdentityLabel(build),
								Detail = DescribeState(build),
								OnServer = true,
								ServerId = build.Id,
								WebUrl = build.WebUrl,
								Live = build.IsQueued || build.IsRunning,
								Number = build.Number,
								VersionName = build.VersionName,
								VersionCode = build.VersionCode,
								BuildName = build.BuildName
							};
							rows.Add(row);
							byFolder[FolderNameFor(row)] = row;
						}
					}
				}
			}

			// On-disk builds (Builds/<definition>/<version>[_<buildNumber>]/, or a "b<number>" fallback folder for
			// pre-identity downloads). A folder that matches a server build's identity folds into that row.
			string dir = BuildLayout.DefinitionDirectory(BuildPanel.ProjectRoot, _definition);
			if (Directory.Exists(dir))
			{
				foreach (string sub in Directory.GetDirectories(dir))
				{
					string name = Path.GetFileName(sub);
					if (byFolder.TryGetValue(name, out BuildRow existing))
					{
						existing.LocalPresent = true;
						existing.LocalPath = sub;
						continue;
					}

					rows.Add(new BuildRow
					{
						Title = name,
						Detail = "on disk",
						LocalPresent = true,
						LocalPath = sub
					});
				}
			}

			_builds = rows;
		}

		/// <summary>
		/// The §12.2 identity folder name for a build row: <c>&lt;version&gt;[_&lt;code&gt;][-&lt;buildName&gt;]</c>
		/// when the server recorded its identity, else a <c>b&lt;number&gt;</c> fallback for builds that predate
		/// identity recording. Used for both the download destination and the local/server correlation key.
		/// </summary>
		private string FolderNameFor(BuildRow row)
		{
			return !string.IsNullOrEmpty(row.VersionName)
				? BuildLayout.FolderName(_definition, row.VersionName, row.VersionCode, row.BuildName)
				: "b" + row.Number;
		}

		/// <summary>
		/// The list label for a server build: its identity (<c>1.0</c> / <c>1.0-test-locomotion-4</c>) when recorded,
		/// else the TeamCity <c>#number</c> for builds that never recorded one (failed-early / pre-identity).
		/// </summary>
		private string IdentityLabel(BuildStatus build)
		{
			return !string.IsNullOrEmpty(build.VersionName)
				? BuildLayout.FolderName(_definition, build.VersionName, build.VersionCode, build.BuildName)
				: "#" + build.Number;
		}

		private void DownloadBuild(BuildRow row)
		{
			_owner.RunAsync(DownloadBuildAsync(row));
		}

		private async Task DownloadBuildAsync(BuildRow row)
		{
			string destDir = Path.Combine(BuildLayout.DefinitionDirectory(BuildPanel.ProjectRoot, _definition), FolderNameFor(row));

			using (TeamCityClient client = _owner.NewClient())
			{
				List<ArtifactFile> files = await client.ListArtifactsAsync(row.ServerId);
				Directory.CreateDirectory(destDir);

				foreach (ArtifactFile file in files)
				{
					string local = Path.Combine(destDir, file.Name);
					await client.DownloadArtifactAsync(row.ServerId, file.Name, local);

					// "Download = fetch + unarchive" (§12.2): expand the build archive in place so a downloaded
					// build is byte-identical on disk to a locally-produced one, then drop the archive.
					if (file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
					{
						ExtractZip(local, destDir);
						File.Delete(local);
					}
				}

				// Label by identity (the list row's label), not the TeamCity #number - users think in versions.
				_owner.SetStatus("Downloaded build " + row.Title + " -> " + destDir);
				Refresh(_owner);
			}
		}

		/// <summary>Extract a zip into <paramref name="destDir"/>, overwriting, with a zip-slip guard.</summary>
		private static void ExtractZip(string zipPath, string destDir)
		{
			string root = Path.GetFullPath(destDir);
			using (ZipArchive archive = ZipFile.OpenRead(zipPath))
			{
				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
					if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard

					if (string.IsNullOrEmpty(entry.Name))
					{
						Directory.CreateDirectory(target); // directory entry
						continue;
					}

					Directory.CreateDirectory(Path.GetDirectoryName(target));
					entry.ExtractToFile(target, true);
				}
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
				{ "unitybuild.project", _owner.Project != null ? _owner.Project.ProjectKey : "" },
				{ "unitybuild.definition", _definition.DefinitionName },
				// Per-build target platform token: a capability executor builds many platforms, so the checkout
				// dir (<team>/<project>/<target>) and history need the platform for THIS build, not the executor's.
				{ "unitybuild.target", _definition.Platform.ToServerToken() }
			};

#if UNITY_6000_0_OR_NEWER
			if (_definition.Profile != null) properties["unitybuild.buildProfile"] = AssetDatabase.GetAssetPath(_definition.Profile);
#endif
			if (!string.IsNullOrEmpty(_versionOverride)) properties["unitybuild.version.name"] = _versionOverride;
			if (!string.IsNullOrEmpty(_buildName)) properties["unitybuild.buildName"] = _buildName;
			if (!string.IsNullOrEmpty(_changeset))
			{
				properties["unitybuild.vcs.ref"] = _changeset;
				properties["unitybuild.vcs.refType"] = _owner.Project != null && _owner.Project.Vcs == VcsKind.Plastic ? "changeset" : "commit";
			}
			else
			{
				// No explicit override: build the definition's default branch. Normalized because the agent
				// resolves BARE branch names to origin/<branch> itself - an "origin/"-prefixed value is the
				// legacy workaround form some committed assets still carry (they are not rewritten). Empty
				// after normalization = send nothing, the agent resolves the repo's default branch.
				string branch = VcsBranch.Normalize(_definition.DefaultBranch);
				if (!string.IsNullOrEmpty(branch))
				{
					properties["unitybuild.vcs.ref"] = branch;
					properties["unitybuild.vcs.refType"] = "branch";
				}
			}

			string skip = SkipSet();
			if (!string.IsNullOrEmpty(skip)) properties["unitybuild.actions.skip"] = skip;

			using (TeamCityClient client = _owner.NewClient())
			{
				// §5.6 idempotency: an impatient re-click of the identical trigger (same executor, project,
				// definition and ref) must not stack a second queue entry.
				properties.TryGetValue("unitybuild.vcs.ref", out string vcsRef);
				BuildStatus duplicate = await client.FindInFlightDuplicateAsync(
					executor, properties["unitybuild.project"], _definition.DefinitionName, vcsRef);
				if (duplicate != null)
				{
					string label = !string.IsNullOrEmpty(duplicate.Number) ? "#" + duplicate.Number : "id " + duplicate.Id;
					_owner.SetStatus("Already " + (duplicate.IsRunning ? "running" : "queued") + " as " + label +
						" - trigger skipped.");
					await LoadBuildsAsync(_owner);
					return;
				}

				long id = await client.TriggerBuildAsync(executor, properties);
				_owner.SetStatus("Queued build " + id + " for '" + _definition.DefinitionName + "'.");
				await LoadBuildsAsync(_owner);
			}
		}

		private void BuildLocal()
		{
			string skip = SkipSet();
			string previousSkip = Environment.GetEnvironmentVariable("BUILD_ACTIONS_SKIP");
			string previousName = Environment.GetEnvironmentVariable("BUILD_NAME");
			try
			{
				Environment.SetEnvironmentVariable("BUILD_ACTIONS_SKIP", skip);
				Environment.SetEnvironmentVariable("BUILD_NAME", _buildName); // BuildRunner reads this into the §12.2 folder
				BuildResult result = BuildRunner.RunDefinition(_definition);
				_localStatus = (result.Success ? "Local build OK: " + result.ArtifactPath : "Local build FAILED: " + result.Error);
			}
			catch (Exception exception)
			{
				_localStatus = "Local build threw: " + exception.Message;
			}
			finally
			{
				Environment.SetEnvironmentVariable("BUILD_ACTIONS_SKIP", previousSkip);
				Environment.SetEnvironmentVariable("BUILD_NAME", previousName);
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
			// StatusText is the human-readable form ("Success" / "Tests failed: ..."); Status is the enum token
			// ("SUCCESS") - showing both gave "SUCCESS  Success". Prefer the readable one, fall back to the token.
			if (!string.IsNullOrEmpty(build.StatusText)) return build.StatusText;
			// A canceled build (visible since the history includes state:any) reports UNKNOWN and often no
			// statusText - show something a user can read instead of a bare token or an empty cell.
			if (string.IsNullOrEmpty(build.Status) || build.Status == "UNKNOWN") return "canceled";

			return build.Status;
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

	/// <summary>
	/// Canonical form of a definition's default branch (and of any branch sent as <c>unitybuild.vcs.ref</c>):
	/// a BARE branch name ("main"); empty = the repo's default branch, resolved agent-side. The agent resolves
	/// bare names to <c>origin/&lt;branch&gt;</c> itself, so an "origin/"-prefixed value - the pre-canonical
	/// workaround some committed definition assets still carry - is stripped at the consumption points
	/// (trigger send, wizard write) instead of rewriting those assets.
	/// </summary>
	internal static class VcsBranch
	{
		private const string LegacyRemotePrefix = "origin/";

		public static string Normalize(string branch)
		{
			string trimmed = branch != null ? branch.Trim() : "";
			return trimmed.StartsWith(LegacyRemotePrefix, StringComparison.Ordinal)
				? trimmed.Substring(LegacyRemotePrefix.Length)
				: trimmed;
		}
	}
}

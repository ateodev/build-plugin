using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ateo.Build
{
	/// <summary>
	/// The single generic headless entry point (L2). CI always calls the SAME method regardless of game or
	/// platform:
	///   Unity ... -executeMethod Ateo.Build.BuildRunner.Run -buildDefinition "Android-AAB-Release"
	///             [-buildResult "&lt;path&gt;.json"]
	/// It loads the named <see cref="BuildDefinition"/> from the project, applies it (target, defines, version
	/// stamp, signing), runs pre-steps, builds (built-in or via a game's own method shim), runs post-steps,
	/// writes a machine-readable <see cref="BuildResult"/>, and sets the process exit code. The exact same
	/// path runs for an in-editor local build via <see cref="RunDefinition"/> - that local/CI parity is the
	/// whole point.
	/// </summary>
	public static class BuildRunner
	{
		#region Public Methods

		/// <summary>CI entry. Reads -buildDefinition / -buildResult from the command line.</summary>
		public static void Run()
		{
			string definitionName = GetArg("-buildDefinition");
			string resultPath = GetArg("-buildResult");
			BuildResult result;

			try
			{
				if (string.IsNullOrEmpty(definitionName)) throw new Exception("Missing -buildDefinition <name> argument.");

				BuildDefinition definition = FindDefinition(definitionName);
				if (definition == null)
				{
					throw new Exception("Build definition '" + definitionName + "' not found. Expected a BuildDefinition " +
						"asset whose name == '" + definitionName + "' (usually under Assets/BuildConfigs/).");
				}

				result = RunDefinition(definition);
			}
			catch (Exception exception)
			{
				result = BuildResult.Failed(definitionName, exception.ToString());
				Debug.LogError("[Build] FAILED: " + exception);
			}

			if (!string.IsNullOrEmpty(resultPath)) result.WriteJson(resultPath);

			if (Application.isBatchMode) EditorApplication.Exit(result.Success ? 0 : 1);
		}

		/// <summary>
		/// Build a definition through the full pipeline. Shared by CI (<see cref="Run"/>) and local builds
		/// (the Editor panel in a later version), so both go through identical logic.
		/// </summary>
		public static BuildResult RunDefinition(BuildDefinition definition)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			BuildContext context = new BuildContext
			{
				Definition = definition,
				Project = FindProjectConfig(),
				IsBatchMode = Application.isBatchMode
			};

			Debug.Log("[Build] definition='" + definition.DefinitionName + "' platform=" + definition.Platform +
				(definition.UsesGameBuilder ? " method='" + definition.BuildMethod + "'" : " (built-in)"));

			// Snapshot the settings the build mutates and restore them on the way out, so the no-clean
			// checkout stays pristine (target/flavor/version changes never persist past the build).
			using (new ProjectSettingsScope(definition.Platform.ToBuildTargetGroup()))
			{
				// 1. Build target. In BATCH MODE Unity cannot recompile mid-script, so the target must already
				//    be set (CI launches Unity with -buildTarget / -activeBuildProfile); we validate, not switch.
				EnsureBuildTarget(definition, context);

				// 2. Scripting defines (built-in no-profile path only; profiles own their own defines).
				ApplyDefines(definition, context);

				// 3. Version stamp (incl. BUILD_VERSION_NAME / ANDROID_VERSION_CODE / IOS_BUILD_NUMBER).
				VersionStamp.Apply(context);

				// 4. Android signing from env references (passwords resolved agent-side, never in the asset).
				ApplyAndroidSigning(definition, context);

				// 5. Resolve output path.
				context.OutputPath = ResolveOutputPath(definition, context);

				// 6. Pre-build steps.
				foreach (BuildStep step in definition.PreSteps)
				{
					if (step != null) step.OnPreBuild(context);
				}

				// 7. Build - via the game's own method (migration shim) or the built-in pipeline.
				BuildResult result;
				try
				{
					result = definition.UsesGameBuilder ? RunGameBuilder(definition, context) : RunBuiltIn(definition, context);
				}
				catch (Exception exception)
				{
					result = BuildResult.Failed(definition.DefinitionName, exception.ToString());
					Debug.LogError("[Build] build step threw: " + exception);
				}

				// 8. Post-build action pipeline (v2 typed contract, §10) - runs the definition's ordered
				//    PostBuildActions on the just-built artifact BEFORE the result is finalized/written. Only
				//    when the build produced an artifact to act on; a pipeline failure fails the whole build.
				if (result.Success)
				{
					try
					{
						context.BuildFolder = string.IsNullOrEmpty(result.ArtifactPath)
							? null
							: Path.GetDirectoryName(result.ArtifactPath);
						context.ArtifactPaths = string.IsNullOrEmpty(result.ArtifactPath)
							? new List<string>()
							: new List<string> { result.ArtifactPath };
						context.Log = MakeProgressLog(context);

						ExecutePostBuildActions(context, definition, definition.PostBuildActions, definition.OutputKind, result);
					}
					catch (Exception exception)
					{
						result.Success = false;
						result.ArtifactPath = "";
						result.Error = exception.Message;
						Debug.LogError("[Build] post-build pipeline FAILED: " + exception.Message);
					}
				}

				// 9. Legacy post-build steps (always run, success or failure).
				foreach (BuildStep step in definition.PostSteps)
				{
					if (step == null) continue;

					try
					{
						step.OnPostBuild(context, result);
					}
					catch (Exception exception)
					{
						Debug.LogWarning("[Build] post-step '" + step.name + "' threw: " + exception.Message);
					}
				}

				result.DurationSeconds = stopwatch.ElapsedMilliseconds / 1000;
				Debug.Log("[Build] " + (result.Success ? "SUCCESS" : "FAILURE") + " in " + result.DurationSeconds +
					"s" + (result.Success ? " -> " + result.ArtifactPath : ""));
				return result;
			}
		}

		#endregion

		#region Private Methods - Build Paths

		private static BuildResult RunBuiltIn(BuildDefinition definition, BuildContext context)
		{
#if UNITY_6000_0_OR_NEWER
			// Unity 6 Build Profile is the preferred L0: target, scenes, defines and player-setting overrides
			// all come from the profile (which must already be active - see RunWithProfile).
			if (definition.Profile != null)
			{
				return RunWithProfile(definition, context);
			}
#endif

			// Legacy path: no profile, build from the definition's scene list for the active target.
			if (definition is AndroidBuildDefinition android)
			{
				EditorUserBuildSettings.buildAppBundle = android.Output == AndroidOutput.AAB;
			}

			BuildPlayerOptions options = new BuildPlayerOptions
			{
				scenes = definition.GetScenePaths(),
				locationPathName = context.OutputPath,
				target = definition.Platform.ToBuildTarget(),
				targetGroup = definition.Platform.ToBuildTargetGroup(),
				options = BuildOptions.None
			};

			if (options.scenes == null || options.scenes.Length == 0)
			{
				throw new Exception("No scenes to build (definition has no scenes and EditorBuildSettings is empty).");
			}

			UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
			UnityEditor.Build.Reporting.BuildSummary summary = report.summary;
			bool succeeded = summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;

			return new BuildResult
			{
				Success = succeeded,
				DefinitionName = definition.DefinitionName,
				Platform = definition.Platform.ToServerToken(),
				ArtifactPath = succeeded ? context.OutputPath : "",
				VersionName = context.VersionName,
				VersionCode = context.VersionCode,
				Error = succeeded ? null : ("BuildPipeline result=" + summary.result + " errors=" + summary.totalErrors)
			};
		}

#if UNITY_6000_0_OR_NEWER
		/// <summary>
		/// Build from a Unity 6 Build Profile. The profile carries target + scenes + defines + player-setting
		/// overrides, but those only apply once the profile is ACTIVE - and activating it triggers a recompile
		/// + domain reload that CANNOT happen mid-script in batch mode. So CI must launch Unity with
		/// <c>-activeBuildProfile &lt;path&gt;</c> (the executor passes the path the panel sends as
		/// <c>unitybuild.buildProfile</c>); we only build, never switch, in batch mode. The build target is the
		/// profile's, so we do not set it.
		/// </summary>
		private static BuildResult RunWithProfile(BuildDefinition definition, BuildContext context)
		{
			BuildProfile profile = definition.Profile;
			BuildProfile active = BuildProfile.GetActiveBuildProfile();

			if (active != profile)
			{
				string profilePath = AssetDatabase.GetAssetPath(profile);
				if (context.IsBatchMode)
				{
					throw new Exception("Build profile '" + profile.name + "' is not active. In batch mode it must be " +
						"pre-activated - launch Unity with -activeBuildProfile \"" + profilePath + "\" (the recompile that " +
						"activation needs cannot happen mid-script in batch mode). Active profile: '" +
						(active != null ? active.name : "<none>") + "'.");
				}

				// Interactive Editor: activating recompiles + reloads the domain, so we cannot build in this same
				// call. The Build Panel will resume after the reload; for now activate and ask the user to re-run.
				BuildProfile.SetActiveBuildProfile(profile);
				throw new Exception("Activated build profile '" + profile.name + "' - it triggers a script recompile. " +
					"Run the build again once scripts finish reloading (seamless local profile builds arrive with the Build Panel).");
			}

			BuildPlayerWithProfileOptions options = new BuildPlayerWithProfileOptions
			{
				buildProfile = profile,
				locationPathName = context.OutputPath,
				options = BuildOptions.None
			};

			UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
			UnityEditor.Build.Reporting.BuildSummary summary = report.summary;
			bool succeeded = summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;

			return new BuildResult
			{
				Success = succeeded,
				DefinitionName = definition.DefinitionName,
				Platform = definition.Platform.ToServerToken(),
				ArtifactPath = succeeded ? summary.outputPath : "",
				VersionName = context.VersionName,
				VersionCode = context.VersionCode,
				Error = succeeded ? null : ("BuildPipeline result=" + summary.result + " errors=" + summary.totalErrors)
			};
		}
#endif

		/// <summary>
		/// Migration escape hatch: invoke a game's existing static headless builder (e.g.
		/// "AndroidBuilder.BuildFromCommandLine") instead of the built-in pipeline. The game's builder reads
		/// its own env/args; we just call it and report success if it didn't throw. Lets projects adopt the
		/// definition/CI model without rewriting their builder first.
		/// </summary>
		private static BuildResult RunGameBuilder(BuildDefinition definition, BuildContext context)
		{
			(string typeName, string methodName) = SplitMethod(definition.BuildMethod);
			Type type = FindType(typeName) ?? throw new Exception("Build method type not found: '" + typeName + "'");
			MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
				?? throw new Exception("Static method not found: '" + typeName + "." + methodName + "'");

			Debug.Log("[Build] invoking game builder " + typeName + "." + methodName +
				(string.IsNullOrEmpty(definition.BuildMethodArgs) ? "" : " (args: " + definition.BuildMethodArgs + ")"));

			// Pass args only if the method accepts a single string[] / string; otherwise call parameterless.
			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]))
			{
				method.Invoke(null, new object[] { SplitArgs(definition.BuildMethodArgs) });
			}
			else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
			{
				method.Invoke(null, new object[] { definition.BuildMethodArgs ?? "" });
			}
			else
			{
				method.Invoke(null, null);
			}

			return new BuildResult
			{
				Success = true,
				DefinitionName = definition.DefinitionName,
				Platform = definition.Platform.ToServerToken(),
				ArtifactPath = FindArtifact(context),
				VersionName = context.VersionName,
				VersionCode = context.VersionCode
			};
		}

		#endregion

		#region Private Methods - Post-Build Action Pipeline

		/// <summary>
		/// Runs an ordered <see cref="PostBuildAction"/> pipeline over the just-built artifact (§10). Seeds the
		/// available-artifact set with <paramref name="seed"/> (the definition's <see cref="BuildDefinition.OutputKind"/>),
		/// then for each action: gates by run-location, an env skip-set, and host capabilities; validates the
		/// declarative artifact flow (Consumes/Produces); runs it; and on success grows the available set and merges
		/// metadata onto <paramref name="result"/>. Any "configured-but-can't-run", artifact-flow, or fatal-failure
		/// condition THROWS - the caller turns that into an overall build failure (exit 1). Internal so the headless
		/// smoke test can drive it directly. <paramref name="result"/> may be null (metadata merge is then skipped).
		/// </summary>
		internal static void ExecutePostBuildActions(BuildContext context, BuildDefinition definition,
			IReadOnlyList<PostBuildAction> actions, ArtifactKind seed, BuildResult result)
		{
			if (actions == null || actions.Count == 0) return;

			HashSet<ArtifactKind> available = new HashSet<ArtifactKind> { seed };
			HashSet<string> skip = ReadActionSkipSet();
			RunLocation here = context.IsBatchMode ? RunLocation.Remote : RunLocation.Local;
			ExecContext execContext = context.IsBatchMode ? ExecContext.Server : ExecContext.Local;
			Action<string> log = context.Log ?? (message => Debug.Log("[Build] " + message));

			foreach (PostBuildAction action in actions)
			{
				if (action == null) continue;

				string name = action.DisplayName;

				// Applicability - author intent (RunLocation): skip when it isn't meant for this context.
				if (action.RunLocation != RunLocation.Both && action.RunLocation != here)
				{
					log("action '" + name + "' skipped: not for this run location (" + here + ").");
					continue;
				}

				// Per-build skip-set (env BUILD_ACTIONS_SKIP = comma-separated GUIDs).
				if (!string.IsNullOrEmpty(action.Id) && skip.Contains(action.Id))
				{
					log("action '" + name + "' skipped: id in BUILD_ACTIONS_SKIP.");
					continue;
				}

				// Capability gate (hard): it was configured to run here, so an unmet requirement fails the build.
				string unmet = FindUnmetRequirement(action);
				if (unmet != null)
				{
					throw new Exception("Post-build action '" + name + "' is configured to run here but cannot: " +
						unmet + ".");
				}

				// Artifact-flow validation: its input must have been produced by an earlier step (or be the seed).
				if (action.Consumes != ArtifactKind.None && !available.Contains(action.Consumes))
				{
					throw new Exception("action '" + name + "' needs " + action.Consumes +
						" but no prior step produced it.");
				}

				// Secret resolution (just-in-time): resolve THIS action's RequiredSecrets through the project's
				// provider into ctx.Secrets before it runs. A missing registry entry or an op/provider error is a
				// hard precondition failure (the action can't do its job), so it fails the build with a clear message.
				ResolveActionSecrets(context, action, execContext, log);

				// Run (await synchronously - batch mode has no sync context, so GetResult cannot deadlock).
				log("post-build action: " + name);
				ActionResult actionResult = action.ExecuteAsync(context, definition).GetAwaiter().GetResult()
					?? throw new Exception("Post-build action '" + name + "' returned a null result.");

				if (!actionResult.Success)
				{
					if (actionResult.Fatal)
					{
						throw new Exception("Post-build action '" + name + "' failed: " + actionResult.Message);
					}

					Debug.LogWarning("[Build] post-build action '" + name + "' failed (non-fatal), continuing: " +
						actionResult.Message);
					continue;
				}

				if (action.Produces != ArtifactKind.None) available.Add(action.Produces);
				MergeMetadata(result, actionResult.Metadata);
				if (!string.IsNullOrEmpty(actionResult.Message)) log("action '" + name + "': " + actionResult.Message);
			}
		}

		/// <summary>Per-build skip-set of action GUIDs from <c>BUILD_ACTIONS_SKIP</c> (comma-separated, case-insensitive).</summary>
		private static HashSet<string> ReadActionSkipSet()
		{
			HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string raw = Environment.GetEnvironmentVariable("BUILD_ACTIONS_SKIP");
			if (string.IsNullOrEmpty(raw)) return set;

			foreach (string part in raw.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string trimmed = part.Trim();
				if (!string.IsNullOrEmpty(trimmed)) set.Add(trimmed);
			}

			return set;
		}

		/// <summary>
		/// Resolves an action's declared <see cref="PostBuildAction.RequiredSecrets"/> into <see cref="BuildContext.Secrets"/>
		/// just before it runs: each requirement's logical key -> <see cref="ProjectConfig.FindSecret"/> -> the
		/// scheme-tagged <see cref="SecretRef"/> -> the matching <see cref="ISecretProvider"/>'s
		/// <see cref="ISecretProvider.ResolveAsync"/>. A declared-but-unregistered key, an unknown scheme, or a
		/// provider/op error THROWS with an actionable message (the caller turns it into an overall build failure).
		/// Already-resolved keys are skipped, so a secret shared by several actions resolves once.
		/// </summary>
		private static void ResolveActionSecrets(BuildContext context, PostBuildAction action, ExecContext execContext,
			Action<string> log)
		{
			foreach (SecretRequirement requirement in action.RequiredSecrets)
			{
				string key = requirement.Key;
				if (string.IsNullOrEmpty(key)) continue;
				if (context.Secrets != null && context.Secrets.ContainsKey(key)) continue;

				if (context.Project == null)
				{
					throw new Exception("action '" + action.DisplayName + "' requires secret '" + key +
						"' but no ProjectConfig was found to resolve it.");
				}

				SecretDeclaration declaration = context.Project.FindSecret(key);
				if (declaration == null)
				{
					throw new Exception("action '" + action.DisplayName + "' requires secret '" + key +
						"' but it has no entry in the project secret registry - add an op:// reference for '" + key +
						"' in ProjectConfig.");
				}

				SecretRef secretRef = declaration.Ref;
				ISecretProvider provider = ResolveSecretProvider(secretRef.Scheme);
				if (provider == null)
				{
					throw new Exception("action '" + action.DisplayName + "': no secret provider is registered for scheme '" +
						secretRef.Scheme + "' (secret '" + key + "').");
				}

				try
				{
					SecretValue value = provider.ResolveAsync(secretRef, execContext).GetAwaiter().GetResult()
						?? throw new Exception("provider returned a null value");
					(context.Secrets ??= new Dictionary<string, SecretValue>())[key] = value;
					log("resolved secret '" + key + "'.");
				}
				catch (Exception exception)
				{
					throw new Exception("action '" + action.DisplayName + "': failed to resolve secret '" + key + "' (" +
						secretRef.Reference + "): " + exception.Message, exception);
				}
			}
		}

		/// <summary>
		/// The scheme -> provider registry. Minimal for now (only 1Password / "op"); a second provider (OpenBao,
		/// §11.4) slots in here behind the same <see cref="ISecretProvider"/> seam. Returns null for an unknown scheme.
		/// </summary>
		private static ISecretProvider ResolveSecretProvider(string scheme)
		{
			switch (scheme)
			{
				case OnePasswordProvider.SchemeName:
					return new OnePasswordProvider();
				default:
					return null;
			}
		}

		/// <summary>
		/// The hard capability gate: returns a human-readable reason for the first UNMET <see cref="HostRequirement"/>,
		/// or null when all are satisfied. OS is checked against <see cref="Application.platform"/>; Tool by locating
		/// the executable on PATH; Device is treated as satisfied (can't probe a connected device headlessly).
		/// </summary>
		private static string FindUnmetRequirement(PostBuildAction action)
		{
			foreach (HostRequirement requirement in action.HostRequirements)
			{
				switch (requirement.Kind)
				{
					case HostRequirement.HostKind.OperatingSystem:
						if (!CurrentOsMatches(requirement.Value))
						{
							return "requires OS '" + requirement.Value + "' (host is " + Application.platform + ")";
						}

						break;
					case HostRequirement.HostKind.Tool:
						if (!ToolOnPath(requirement.Value))
						{
							return "requires tool '" + requirement.Value + "' on PATH";
						}

						break;
					case HostRequirement.HostKind.Device:
						// Device presence can't be checked in a headless build - treated as satisfied for now.
						break;
				}
			}

			return null;
		}

		private static bool CurrentOsMatches(string os)
		{
			if (string.IsNullOrEmpty(os)) return true;

			RuntimePlatform platform = Application.platform;
			bool isMac = platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.OSXPlayer;
			bool isWindows = platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer;
			bool isLinux = platform == RuntimePlatform.LinuxEditor || platform == RuntimePlatform.LinuxPlayer;

			switch (os.Trim().ToLowerInvariant())
			{
				case "macos":
				case "osx":
				case "mac":
					return isMac;
				case "windows":
				case "win":
					return isWindows;
				case "linux":
					return isLinux;
				default:
					return false;
			}
		}

		private static bool ToolOnPath(string tool)
		{
			if (string.IsNullOrEmpty(tool)) return true;

			string path = Environment.GetEnvironmentVariable("PATH") ?? "";
			RuntimePlatform platform = Application.platform;
			bool isWindows = platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer;

			List<string> extensions = new List<string> { "" };
			if (isWindows)
			{
				string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD";
				foreach (string ext in pathExt.Split(';'))
				{
					if (!string.IsNullOrEmpty(ext)) extensions.Add(ext);
				}
			}

			foreach (string directory in path.Split(Path.PathSeparator))
			{
				string trimmed = directory.Trim();
				if (string.IsNullOrEmpty(trimmed)) continue;

				foreach (string ext in extensions)
				{
					try
					{
						if (File.Exists(Path.Combine(trimmed, tool + ext))) return true;
					}
					catch (Exception)
					{
						// Malformed PATH entry - ignore and keep scanning.
					}
				}
			}

			return false;
		}

		private static void MergeMetadata(BuildResult result, Dictionary<string, string> metadata)
		{
			if (result == null || metadata == null || metadata.Count == 0) return;

			foreach (KeyValuePair<string, string> pair in metadata)
			{
				result.Metadata.Add(new BuildResult.MetaEntry { Key = pair.Key, Value = pair.Value });
			}
		}

		/// <summary>
		/// Builds the <see cref="BuildContext.Log"/> hook the pipeline uses for phase text: writes to the Unity log
		/// AND, under CI batch mode, emits a TeamCity <c>##teamcity[progressMessage '...']</c> service line on stdout
		/// so the phase surfaces in the build's progress (mirrors §5.5).
		/// </summary>
		private static Action<string> MakeProgressLog(BuildContext context)
		{
			bool batch = context.IsBatchMode;
			return message =>
			{
				Debug.Log("[Build] " + message);
				if (batch) Console.WriteLine("##teamcity[progressMessage '" + EscapeTeamCity(message) + "']");
			};
		}

		/// <summary>Escapes a string for a TeamCity service-message value (| ' [ ] and newlines).</summary>
		private static string EscapeTeamCity(string value)
		{
			if (string.IsNullOrEmpty(value)) return "";

			return value
				.Replace("|", "||")
				.Replace("'", "|'")
				.Replace("\n", "|n")
				.Replace("\r", "|r")
				.Replace("[", "|[")
				.Replace("]", "|]");
		}

		#endregion

		#region Private Methods - Apply Definition Settings

		private static void EnsureBuildTarget(BuildDefinition definition, BuildContext context)
		{
#if UNITY_6000_0_OR_NEWER
			// Profile builds get their target from the (pre-activated) profile - see RunWithProfile.
			if (definition.Profile != null) return;
#endif
			BuildTarget target = definition.Platform.ToBuildTarget();
			if (EditorUserBuildSettings.activeBuildTarget != target)
			{
				if (context.IsBatchMode)
				{
					throw new Exception("Active build target is " + EditorUserBuildSettings.activeBuildTarget +
						" but the definition targets " + target + ". In batch mode launch Unity with -buildTarget " +
						target + " (the target cannot be switched mid-script in batch mode).");
				}

				Debug.Log("[Build] switching active build target -> " + target + " (a recompile follows)");
				EditorUserBuildSettings.SwitchActiveBuildTarget(definition.Platform.ToBuildTargetGroup(), target);
			}
		}

		/// <summary>
		/// Adds/removes scripting defines for the built-in, no-profile path. Player defines DO take effect in
		/// batch mode (BuildPlayer compiles the player with the current symbols); the surrounding
		/// <see cref="ProjectSettingsScope"/> restores the originals afterwards. Profiles own their defines;
		/// the shim lets the game's builder own them - so this is a no-op for those.
		/// </summary>
		private static void ApplyDefines(BuildDefinition definition, BuildContext context)
		{
#if UNITY_6000_0_OR_NEWER
			if (definition.Profile != null) return;
#endif
			if (definition.UsesGameBuilder) return;

			IReadOnlyList<string> include = definition.IncludeDefines;
			IReadOnlyList<string> exclude = definition.ExcludeDefines;
			bool hasInclude = include != null && include.Count > 0;
			bool hasExclude = exclude != null && exclude.Count > 0;
			if (!hasInclude && !hasExclude) return;

			NamedBuildTarget namedTarget = NamedBuildTarget.FromBuildTargetGroup(definition.Platform.ToBuildTargetGroup());
			PlayerSettings.GetScriptingDefineSymbols(namedTarget, out string[] current);
			List<string> defines = current.ToList();

			if (hasInclude)
			{
				foreach (string define in include)
				{
					string trimmed = define?.Trim();
					if (!string.IsNullOrEmpty(trimmed) && !defines.Contains(trimmed)) defines.Add(trimmed);
				}
			}

			if (hasExclude)
			{
				foreach (string define in exclude)
				{
					string trimmed = define?.Trim();
					if (!string.IsNullOrEmpty(trimmed)) defines.Remove(trimmed);
				}
			}

			PlayerSettings.SetScriptingDefineSymbols(namedTarget, defines.ToArray());
			Debug.Log("[Build] scripting defines (" + namedTarget.TargetName + "): " + string.Join(";", defines));
		}

		private static void ApplyAndroidSigning(BuildDefinition definition, BuildContext context)
		{
			if (!(definition is AndroidBuildDefinition android) || !android.Signing.IsConfigured) return;

			AndroidSigning signing = android.Signing;
			string keystorePath = Path.IsPathRooted(signing.KeystoreFile)
				? signing.KeystoreFile
				: Path.GetFullPath(Path.Combine(CheckoutRoot(), signing.KeystoreFile));
			string storePass = Environment.GetEnvironmentVariable(signing.KeystorePasswordEnvOrDefault);
			string aliasPass = Environment.GetEnvironmentVariable(signing.KeyAliasPasswordEnvOrDefault);
			if (string.IsNullOrEmpty(aliasPass)) aliasPass = storePass;

			if (string.IsNullOrEmpty(storePass))
			{
				Debug.LogWarning("[Build] Android signing configured but env '" + signing.KeystorePasswordEnvOrDefault +
					"' is empty - the build may fall back to a debug keystore.");
				return;
			}

			PlayerSettings.Android.useCustomKeystore = true;
			PlayerSettings.Android.keystoreName = keystorePath;
			PlayerSettings.Android.keystorePass = storePass;
			PlayerSettings.Android.keyaliasName = signing.KeyAlias;
			PlayerSettings.Android.keyaliasPass = aliasPass;
			Debug.Log("[Build] Android signing applied (keystore=" + keystorePath + ", alias=" + signing.KeyAlias + ").");
		}

		private static string ResolveOutputPath(BuildDefinition definition, BuildContext context)
		{
			string extension;
			if (definition is AndroidBuildDefinition android)
			{
				extension = android.Output == AndroidOutput.AAB ? ".aab" : ".apk";
			}
			else if (definition.Platform == BuildPlatform.WindowsStandalone)
			{
				extension = ".exe";
			}
			else
			{
				extension = ""; // iOS/Mac/Linux/WebGL produce a folder; refined later.
			}

			string fileName = string.IsNullOrEmpty(definition.OutputFileName)
				? SanitizeFileName(Application.productName) + "_" + context.VersionName + "_vc" + context.VersionCode
				: definition.OutputFileName
					.Replace("{project}", SanitizeFileName(Application.productName))
					.Replace("{version}", context.VersionName)
					.Replace("{code}", context.VersionCode.ToString());

			// Output under the checkout's Builds/Output/<Platform>/ staging (matches the server's artifact rules).
			string directory = Path.Combine(CheckoutRoot(), "Builds", "Output", definition.Platform.ToServerToken());
			Directory.CreateDirectory(directory);
			return Path.Combine(directory, fileName + extension);
		}

		#endregion

		#region Private Methods - Discovery

		private static BuildDefinition FindDefinition(string definitionName)
		{
			foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BuildDefinition)))
			{
				string path = AssetDatabase.GUIDToAssetPath(guid);
				BuildDefinition definition = AssetDatabase.LoadAssetAtPath<BuildDefinition>(path);
				if (definition != null && definition.DefinitionName == definitionName) return definition;
			}

			return null;
		}

		private static ProjectConfig FindProjectConfig()
		{
			string[] guids = AssetDatabase.FindAssets("t:" + nameof(ProjectConfig));
			if (guids.Length == 0) return null;
			if (guids.Length > 1) Debug.LogWarning("[Build] multiple ProjectConfig assets found; using the first.");

			return AssetDatabase.LoadAssetAtPath<ProjectConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));
		}

		private static string FindArtifact(BuildContext context)
		{
			string directory = Path.Combine(CheckoutRoot(), "Builds");
			if (!Directory.Exists(directory)) return "";

			string pattern = context.Definition is AndroidBuildDefinition android
				? (android.Output == AndroidOutput.AAB ? "*.aab" : "*.apk")
				: "*";
			return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).FirstOrDefault() ?? "";
		}

		#endregion

		#region Private Methods - Utilities

		/// <summary>
		/// Root for repo-relative paths (build output, keystore). CI sets BUILD_CHECKOUT_ROOT to the TeamCity
		/// checkout dir so artifacts/keystores resolve there; locally we fall back to the Unity project root
		/// (parent of Assets). Never the process cwd - CI launches Unity with a neutral working dir, so cwd is
		/// not the checkout.
		/// </summary>
		private static string CheckoutRoot()
		{
			string root = Environment.GetEnvironmentVariable("BUILD_CHECKOUT_ROOT");
			return string.IsNullOrEmpty(root) ? Directory.GetParent(Application.dataPath).FullName : root;
		}

		private static string GetArg(string flag)
		{
			string[] args = Environment.GetCommandLineArgs();
			for (int i = 0; i < args.Length - 1; i++)
			{
				if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
			}

			return null;
		}

		private static (string Type, string Method) SplitMethod(string fullyQualified)
		{
			int index = fullyQualified.LastIndexOf('.');
			if (index <= 0) throw new Exception("buildMethod must be 'Type.Method' or 'Namespace.Type.Method': " + fullyQualified);

			return (fullyQualified.Substring(0, index), fullyQualified.Substring(index + 1));
		}

		private static string[] SplitArgs(string args)
		{
			return string.IsNullOrEmpty(args)
				? Array.Empty<string>()
				: args.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		}

		private static Type FindType(string typeName)
		{
			// Search loaded assemblies; try the bare name and a namespace-insensitive match.
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(typeName, false);
				if (type != null) return type;
			}

			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (Type type in SafeGetTypes(assembly))
				{
					if (type.Name == typeName || type.FullName == typeName) return type;
				}
			}

			return null;
		}

		private static Type[] SafeGetTypes(Assembly assembly)
		{
			try
			{
				return assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException exception)
			{
				return exception.Types.Where(type => type != null).ToArray();
			}
		}

		private static string SanitizeFileName(string value)
		{
			foreach (char invalid in Path.GetInvalidFileNameChars())
			{
				value = value.Replace(invalid, '_');
			}

			return value.Replace(' ', '_');
		}

		#endregion
	}
}

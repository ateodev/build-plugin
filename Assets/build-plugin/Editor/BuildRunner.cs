using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ateo.Build
{
    /// <summary>
    /// The single generic headless entry point (L2). CI always calls the SAME method regardless of game or
    /// platform:
    ///   Unity ... -executeMethod Ateo.Build.BuildRunner.Run -buildDefinition "Android-AAB-Release"
    ///             [-buildResult "&lt;path&gt;.json"]
    /// It loads the named <see cref="BuildDefinition"/> from the project, applies it (target, defines,
    /// version stamp, signing), runs pre-steps, builds (built-in or via a game's own method shim), runs
    /// post-steps, writes a machine-readable <see cref="BuildResult"/>, and sets the process exit code.
    /// The exact same path runs for an in-editor local build via <see cref="RunDefinition"/> - that local/CI
    /// parity is the whole point.
    /// </summary>
    public static class BuildRunner
    {
        /// <summary>CI entry. Reads -buildDefinition / -buildResult from the command line.</summary>
        public static void Run()
        {
            var defName = GetArg("-buildDefinition");
            var resultPath = GetArg("-buildResult");
            BuildResult result;

            try
            {
                if (string.IsNullOrEmpty(defName))
                    throw new Exception("Missing -buildDefinition <name> argument.");

                var def = FindDefinition(defName);
                if (def == null)
                    throw new Exception("Build definition '" + defName + "' not found. Expected a BuildDefinition " +
                                        "asset with definitionName == '" + defName + "' (usually under Assets/BuildConfigs/).");

                result = RunDefinition(def);
            }
            catch (Exception e)
            {
                result = BuildResult.Failed(defName, e.ToString());
                Debug.LogError("[Build] FAILED: " + e);
            }

            if (!string.IsNullOrEmpty(resultPath))
                result.WriteJson(resultPath);

            if (Application.isBatchMode)
                EditorApplication.Exit(result.success ? 0 : 1);
        }

        /// <summary>
        /// Build a definition through the full pipeline. Shared by CI (<see cref="Run"/>) and local builds
        /// (the Editor panel in a later version), so both go through identical logic.
        /// </summary>
        public static BuildResult RunDefinition(BuildDefinition def)
        {
            var sw = Stopwatch.StartNew();
            var ctx = new BuildContext
            {
                definition = def,
                project = FindProjectConfig(),
                isBatchMode = Application.isBatchMode
            };

            Debug.Log("[Build] definition='" + def.definitionName + "' platform=" + def.platform +
                      (def.UsesGameBuilder ? " method='" + def.buildMethod + "'" : " (built-in)"));

            // 1. Active build target.
            var target = def.platform.ToBuildTarget();
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                Debug.Log("[Build] switching active build target -> " + target);
                EditorUserBuildSettings.SwitchActiveBuildTarget(def.platform.ToBuildTargetGroup(), target);
            }

            // 2. Layered scripting defines.
            ApplyExtraDefines(def);

            // 3. Version stamp (incl. BUILD_VERSION_NAME / ANDROID_VERSION_CODE / IOS_BUILD_NUMBER).
            VersionStamp.Apply(ctx);

            // 4. Android signing from env references (passwords resolved agent-side, never in the asset).
            ApplyAndroidSigning(def, ctx);

            // 5. Resolve output path.
            ctx.outputPath = ResolveOutputPath(def, ctx);

            // 6. Pre-build steps.
            foreach (var step in def.preSteps)
                if (step != null) step.OnPreBuild(ctx);

            // 7. Build - via the game's own method (migration shim) or the built-in pipeline.
            BuildResult result;
            try
            {
                result = def.UsesGameBuilder ? RunGameBuilder(def, ctx) : RunBuiltIn(def, ctx);
            }
            catch (Exception e)
            {
                result = BuildResult.Failed(def.definitionName, e.ToString());
                Debug.LogError("[Build] build step threw: " + e);
            }

            // 8. Post-build steps (always run, success or failure).
            foreach (var step in def.postSteps)
            {
                if (step == null) continue;
                try { step.OnPostBuild(ctx, result); }
                catch (Exception e) { Debug.LogWarning("[Build] post-step '" + step.name + "' threw: " + e.Message); }
            }

            result.durationSeconds = sw.ElapsedMilliseconds / 1000;
            Debug.Log("[Build] " + (result.success ? "SUCCESS" : "FAILURE") + " in " + result.durationSeconds +
                      "s" + (result.success ? " -> " + result.artifactPath : ""));
            return result;
        }

        // ---- build paths -------------------------------------------------------------------------------

        static BuildResult RunBuiltIn(BuildDefinition def, BuildContext ctx)
        {
            if (def.platform == BuildPlatform.Android)
                EditorUserBuildSettings.buildAppBundle = def.androidOutput == AndroidOutput.AAB;

            var options = new BuildPlayerOptions
            {
                scenes = def.GetScenePaths(),
                locationPathName = ctx.outputPath,
                target = def.platform.ToBuildTarget(),
                targetGroup = def.platform.ToBuildTargetGroup(),
                options = BuildOptions.None
            };

            if (options.scenes == null || options.scenes.Length == 0)
                throw new Exception("No scenes to build (definition has no scenes and EditorBuildSettings is empty).");

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            var ok = summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;

            return new BuildResult
            {
                success = ok,
                definitionName = def.definitionName,
                platform = def.platform.ToServerToken(),
                artifactPath = ok ? ctx.outputPath : "",
                versionName = ctx.versionName,
                versionCode = ctx.versionCode,
                error = ok ? null : ("BuildPipeline result=" + summary.result + " errors=" + summary.totalErrors)
            };
        }

        /// <summary>
        /// Migration escape hatch: invoke a game's existing static headless builder (e.g.
        /// "AndroidBuilder.BuildFromCommandLine") instead of the built-in pipeline. The game's builder reads
        /// its own env/args; we just call it and report success if it didn't throw. Lets projects adopt the
        /// definition/CI model without rewriting their builder first.
        /// </summary>
        static BuildResult RunGameBuilder(BuildDefinition def, BuildContext ctx)
        {
            var (typeName, methodName) = SplitMethod(def.buildMethod);
            var type = FindType(typeName)
                       ?? throw new Exception("Build method type not found: '" + typeName + "'");
            var method = type.GetMethod(methodName,
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                         ?? throw new Exception("Static method not found: '" + typeName + "." + methodName + "'");

            Debug.Log("[Build] invoking game builder " + typeName + "." + methodName +
                      (string.IsNullOrEmpty(def.buildMethodArgs) ? "" : " (args: " + def.buildMethodArgs + ")"));
            // Pass args only if the method accepts a single string[] / string; otherwise call parameterless.
            var ps = method.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string[]))
                method.Invoke(null, new object[] { SplitArgs(def.buildMethodArgs) });
            else if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                method.Invoke(null, new object[] { def.buildMethodArgs ?? "" });
            else
                method.Invoke(null, null);

            return new BuildResult
            {
                success = true,
                definitionName = def.definitionName,
                platform = def.platform.ToServerToken(),
                artifactPath = FindArtifact(ctx),
                versionName = ctx.versionName,
                versionCode = ctx.versionCode
            };
        }

        // ---- application of definition settings --------------------------------------------------------

        static void ApplyExtraDefines(BuildDefinition def)
        {
            if (def.extraScriptingDefines == null || def.extraScriptingDefines.Length == 0) return;
            var named = NamedBuildTarget.FromBuildTargetGroup(def.platform.ToBuildTargetGroup());
            var current = PlayerSettings.GetScriptingDefineSymbols(named)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            foreach (var d in def.extraScriptingDefines)
                if (!string.IsNullOrWhiteSpace(d) && !current.Contains(d.Trim()))
                    current.Add(d.Trim());
            PlayerSettings.SetScriptingDefineSymbols(named, current.ToArray());
            Debug.Log("[Build] scripting defines (" + named.TargetName + "): " + string.Join(";", current));
        }

        static void ApplyAndroidSigning(BuildDefinition def, BuildContext ctx)
        {
            if (def.platform != BuildPlatform.Android || !def.androidSigning.IsConfigured) return;
            var s = def.androidSigning;
            var keystorePath = Path.IsPathRooted(s.keystoreFile)
                ? s.keystoreFile
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), s.keystoreFile));
            var storePass = Environment.GetEnvironmentVariable(s.KeystorePasswordEnvOrDefault);
            var aliasPass = Environment.GetEnvironmentVariable(s.KeyAliasPasswordEnvOrDefault);
            if (string.IsNullOrEmpty(aliasPass)) aliasPass = storePass;

            if (string.IsNullOrEmpty(storePass))
            {
                Debug.LogWarning("[Build] Android signing configured but env '" + s.KeystorePasswordEnvOrDefault +
                                 "' is empty - the build may fall back to a debug keystore.");
                return;
            }

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystorePath;
            PlayerSettings.Android.keystorePass = storePass;
            PlayerSettings.Android.keyaliasName = s.keyAlias;
            PlayerSettings.Android.keyaliasPass = aliasPass;
            Debug.Log("[Build] Android signing applied (keystore=" + keystorePath + ", alias=" + s.keyAlias + ").");
        }

        static string ResolveOutputPath(BuildDefinition def, BuildContext ctx)
        {
            var ext = def.platform == BuildPlatform.Android
                ? (def.androidOutput == AndroidOutput.AAB ? ".aab" : ".apk")
                : ""; // iOS/standalone produce a folder; refined per-platform later.

            var fileName = string.IsNullOrEmpty(def.outputFileName)
                ? SanitizeFileName(Application.productName) + "_" + ctx.versionName + "_vc" + ctx.versionCode
                : def.outputFileName
                    .Replace("{project}", SanitizeFileName(Application.productName))
                    .Replace("{version}", ctx.versionName)
                    .Replace("{code}", ctx.versionCode.ToString());

            // Output under the checkout's Builds/Output/<Platform>/ staging (matches the server's artifact rules).
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "Builds", "Output", def.platform.ToServerToken());
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName + ext);
        }

        // ---- discovery helpers -------------------------------------------------------------------------

        static BuildDefinition FindDefinition(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(BuildDefinition)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<BuildDefinition>(path);
                if (def != null && def.definitionName == name) return def;
            }
            return null;
        }

        static ProjectConfig FindProjectConfig()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(ProjectConfig));
            if (guids.Length == 0) return null;
            if (guids.Length > 1)
                Debug.LogWarning("[Build] multiple ProjectConfig assets found; using the first.");
            return AssetDatabase.LoadAssetAtPath<ProjectConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        static string FindArtifact(BuildContext ctx)
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "Builds");
            if (!Directory.Exists(dir)) return "";
            var pattern = ctx.Platform == BuildPlatform.Android
                ? (ctx.definition.androidOutput == AndroidOutput.AAB ? "*.aab" : "*.apk")
                : "*";
            return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).FirstOrDefault() ?? "";
        }

        // ---- small utilities ---------------------------------------------------------------------------

        static string GetArg(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static (string type, string method) SplitMethod(string fq)
        {
            var idx = fq.LastIndexOf('.');
            if (idx <= 0) throw new Exception("buildMethod must be 'Type.Method' or 'Namespace.Type.Method': " + fq);
            return (fq.Substring(0, idx), fq.Substring(idx + 1));
        }

        static string[] SplitArgs(string args)
            => string.IsNullOrEmpty(args) ? Array.Empty<string>()
                                          : args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        static Type FindType(string typeName)
        {
            // Search loaded assemblies; try the bare name and a namespace-insensitive match.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName, false);
                if (t != null) return t;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var t in SafeGetTypes(asm))
                    if (t.Name == typeName || t.FullName == typeName) return t;
            return null;
        }

        static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        static string SanitizeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}

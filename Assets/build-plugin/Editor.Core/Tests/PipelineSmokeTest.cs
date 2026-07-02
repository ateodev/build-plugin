using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build.Tests
{
	/// <summary>
	/// TEST-ONLY headless smoke harness for the post-build-action pipeline (NOT a shipped action). Drives
	/// <see cref="BuildRunner.ExecutePostBuildActions"/> directly with a couple of tiny in-file sample actions to
	/// prove the pipeline actually RUNS (compile alone can't), asserts the declarative artifact-flow gate
	/// rejects an action whose <c>Consumes</c> kind was never produced, then LINTS every SHIPPED catalog
	/// action's declarations (DisplayName / Consumes / Produces / RequiredSecrets / HostRequirements) for
	/// internal consistency - declarations only, no tool execution. Run it from CI/CLI with:
	///   Unity ... -batchmode -quit -executeMethod Ateo.Build.Tests.PipelineSmokeTest.RunPipelineSmoke
	/// Exits 0 when every check passes, 1 otherwise. Lives under Tests/ so it is obviously a sample, not a real
	/// catalog action (§10.2).
	/// </summary>
	public static class PipelineSmokeTest
	{
		#region Sample Actions (test-only, not part of the shipped catalog)

		/// <summary>A no-op action: consumes/produces nothing, flips a flag and logs via the context, returns Ok.</summary>
		private sealed class SmokeTestNoOpAction : PostBuildAction<AndroidBuildDefinition>
		{
			public static bool Executed;

			public override string DisplayName => "Smoke Test No-Op";
			public override ArtifactKind Consumes => ArtifactKind.None;
			public override ArtifactKind Produces => ArtifactKind.None;

			protected override Task<ActionResult> ExecuteAsync(BuildContext ctx, AndroidBuildDefinition def)
			{
				Executed = true;
				if (ctx.Log != null) ctx.Log("smoke no-op action body ran");
				return Task.FromResult(ActionResult.Ok("noop done"));
			}
		}

		/// <summary>A consumer that needs an IPA - used to prove the artifact-flow gate rejects a dangling chain.</summary>
		private sealed class SmokeTestConsumingAction : PostBuildAction<AndroidBuildDefinition>
		{
			public static bool Executed;

			public override string DisplayName => "Smoke Test Consumer";
			public override ArtifactKind Consumes => ArtifactKind.IPA;
			public override ArtifactKind Produces => ArtifactKind.None;

			protected override Task<ActionResult> ExecuteAsync(BuildContext ctx, AndroidBuildDefinition def)
			{
				Executed = true;
				return Task.FromResult(ActionResult.Ok());
			}
		}

		#endregion

		#region Public Methods

		public static void RunPipelineSmoke()
		{
			int failures = 0;

			List<string> logged = new List<string>();
			string fakeFolder = Path.Combine(Path.GetTempPath(), "ateo-pipeline-smoke");
			string fakeArtifact = Path.Combine(fakeFolder, "app.aab");

			BuildContext context = new BuildContext
			{
				IsBatchMode = Application.isBatchMode,
				BuildFolder = fakeFolder,
				Log = message => logged.Add(message)
			};
			context.ArtifactPaths = new List<string> { fakeArtifact };

			// A real (non-null) definition so the action's typed cast exercises the real path.
			BuildDefinition definition = ScriptableObject.CreateInstance<AndroidBuildDefinition>();

			// 1) A no-op action (Consumes/Produces = None) must run, and the pipeline must log through ctx.Log.
			SmokeTestNoOpAction.Executed = false;
			BuildResult result = new BuildResult { Success = true };
			List<PostBuildAction> happyPath = new List<PostBuildAction> { new SmokeTestNoOpAction() };

			try
			{
				BuildRunner.ExecutePostBuildActions(context, definition, happyPath, ArtifactKind.AAB, result);
				failures += Check(SmokeTestNoOpAction.Executed, "no-op action executed") ? 0 : 1;
				failures += Check(logged.Contains("post-build action: Smoke Test No-Op"),
					"pipeline surfaced the action via ctx.Log") ? 0 : 1;
				failures += Check(logged.Contains("smoke no-op action body ran"),
					"action body wrote through ctx.Log") ? 0 : 1;
			}
			catch (Exception exception)
			{
				failures++;
				Debug.LogError("[SmokeTest] FAIL: happy path threw: " + exception);
			}

			// 2) The artifact-flow gate must REJECT a consumer whose Consumes kind was never produced.
			SmokeTestConsumingAction.Executed = false;
			List<PostBuildAction> brokenChain = new List<PostBuildAction> { new SmokeTestConsumingAction() };
			bool threw = false;
			string gateMessage = "";

			try
			{
				BuildRunner.ExecutePostBuildActions(context, definition, brokenChain, ArtifactKind.AAB, null);
			}
			catch (Exception exception)
			{
				threw = true;
				gateMessage = exception.Message;
			}

			failures += Check(threw, "artifact-flow gate rejected an unavailable Consumes") ? 0 : 1;
			failures += Check(!SmokeTestConsumingAction.Executed, "rejected action did NOT run") ? 0 : 1;
			failures += Check(gateMessage.Contains("IPA"), "gate message names the missing kind (IPA)") ? 0 : 1;

			// 3) Declaration lint over every SHIPPED catalog action: the declarations the wizard, panel and
			//    runner reason about must be internally consistent. Reflection-only - no tool ever executes.
			failures += LintShippedActionDeclarations();

			// 4) SecretDemand classification on a synthetic in-memory case (no asset/scene scaffolding): the
			//    demand-driven reconciliation the Secrets view and definition banner run on must classify
			//    needed/registered/unused consistently.
			failures += LintSecretDemandClassification();

			UnityEngine.Object.DestroyImmediate(definition);

			Debug.Log(failures == 0 ? "[SmokeTest] RESULT: ALL PASS" : "[SmokeTest] RESULT: FAILURES=" + failures);
			if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
		}

		#endregion

		#region Private Methods

		/// <summary>
		/// Walks every concrete <see cref="PostBuildAction"/> in the Data assembly (= the shipped catalog; the
		/// assembly filter excludes these in-file test samples) and lints its DECLARATIONS: DisplayName
		/// non-empty, SupportedDefinition a definition type, Consumes/Produces defined enum members, Consumes
		/// satisfiable by SOME OutputKind (via the action's own <see cref="PostBuildAction.CanConsume"/>, so
		/// widened category actions lint their real gate), every RequiredSecret carrying a unique non-empty key
		/// + a defined kind + a description, and every HostRequirement carrying a defined kind + non-empty
		/// value. One PASS/FAIL line per action, listing all of its problems at once. Returns the failure count.
		/// </summary>
		private static int LintShippedActionDeclarations()
		{
			int failures = 0;
			int linted = 0;
			Assembly catalogAssembly = typeof(PostBuildAction).Assembly;

			// The universe every declared Consumes must be satisfiable in: some definition's OutputKind or an
			// earlier action's Produces - both draw from the same enum, so the full value set is the universe.
			HashSet<ArtifactKind> everyKind = new HashSet<ArtifactKind>((ArtifactKind[])Enum.GetValues(typeof(ArtifactKind)));

			foreach (Type type in TypeCache.GetTypesDerivedFrom<PostBuildAction>())
			{
				if (type.Assembly != catalogAssembly || type.IsAbstract || type.IsGenericTypeDefinition) continue;

				PostBuildAction action;
				try
				{
					action = (PostBuildAction)Activator.CreateInstance(type);
				}
				catch (Exception exception)
				{
					failures += Check(false, type.Name + ": instantiable via parameterless constructor (" +
						exception.Message + ")") ? 0 : 1;
					continue;
				}

				linted++;
				List<string> problems = new List<string>();

				if (string.IsNullOrEmpty(action.DisplayName)) problems.Add("DisplayName is empty");

				if (action.SupportedDefinition == null || !typeof(BuildDefinition).IsAssignableFrom(action.SupportedDefinition))
				{
					problems.Add("SupportedDefinition is not a BuildDefinition type");
				}

				if (!Enum.IsDefined(typeof(ArtifactKind), action.Consumes)) problems.Add("Consumes is not a defined ArtifactKind");
				if (!Enum.IsDefined(typeof(ArtifactKind), action.Produces)) problems.Add("Produces is not a defined ArtifactKind");

				if (!action.CanConsume(everyKind))
				{
					problems.Add("Consumes (" + action.Consumes + ") is not satisfiable by any OutputKind");
				}

				HashSet<string> secretKeys = new HashSet<string>(StringComparer.Ordinal);
				foreach (SecretRequirement secret in action.RequiredSecrets)
				{
					if (string.IsNullOrEmpty(secret.Key)) problems.Add("a RequiredSecret has an empty key");
					else if (!secretKeys.Add(secret.Key)) problems.Add("secret key '" + secret.Key + "' declared twice");

					if (!Enum.IsDefined(typeof(SecretKind), secret.Kind)) problems.Add("secret '" + secret.Key + "' has an undefined kind");
					if (string.IsNullOrEmpty(secret.Description)) problems.Add("secret '" + secret.Key + "' has no description");
				}

				foreach (HostRequirement requirement in action.HostRequirements)
				{
					if (!Enum.IsDefined(typeof(HostRequirement.HostKind), requirement.Kind)) problems.Add("a HostRequirement has an undefined kind");
					if (string.IsNullOrEmpty(requirement.Value)) problems.Add("a " + requirement.Kind + " HostRequirement has an empty value");
				}

				string label = type.Name + ": declarations consistent" +
					(problems.Count > 0 ? " - " + string.Join("; ", problems) : "");
				failures += Check(problems.Count == 0, label) ? 0 : 1;
			}

			// Zero discovered actions would mean the reflection sweep silently broke - that must fail, not pass.
			failures += Check(linted > 0, "shipped action catalog discovered (" + linted + " actions linted)") ? 0 : 1;
			return failures;
		}

		/// <summary>
		/// Synthetic <see cref="SecretDemand"/> case: an Android definition with wired signing (demands its two
		/// env-key names) and a GooglePlayUpload action (demands its declared File secret), against a registry
		/// holding one of the needed keys plus one stale orphan. Asserts each key lands in the right
		/// <see cref="SecretDemand.State"/> and that consumers name who needs a key. Pure in-memory
		/// ScriptableObjects + reflection (the same private-field access the wizard/runner already rely on).
		/// </summary>
		private static int LintSecretDemandClassification()
		{
			int failures = 0;
			AndroidBuildDefinition definition = ScriptableObject.CreateInstance<AndroidBuildDefinition>();
			ProjectConfig project = ScriptableObject.CreateInstance<ProjectConfig>();

			try
			{
				SetPrivateField(definition, "_definitionName", "SmokeSecrets");
				SetPrivateField(definition, "_androidSigning",
					new AndroidSigning("keystores/smoke.keystore", "release", "ANDROID_KEYSTORE_PASS", "ANDROID_KEYALIAS_PASS"));

				List<PostBuildAction> actions = GetPrivateField(definition, "_postBuildActions") as List<PostBuildAction>;
				failures += Check(actions != null, "secret demand: definition action list reachable") ? 0 : 1;
				actions?.Add(new GooglePlayUpload());

				List<SecretDeclaration> registry = GetPrivateField(project, "_secretRegistry") as List<SecretDeclaration>;
				failures += Check(registry != null, "secret demand: project registry reachable") ? 0 : 1;
				registry?.Add(new SecretDeclaration("ANDROID_KEYSTORE_PASS", "Android keystore password.",
					SecretKind.String, "op://Vault/smoke-android-signing/storepass"));
				registry?.Add(new SecretDeclaration("STALE_KEY", "No longer used by anything.",
					SecretKind.String, "op://Vault/smoke-stale/value"));

				List<SecretDemand.Row> rows = SecretDemand.Classify(project, new BuildDefinition[] { definition });

				failures += Check(StateOf(rows, "ANDROID_KEYSTORE_PASS") == SecretDemand.State.NeededRegistered,
					"needed + registered key classifies NeededRegistered") ? 0 : 1;
				failures += Check(StateOf(rows, "ANDROID_KEYALIAS_PASS") == SecretDemand.State.NeededUnregistered,
					"wired signing key without registry entry classifies NeededUnregistered") ? 0 : 1;
				failures += Check(StateOf(rows, "PLAY_SERVICE_ACCOUNT_JSON") == SecretDemand.State.NeededUnregistered,
					"action-declared key without registry entry classifies NeededUnregistered") ? 0 : 1;
				failures += Check(StateOf(rows, "STALE_KEY") == SecretDemand.State.RegisteredUnused,
					"registered key nothing needs classifies RegisteredUnused") ? 0 : 1;

				SecretDemand.Row playRow = FindRow(rows, "PLAY_SERVICE_ACCOUNT_JSON");
				failures += Check(playRow != null && playRow.Kind == SecretKind.File,
					"demanded key carries the declaring requirement's kind (File)") ? 0 : 1;
				failures += Check(playRow != null && playRow.Consumers.Count == 1 &&
					playRow.Consumers[0].Contains("Upload to Google Play") && playRow.Consumers[0].Contains("SmokeSecrets"),
					"consumer names the action and the definition label") ? 0 : 1;
			}
			catch (Exception exception)
			{
				failures++;
				Debug.LogError("[SmokeTest] FAIL: secret demand classification threw: " + exception);
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(definition);
				UnityEngine.Object.DestroyImmediate(project);
			}

			return failures;
		}

		private static SecretDemand.Row FindRow(List<SecretDemand.Row> rows, string key)
		{
			return rows.Find(row => row.Key == key);
		}

		private static SecretDemand.State? StateOf(List<SecretDemand.Row> rows, string key)
		{
			SecretDemand.Row row = FindRow(rows, key);
			return row != null ? row.State : (SecretDemand.State?)null;
		}

		private static void SetPrivateField(object target, string name, object value)
		{
			Type type = target.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null)
				{
					field.SetValue(target, value);
					return;
				}

				type = type.BaseType;
			}
		}

		private static object GetPrivateField(object target, string name)
		{
			Type type = target.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null) return field.GetValue(target);

				type = type.BaseType;
			}

			return null;
		}

		private static bool Check(bool condition, string label)
		{
			if (condition) Debug.Log("[SmokeTest] PASS: " + label);
			else Debug.LogError("[SmokeTest] FAIL: " + label);

			return condition;
		}

		#endregion
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build.Tests
{
	/// <summary>
	/// TEST-ONLY headless smoke harness for the post-build-action pipeline (NOT a shipped action). Drives
	/// <see cref="BuildRunner.ExecutePostBuildActions"/> directly with a couple of tiny in-file sample actions to
	/// prove the pipeline actually RUNS (compile alone can't), then asserts the declarative artifact-flow gate
	/// rejects an action whose <c>Consumes</c> kind was never produced. Run it from CI/CLI with:
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

			UnityEngine.Object.DestroyImmediate(definition);

			Debug.Log(failures == 0 ? "[SmokeTest] RESULT: ALL PASS" : "[SmokeTest] RESULT: FAILURES=" + failures);
			if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
		}

		#endregion

		#region Private Methods

		private static bool Check(bool condition, string label)
		{
			if (condition) Debug.Log("[SmokeTest] PASS: " + label);
			else Debug.LogError("[SmokeTest] FAIL: " + label);

			return condition;
		}

		#endregion
	}
}

using System;
using System.Collections.Generic;
using UnityEditor;

namespace Ateo.Build
{
	/// <summary>
	/// Demand-driven secret reconciliation (§11.2, Secrets UX spec): computes the set of logical secret keys the
	/// project's committed data actually NEEDS and classifies the registry against it. "Needed" is derived from
	/// the data, never from a hardcoded list: the union of every attached <see cref="PostBuildAction"/>'s
	/// declared <see cref="PostBuildAction.RequiredSecrets"/> plus the keys WIRED by definition fields (the
	/// Android signing env-key names, the iOS match-password key) - so adding an action or wiring signing makes
	/// its keys show up as demanded everywhere (Secrets view, definition banner) with zero UI changes.
	/// Editor-side only (walks loaded definition assets); the build-time enforcement stays in
	/// <see cref="BuildRunner"/>'s just-in-time resolution.
	/// </summary>
	public static class SecretDemand
	{
		#region Nested Types

		/// <summary>How a logical key stands relative to the registry - drives the Secrets view's Status column.</summary>
		public enum State
		{
			/// <summary>Needed by the data AND registered - healthy (vault presence is a separate, async probe).</summary>
			NeededRegistered,

			/// <summary>Needed by the data but missing from the registry - a build using it will fail; register it.</summary>
			NeededUnregistered,

			/// <summary>Registered but nothing in the data needs it - an orphan entry, safe to remove.</summary>
			RegisteredUnused
		}

		/// <summary>One demanded logical key with everything the UI needs to offer registration for it.</summary>
		public sealed class NeededSecret
		{
			#region Fields

			public string Key;
			public SecretKind Kind;
			public string Description;

			/// <summary>Human-readable "who needs it" entries ("&lt;action&gt; on &lt;definition label&gt;").</summary>
			public List<string> Consumers = new List<string>();

			#endregion
		}

		/// <summary>One reconciled row: a registry entry, a demanded key, or both (the view draws these directly).</summary>
		public sealed class Row
		{
			#region Fields

			public string Key;
			public SecretKind Kind;
			public string Description;
			public State State;

			/// <summary>The registry entry backing this row; null for a needed-but-unregistered key.</summary>
			public SecretDeclaration Declaration;

			/// <summary>Who needs the key ("&lt;action&gt; on &lt;definition label&gt;"); empty for an unused entry.</summary>
			public List<string> Consumers = new List<string>();

			#endregion
		}

		#endregion

		#region Public Methods

		/// <summary>
		/// The union of logical keys needed by <paramref name="definitions"/>, keyed by logical key. Kind and
		/// description come from the FIRST declaring consumer (a key demanded twice with conflicting kinds is an
		/// authoring bug the smoke lint would surface); consumers accumulate across all of them.
		/// </summary>
		public static Dictionary<string, NeededSecret> ComputeNeeded(IEnumerable<BuildDefinition> definitions)
		{
			Dictionary<string, NeededSecret> needed = new Dictionary<string, NeededSecret>(StringComparer.Ordinal);
			if (definitions == null) return needed;

			foreach (BuildDefinition definition in definitions)
			{
				if (definition == null) continue;

				CollectFrom(definition, needed);
			}

			return needed;
		}

		/// <summary>
		/// Reconciles the project registry against the demand of <paramref name="definitions"/>. Row order is
		/// what the Secrets view shows: the registry entries first (in registry order, classified needed/unused),
		/// then the needed-but-unregistered keys appended.
		/// </summary>
		public static List<Row> Classify(ProjectConfig project, IEnumerable<BuildDefinition> definitions)
		{
			Dictionary<string, NeededSecret> needed = ComputeNeeded(definitions);
			List<Row> rows = new List<Row>();
			HashSet<string> registered = new HashSet<string>(StringComparer.Ordinal);

			if (project != null && project.SecretRegistry != null)
			{
				foreach (SecretDeclaration declaration in project.SecretRegistry)
				{
					if (declaration == null || string.IsNullOrEmpty(declaration.LogicalKey)) continue;

					registered.Add(declaration.LogicalKey);
					bool isNeeded = needed.TryGetValue(declaration.LogicalKey, out NeededSecret demand);
					rows.Add(new Row
					{
						Key = declaration.LogicalKey,
						Kind = declaration.Kind,
						Description = declaration.Description,
						State = isNeeded ? State.NeededRegistered : State.RegisteredUnused,
						Declaration = declaration,
						Consumers = isNeeded ? demand.Consumers : new List<string>()
					});
				}
			}

			foreach (KeyValuePair<string, NeededSecret> pair in needed)
			{
				if (registered.Contains(pair.Key)) continue;

				rows.Add(new Row
				{
					Key = pair.Value.Key,
					Kind = pair.Value.Kind,
					Description = pair.Value.Description,
					State = State.NeededUnregistered,
					Declaration = null,
					Consumers = pair.Value.Consumers
				});
			}

			return rows;
		}

		/// <summary>
		/// The needed-but-unregistered keys of ONE definition - the definition view's error banner. Same
		/// derivation as <see cref="ComputeNeeded"/>, scoped to the given definition.
		/// </summary>
		public static List<NeededSecret> UnregisteredFor(ProjectConfig project, BuildDefinition definition)
		{
			List<NeededSecret> missing = new List<NeededSecret>();
			if (definition == null) return missing;

			Dictionary<string, NeededSecret> needed = new Dictionary<string, NeededSecret>(StringComparer.Ordinal);
			CollectFrom(definition, needed);

			foreach (KeyValuePair<string, NeededSecret> pair in needed)
			{
				if (project == null || project.FindSecret(pair.Key) == null) missing.Add(pair.Value);
			}

			return missing;
		}

		/// <summary>
		/// The ONE unassign-confirm text, shared by the Secrets view's Unassign button and the manage dialog's
		/// Unassign verb (dev1: identical wording, stated once - never duplicated per call site). Always states
		/// the entry-only rule (the vault item is untouched); when <paramref name="consumers"/> is non-empty the
		/// key is still NEEDED, so the consequences are spelled out too - who needs it, the row falling back to
		/// 'not registered', builds failing until re-registered. Empty consumers = the simple orphan-entry form.
		/// </summary>
		public static string UnassignConfirmMessage(string logicalKey, IReadOnlyList<string> consumers)
		{
			return "Unassign '" + logicalKey + "' from the project's secret registry?\n\n" +
				"Only the registry entry is removed - the secret in the vault is untouched." +
				StillNeededConsequences(logicalKey, consumers);
		}

		/// <summary>
		/// The manage dialog's DELETE-confirm text - the destructive sibling of
		/// <see cref="UnassignConfirmMessage"/>, built here so both verbs' wording lives side by side. Names the
		/// vault item VERBATIM (dev1: he must recognize exactly what disappears, e.g.
		/// 'build-plugin-test_steam-user'), states that the assignment goes with it, inserts a CAUTION when the
		/// caller detected the item as shared (<paramref name="sharedReason"/> says WHY - two shapes exist:
		/// another registry entry points at it, or a bare un-prefixed title marks it team-level), and appends
		/// the same still-needed consequences as an unassign.
		/// </summary>
		public static string DeleteConfirmMessage(string logicalKey, string item, string sharedReason,
			IReadOnlyList<string> consumers)
		{
			string message = "Delete the vault item '" + item + "'?\n\n" +
				"The whole item is deleted from the vault, and the registry entry for '" + logicalKey +
				"' is removed with it.";

			if (!string.IsNullOrEmpty(sharedReason))
			{
				message += "\n\nCAUTION: this item looks SHARED - " + sharedReason + ". Deleting it may break " +
					"other consumers beyond this project's entry.";
			}

			return message + StillNeededConsequences(logicalKey, consumers);
		}

		/// <summary>Every <see cref="BuildDefinition"/> asset in the project - the demand universe for project-wide
		/// reconciliation (mirrors the Build Panel's own asset scan).</summary>
		public static List<BuildDefinition> CollectDefinitions()
		{
			List<BuildDefinition> definitions = new List<BuildDefinition>();
			foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BuildDefinition)))
			{
				BuildDefinition definition = AssetDatabase.LoadAssetAtPath<BuildDefinition>(AssetDatabase.GUIDToAssetPath(guid));
				if (definition != null) definitions.Add(definition);
			}

			return definitions;
		}

		#endregion

		#region Private Methods

		/// <summary>The shared still-needed tail of both confirms: who needs the key, the row falling back to
		/// 'not registered', builds failing until re-registered. Empty for an orphan entry (nothing breaks).</summary>
		private static string StillNeededConsequences(string logicalKey, IReadOnlyList<string> consumers)
		{
			if (consumers == null || consumers.Count == 0) return string.Empty;

			return "\n\n" +
				"'" + logicalKey + "' is still needed by: " + string.Join("; ", consumers) + ".\n" +
				"The row falls back to 'not registered' and builds that need this key will FAIL until it is " +
				"registered again.";
		}

		/// <summary>Adds one definition's demanded keys: its actions' declared requirements + its wired signing keys.</summary>
		private static void CollectFrom(BuildDefinition definition, Dictionary<string, NeededSecret> needed)
		{
			string label = DefinitionNaming.ComposeDisplayLabel(definition.Platform.ToServerToken(), definition.DefinitionName);

			IReadOnlyList<PostBuildAction> actions = definition.PostBuildActions;
			if (actions != null)
			{
				foreach (PostBuildAction action in actions)
				{
					if (action == null) continue;

					foreach (SecretRequirement requirement in action.RequiredSecrets)
					{
						Add(needed, requirement.Key, requirement.Kind, requirement.Description,
							action.DisplayName + " on " + label);
					}
				}
			}

			// Wired signing keys - derived from the definition DATA (the committed env-key names double as the
			// logical registry keys the agent/local build resolves), never from a hardcoded key list.
			if (definition is AndroidBuildDefinition android && android.Signing.IsConfigured)
			{
				Add(needed, android.Signing.KeystorePasswordEnvOrDefault, SecretKind.String,
					"Android keystore password.", "Android signing on " + label);
				Add(needed, android.Signing.KeyAliasPasswordEnvOrDefault, SecretKind.String,
					"Android key-alias password.", "Android signing on " + label);
			}
			else if (definition is iOSBuildDefinition ios && ios.Signing.IsConfigured)
			{
				// Only the match passphrase: the signing struct also names an ASC API key env, but no shipped
				// consumer reads it FROM the signing (BuildIPA makes no Apple API call; AscUpload declares its
				// own ASC keys via RequiredSecrets above) - demanding it here would flag a secret nothing resolves.
				Add(needed, ios.Signing.MatchPasswordEnvOrDefault, SecretKind.String,
					"fastlane match repo passphrase.", "iOS signing on " + label);
			}
		}

		private static void Add(Dictionary<string, NeededSecret> needed, string key, SecretKind kind, string description,
			string consumer)
		{
			if (string.IsNullOrEmpty(key)) return;

			if (!needed.TryGetValue(key, out NeededSecret entry))
			{
				entry = new NeededSecret { Key = key, Kind = kind, Description = description };
				needed[key] = entry;
			}

			if (!entry.Consumers.Contains(consumer)) entry.Consumers.Add(consumer);
		}

		#endregion
	}
}

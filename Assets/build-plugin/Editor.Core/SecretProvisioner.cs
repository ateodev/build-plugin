using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// The SHARED provision/register logic behind every place that writes a secret and records its pointer:
	/// the create-definition wizard's signing provisioning and the Secrets UX's register/set-value dialog both
	/// go through here, so item naming, the write-confirmed-reference rule and the registry write exist exactly
	/// once (extracted from CreateDefinitionWizard). Fully provider-agnostic: everything flows through the
	/// <see cref="ISecretProvider"/> verbs (ReferenceFor / CreateOrUpdateAsync), never a provider CLI.
	/// </summary>
	public static class SecretProvisioner
	{
		#region Constants

		/// <summary>
		/// The field name used for single-value String items created by convention (see <see cref="ItemNameFor"/>):
		/// one item per logical key with one "value" field keeps the vault UI, ExistsAsync probes and registry rows
		/// 1:1. File secrets are provider documents - field-less - so this only applies to String kinds.
		/// </summary>
		public const string ValueField = "value";

		#endregion

		#region Public Methods - Provider

		/// <summary>
		/// The provider used to provision secrets. Coordinates are TEAM-level (single source, §11.7): fetched
		/// from the project's team's TeamCity params - NOT the local build-env defaults, which may point somewhere
		/// other than the team's vault. Those defaults are the fallback only when there is no ProjectConfig/team/
		/// token or the fetch fails, and that fallback warns visibly. NOTE: blocks briefly on a TeamCity fetch -
		/// call it from a user action (button/dialog open), never per IMGUI repaint.
		/// </summary>
		public static ISecretProvider ResolveTeamProvider(ProjectConfig project)
		{
			string token = BuildServerSettings.Token;
			if (project == null || string.IsNullOrEmpty(project.TeamId) || string.IsNullOrEmpty(token))
			{
				Debug.LogWarning("[Secrets] No ProjectConfig team / server token to fetch the team's provider " +
					"coordinates from - provisioning with LOCAL DEFAULT coordinates instead of the team's.");
				return SecretProviders.ForBuild();
			}

			try
			{
				TeamCityClient.ProviderCoords coords = RunSync(() => FetchTeamCoordsAsync(project, token));
				ISecretProvider provider = SecretProviders.Resolve(coords.Scheme, coords.Config, coords.Account);
				if (provider != null) return provider;

				Debug.LogWarning("[Secrets] Team '" + project.TeamId + "' declares no known provider (scheme '" +
					coords.Scheme + "') - provisioning with LOCAL DEFAULT coordinates instead of the team's.");
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[Secrets] Could not fetch team provider coordinates (" + exception.Message +
					") - provisioning with LOCAL DEFAULT coordinates instead of the team's.");
			}

			return SecretProviders.ForBuild();
		}

		#endregion

		#region Public Methods - Write / Register

		/// <summary>
		/// The conventional item name for a logical key provisioned standalone:
		/// <c>&lt;project-key&gt;_&lt;type-key&gt;</c> - kebab-case WITHIN each group, one UNDERSCORE between the
		/// two groups (e.g. STEAM_USER on project build-plugin-test -&gt; <c>build-plugin-test_steam-user</c>).
		/// The underscore is deliberate: both groups are kebab-case internally (project keys carry dashes, the
		/// key lowercases and folds '_' to '-'), so a dash join would make the project/key boundary
		/// unrecoverable - and the split MUST stay machine-parseable, because these names end up inside stored
		/// references that <see cref="TryGetWriteCoordinates"/> later round-trips back into write coordinates.
		/// The project-key prefix namespaces the item inside the team's SHARED vault; one item per logical key
		/// keeps registry rows, probes and the vault UI 1:1. The wizard's multi-field Android signing item takes
		/// its NAME from here too (only its multi-field layout is its own deliberate choice), and so does the
		/// per-project vcs record (<see cref="VcsRecordNameFor"/>) - ONE pattern for every project-bound item.
		/// Only team-global/reusable items (unity-licenses, shared bot credentials) keep bare descriptive names:
		/// a fake project prefix on a cross-project item would lie about its scope.
		/// </summary>
		public static string ItemNameFor(ProjectConfig project, string logicalKey)
		{
			return ItemNameFor(project != null ? project.ProjectKey : null, logicalKey);
		}

		/// <summary>
		/// String-key overload of <see cref="ItemNameFor(ProjectConfig,string)"/> for callers that run BEFORE a
		/// ProjectConfig asset exists (the project-setup wizard names vault items from the typed project key).
		/// </summary>
		public static string ItemNameFor(string projectKey, string logicalKey)
		{
			if (string.IsNullOrEmpty(projectKey)) projectKey = "project";

			// The project key is kebab-folded too: the single underscore separator is only unambiguous when
			// NEITHER group can contain one.
			return projectKey.ToLowerInvariant().Replace('_', '-') + "_" +
				(logicalKey ?? "").ToLowerInvariant().Replace('_', '-');
		}

		/// <summary>
		/// The per-project VCS record's item name: <c>&lt;project-key&gt;_vcs</c> (§11.7) - the record the agent
		/// resolves {repoUrl, vcsType, credentialName, cmServer?} from, pre-checkout. Derived through
		/// <see cref="ItemNameFor(string,string)"/> ("vcs" is just the type key) so the wizard, the panel and the
		/// agent scripts all compose the IDENTICAL name from the project key alone - the name exists in one place.
		/// </summary>
		public static string VcsRecordNameFor(string projectKey)
		{
			return ItemNameFor(projectKey, "vcs");
		}

		/// <summary>
		/// Writes a secret value through the provider (off the Editor main thread - see <see cref="RunSync{T}"/>)
		/// and returns the WRITE-CONFIRMED reference; falls back to <see cref="ISecretProvider.ReferenceFor"/>
		/// when the provider confirmed the write but returned an empty pointer. THROWS on a null provider or a
		/// failed write - the caller decides whether to degrade (wizard) or surface the error (dialog).
		/// </summary>
		public static SecretRef WriteSecret(ISecretProvider provider, string item, string field, SecretValue value)
		{
			if (provider == null)
			{
				throw new Exception("No secret provider is resolvable - cannot write '" + item + "/" + field + "'. " +
					"Provider coordinates come from the team's TeamCity params (unitybuild.provider.*) or, locally, " +
					"the UNITYBUILD_PROVIDER_* environment.");
			}

			SecretKind kind = value != null && value.IsFile ? SecretKind.File : SecretKind.String;
			SecretRef written = RunSync(() => provider.CreateOrUpdateAsync(item, field, value));

			return string.IsNullOrEmpty(written.Reference) ? provider.ReferenceFor(item, field, kind) : written;
		}

		/// <summary>
		/// Records a logical-key -&gt; reference mapping in the project's committed secret registry (add, or
		/// replace when <paramref name="overwriteExisting"/> - the register dialog's re-point; the wizard passes
		/// false so re-running never clobbers an entry). UsedBy labels merge on overwrite. Saves the asset.
		/// </summary>
		public static void RegisterSecret(ProjectConfig project, string logicalKey, string description, SecretKind kind,
			string reference, string[] usedBy, bool overwriteExisting)
		{
			if (project == null || string.IsNullOrEmpty(logicalKey)) return;

			List<SecretDeclaration> registry = GetRegistry(project);
			if (registry == null) return;

			List<string> labels = new List<string>(usedBy ?? Array.Empty<string>());
			for (int i = 0; i < registry.Count; i++)
			{
				SecretDeclaration existing = registry[i];
				if (existing == null || existing.LogicalKey != logicalKey) continue;
				if (!overwriteExisting) return; // already registered - the wizard's idempotent re-run path

				foreach (string label in existing.UsedBy ?? Array.Empty<string>())
				{
					if (!string.IsNullOrEmpty(label) && !labels.Contains(label)) labels.Add(label);
				}

				registry[i] = new SecretDeclaration(logicalKey, description, kind, reference, labels.ToArray());
				Save(project);
				return;
			}

			registry.Add(new SecretDeclaration(logicalKey, description, kind, reference, labels.ToArray()));
			Save(project);
		}

		/// <summary>
		/// Removes a registry ENTRY by logical key - the vault item behind its reference is deliberately left
		/// untouched (removal un-declares the mapping; it never destroys secret material). Returns whether an
		/// entry was removed. Saves the asset.
		/// </summary>
		public static bool RemoveSecret(ProjectConfig project, string logicalKey)
		{
			if (project == null || string.IsNullOrEmpty(logicalKey)) return false;

			List<SecretDeclaration> registry = GetRegistry(project);
			if (registry == null) return false;

			int removed = registry.RemoveAll(entry => entry != null && entry.LogicalKey == logicalKey);
			if (removed > 0) Save(project);

			return removed > 0;
		}

		/// <summary>
		/// Recovers the (item, field) write coordinates of an EXISTING registry reference so "Set value" can
		/// write through <see cref="ISecretProvider.CreateOrUpdateAsync"/> - the contract has no write-by-reference
		/// verb. Provider-agnostic by construction: the candidate split (last segment = field, previous = item;
		/// File documents are field-less so last = item) is only accepted when the provider's own
		/// <see cref="ISecretProvider.ReferenceFor"/> ROUND-TRIPS to the exact stored reference - the provider
		/// stays the sole authority on its reference shape, and a foreign-convention (or foreign-vault) pointer
		/// yields false instead of a write landing somewhere unintended.
		/// </summary>
		public static bool TryGetWriteCoordinates(ISecretProvider provider, SecretDeclaration declaration,
			out string item, out string field)
		{
			item = null;
			field = null;
			if (provider == null || declaration == null || string.IsNullOrEmpty(declaration.Reference)) return false;

			string reference = declaration.Reference;
			int schemeEnd = reference.IndexOf("://", StringComparison.Ordinal);
			if (schemeEnd <= 0) return false;

			string[] segments = reference.Substring(schemeEnd + 3).Split('/');
			if (segments.Length < 2) return false;

			if (declaration.Kind == SecretKind.File)
			{
				item = segments[segments.Length - 1];
				field = ValueField; // documents ignore the field; passed so CreateOrUpdateAsync has a non-null arg
				return ReferencesMatch(provider, item, field, SecretKind.File, reference);
			}

			field = segments[segments.Length - 1];
			item = segments.Length >= 3 ? segments[segments.Length - 2] : null;
			return !string.IsNullOrEmpty(item) && ReferencesMatch(provider, item, field, SecretKind.String, reference);
		}

		/// <summary>The project's single <see cref="ProjectConfig"/> asset, or null before onboarding.</summary>
		public static ProjectConfig FindProjectConfig()
		{
			string[] guids = AssetDatabase.FindAssets("t:" + nameof(ProjectConfig));
			return guids.Length > 0
				? AssetDatabase.LoadAssetAtPath<ProjectConfig>(AssetDatabase.GUIDToAssetPath(guids[0]))
				: null;
		}

		#endregion

		#region Private Methods

		private static bool ReferencesMatch(ISecretProvider provider, string item, string field, SecretKind kind,
			string reference)
		{
			return string.Equals(provider.ReferenceFor(item, field, kind).Reference, reference, StringComparison.Ordinal);
		}

		private static async Task<TeamCityClient.ProviderCoords> FetchTeamCoordsAsync(ProjectConfig project, string token)
		{
			// The server URL is a per-machine setting (environment fact), not a ProjectConfig field.
			using (TeamCityClient client = new TeamCityClient(BuildServerSettings.ServerBaseUrl, token))
			{
				return await client.GetTeamProviderCoordsAsync(project.TeamId);
			}
		}

		private static void Save(ProjectConfig project)
		{
			EditorUtility.SetDirty(project);
			AssetDatabase.SaveAssetIfDirty(project);
		}

		private static List<SecretDeclaration> GetRegistry(ProjectConfig project)
		{
			FieldInfo field = typeof(ProjectConfig).GetField("_secretRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
			return field?.GetValue(project) as List<SecretDeclaration>;
		}

		/// <summary>
		/// Run an async provider call to completion OFF the Editor main thread (same rationale as the UI-side
		/// WizardShell.RunSync, which this cannot reference - Editor.Core sits below Editor.UI): awaiting the
		/// provider chain via GetResult() directly on the main thread deadlocks on Unity's SynchronizationContext;
		/// Task.Run gives it a thread-pool context instead. Bounded by the provider CLI's own timeout.
		/// </summary>
		private static T RunSync<T>(Func<Task<T>> call)
		{
			return Task.Run(call).GetAwaiter().GetResult();
		}

		#endregion
	}
}

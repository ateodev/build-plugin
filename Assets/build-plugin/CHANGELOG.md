# Changelog

## [0.9.0] - 2026-07-02
### Added (build notifications - scheme-dispatched, Slack now, Discord-ready)
- **`ProjectConfig` gains `Notification Target`** (replaces the Slack-specific channel field - hard rename, no migration shim): a scheme-tagged target like `slack:C0123ABC456`; empty = no notifications. Delivered server-side by the executor scripts (the plugin stays delivery-ignorant); the scheme picks the transport, so `discord:<provider-ref>` and future channels slot in agent-side with zero plugin changes.
- **Per-definition override**: a `BuildDefinition` can set its own `Notification Target Override` (e.g. a nightly smoke posting to a quieter channel); empty inherits the project target.
- **Per-build mute**: a "Mute notifications (this build)" toggle in the trigger section sends `unitybuild.notify=false` (recorded on the build; suppresses success AND failure messages).
- Authoring-time validation (`NotificationTarget` helper in Data): untagged targets are invalid - the wizard and Configure tab flag them with the expected form; known schemes today: `slack`, `discord`.
- Wizard: the Slack field became a scheme-neutral "Notifications" section.

## [0.8.2] - 2026-07-02
### Added
- **Select an existing checkout credential** (13.3): the project-setup wizard now lists the team vault's existing `cred-*` items in a dropdown (names shown without the prefix) next to "add new" - reusing a credential skips key generation/paste/verify entirely. Backed by a new provider verb `ListItemsAsync(prefix)` on `ISecretProvider` (op impl via `op item list`; an empty result just hides the select-existing mode).

## [0.8.1] - 2026-07-02
### Fixed
- **A definition's Default Branch is now actually sent** — triggering a server build only sent `unitybuild.vcs.ref` when the manual Changeset override was typed, so the definition's own branch was silently ignored (empty always meant "repo default"). The trigger now sends the definition's Default Branch (with `refType: branch`) when no override is given.
- **Canonical branch form = bare name** ("main", empty = repo default): `origin/`-prefixed values are normalized away at trigger and asset-write time (the agent resolves bare names to `origin/<branch>`); the wizard's branch dropdown no longer offers `origin/`-prefixed duplicates or the bogus `origin` entry, and the hardcoded `main` fallback is gone.
- **Token UX**: a Clear button next to the TeamCity token in Settings; the project-setup wizard now says "Set your TeamCity access token in the Build Panel's Settings to load teams" (with a Reload button) instead of silently showing an empty Team dropdown.

## [0.8.0] - 2026-07-02
### Fixed (debt sprint - full-project review follow-up)
- **File secrets round-trip**: `CreateOrUpdateAsync(File)` stored a 1Password document but returned a field-style `op://vault/item/field` reference that could never be read back (and `ExistsAsync` reported it absent). File secrets now use the field-less document reference `op://<vault>/<item>` end-to-end (create, resolve, exists), matching the agent-side convention; stored documents no longer leak the random staging file name.
- **Live builds were invisible in the Builds list**: TeamCity's default build locator returns finished, default-branch builds only, so a just-triggered (queued/running) build never appeared and the focused poll never engaged. The history locator now sends `defaultFilter:false,state:any`.
- **Locator + artifact-URL escaping**: free-text definition names containing `( ) , :` corrupted the REST locator (server error); values are now `$base64`-wrapped when needed. Artifact download paths are URL-escaped per segment, and downloads stream to disk instead of buffering whole artifacts in editor memory.
- **Trigger dedupe (5.6)**: "Build on Server" now checks the in-flight queue for an identical (project, definition, ref) build and skips the duplicate POST ("already queued as #N").
- **Secret hygiene**: materialized file secrets (`%TEMP%/ateo-secret-*`) are wiped after the post-build-action pipeline; `ServerDeploy` restricts the SSH key file (icacls/chmod 600) so OpenSSH accepts it; `FtpUpload` passes credentials via a transient netrc file instead of argv; `steamcmd`/BuildPatchTool argv constraints documented in place.
- **Provider-agnosticism sweep completed**: the create-definition wizard no longer hand-builds an `op://` reference in its no-provider fallback (fails loudly instead) and provisions secrets with the TEAM's provider coordinates (fetched from TeamCity team params like the setup wizard) instead of local defaults; remaining 1Password wording removed from agnostic messages.
- `OpCli` default path derived from `%LOCALAPPDATA%` (was hardcoded to this build server's user profile - broke on dev machines).
- External tools launched by post-build actions now time out (default 120 min) instead of hanging a build forever.
- Settings view: stale "wizard not built yet" guidance replaced (opens the Project Setup Wizard); the TeamCity token is only written to EditorPrefs on an actual edit.
- Download status line labels builds by identity (version/build name), not the TeamCity #number.

### Removed
- Dead code: unused `OpItem`/`OpField` DTOs, a dead client-side definition re-filter, unused `project` parameters on provider-resolution helpers. (The `BuildStep` pre/post framework stays - it becomes the `IPreprocessBuildWithReport`/`IPostprocessBuildWithReport` hook mechanism, design pending.)

### Docs
- README rewritten for the v2 package (install with `?path=`, wizard-first onboarding, concepts, CI entry point, release process).

## [0.7.4] - 2026-07-01
### Fixed
- **A definition's Builds list now shows only ITS builds** — it was filtered by project only, so a shared executor's builds from *other* definitions appeared (a brand-new definition showed 10 unrelated builds). Now filtered by `unitybuild.definition` too.

## [0.7.3] - 2026-07-01
### Fixed
- **"Build On Server" hit a stale server URL** — the panel cached its base URL the first time (defaulting to `build.ateonet.work` when no project was loaded yet) and never updated it, so triggering a build against a project configured for `http://localhost:8111` failed with "An error occurred while sending the request". The base URL now always tracks the loaded ProjectConfig's server URL.

## [0.7.2] - 2026-07-01
### Fixed
- **VCS record pointers stored as plain text, not concealed** — `repoUrl` / `vcsType` / `credentialName` are non-secret, so they no longer write as masked `password` fields in 1Password. `CreateOrUpdateAsync` / `OpCli.CreateOrEditItemAsync` gained a `concealed` flag (default true for real secrets); the wizard's record writes pass `concealed: false`.

## [0.7.1] - 2026-07-01
### Fixed
- **Editing the Server URL re-fetches teams** — changing it (e.g. to `http://localhost:8111`) now reloads the team dropdown on commit, instead of leaving the list stale from the value at open.

## [0.7.0] - 2026-07-01
### Changed (project-setup wizard — UX sign-off pass)
- **No secret-provider interaction on open** — the wizard no longer touches the provider (no 1Password auth prompt) when it opens. Teams are fetched from TeamCity only; the provider is first touched when you **pick a team** (coords + licenses load then).
- **Secrets-provider section is empty until a team is chosen**, then filled from that team's TeamCity params. Labels are provider-agnostic (`Scheme` / `Config` / `Account`) — no 1Password-specific wording.
- **Removed the "Refresh teams + coords" button** — teams auto-fetch on open (they don't change while the window's open).
- **Removed the project-key info box** (internal detail the user doesn't need) and the **"Validate" button** (its checks weren't required — server reachability shows via the team list, provider via the license list).
- **Unity version removed from the wizard** — rarely pinned; left blank so the agent reads `ProjectVersion.txt`. Pin it on the ProjectConfig asset only if a build must differ.
- **License dropdown shows capitalized names** ("Ateo") while storing the lowercase key ("ateo").
- **VCS type auto-detected** from the checkout (git remote → Git; `.plastic` → Plastic). Full Plastic-shaped fields are still WIP (task #40, with the Plastic E2E #19).

## [0.6.7] - 2026-07-01
### Fixed
- **Project key formats truly live** — `[OnValueChanged]` only re-formatted the *backing* field, so the change wasn't visible until focus-loss. Now custom-drawn: the input **event** is transformed as you type (space → `-`, uppercase → lowercase, illegal chars blocked), so the field's own buffer is always a valid key; a full normalize (`--` collapse / trim / paste) runs on focus-loss.

## [0.6.6] - 2026-07-01
### Changed (project-setup wizard)
- **Project key: pre-filled *and* live-formatted** — re-suggested from the Unity product name (itself normalized), and the field now re-normalizes on every edit via `[OnValueChanged]` → lowercase, whitespace/illegal chars become `-`, `--` collapsed, so it can only ever hold a valid `[a-z0-9-]` key.

## [0.6.5] - 2026-07-01
### Fixed (project-setup wizard UX)
- **Project key no longer pre-filled** — was silently seeded from the Unity product name (misleading); now blank, typed deliberately.
- **License is a normal dropdown** — dropped the `AppendNextDrawer` hybrid text+dropdown; matches the Team/VCS dropdowns. Enumerates from the provider's `unity-licenses` item.
- **Team dropdown auto-fetches on open** — teams + coords are pulled up front (no-op without a token / if the server is unreachable), instead of only after clicking "Refresh".

## [0.6.4] - 2026-07-01
### Removed
- **"Executor fallback" setting** — the manual executor-id field is gone; executor resolution now relies solely on auto-discovery (the platform→executor map). `BuildServerSettings.BuildTypeId` removed; `ResolveExecutor` returns empty (an actionable "no executor for platform" message) when discovery finds none.

## [0.6.3] - 2026-07-01
### Changed
- **Completed the provider-agnostic sweep** — removed the remaining 1Password-specific shortcuts in the wizards. `ISecretProvider` gained `ReferenceFor(item, field)` so consumers never hand-assemble an `op://` string; the create-definition secret fallback reference, the project-setup provider **reachability probe** (`ExistsAsync` + `ReferenceFor`, no scheme gate), and the **license-registry read** (`ReadRecordAsync` instead of shelling to `op item get`) now all go through the provider interface. Adding a second provider requires no wizard changes.

## [0.6.2] - 2026-07-01
### Changed
- **Provider-presence check is now fully provider-agnostic** — `ISecretProvider` gained `IsAvailable()` + `UnavailableHint`; the Build Panel's "provider not set up" banner asks whichever provider(s) a project actually references (from its scheme-tagged secret registry), instead of hardcoding `OpCli`. Adding a provider (OpenBao, …) surfaces its own unavailability warning for free — no UI change. (Removes the deferred 1Password-specific shortcut.)

## [0.6.1] - 2026-07-01
### Added
- **Deploy-key host guidance + Verify** (#29) — the project-setup wizard shows host-specific instructions for adding the generated key (GitHub / GitLab / Bitbucket, read-only) and a **"Verify deploy key"** button that test-clones (`git ls-remote` over SSH with the generated key) to confirm the key works before you leave the wizard. No host PAT required; auto-add via host API intentionally deferred.

## [0.6.0] - 2026-07-01
Self-service onboarding — ProjectConfig slimmed, nothing the server owns is typed.

### Added
- **Team dropdown** — the project-setup wizard's Team field is a live `ValueDropdown` of the server's teams (top-level TeamCity projects, `TeamCityClient.ListTeamsAsync`); a dev picks a valid team and never has to ask the admin what to enter.
- **Live provider-coords fetch** — coords come from the selected team's TeamCity params (single source, `GetTeamProviderCoordsAsync`), fetched via the panel's token; shown read-only, never typed or committed.

### Changed
- **ProjectConfig slimmed** — dropped `_repoUrl`, `_vcsCredentialName`, and the four `_secretProvider*` fields. Repo URL + checkout credential now come from the provider `vcs-<key>` record (server-authoritative); provider coords from TeamCity team params. ProjectConfig is now just "who am I + how do I build + which secrets do I reference."
- **Build-time secret coords via environment** — `BuildRunner`/`SecretsView` read coords from `UNITYBUILD_PROVIDER_*` env (server) / defaults (local), no longer from ProjectConfig.

## [0.5.0] - 2026-07-01
Editor UX + local-build reliability.

### Added
- **op CLI presence banner** — the Build Panel shows an actionable error at the top when a 1Password-backed project is opened without the `op` CLI installed (nothing — build or wizard — can run without it).
- **Copy-public-key button** — the project-setup wizard's generated deploy-key field is now selectable (no longer greyed `[ReadOnly]`) and has a "Copy public key to clipboard" button.

### Fixed
- **Local (in-Editor) builds no longer deadlock** — post-build actions and secret resolution await via a helper that runs off Unity's SynchronizationContext in the Editor (batch/CI behaviour unchanged).

## [0.4.0] - 2026-07-01
The full platform matrix + the `<team>/<project>/<target>` checkout contract.

### Added
- **Full `BuildPlatform` matrix (20 tokens)** — adds `WindowsServer`, `MacServer`, `UWP`, `tvOS`, `VisionOS`, and the console targets (`Switch`, `PS4`, `PS5`, `XboxOne`, `XboxGDKOne`, `XboxSeries`) on top of the existing desktop/mobile/web/Linux-server set. **18 authorable definition asset types**; dedicated **Server** is its own platform (Standalone* + `StandaloneBuildSubtarget.Server`). `Windows32`/`LinuxSim` are map-only; consoles are authorable but partner-SDK (fail-and-notify, never auto-installed).
- **`unitybuild.target` build param** — the panel sends the leaf's platform token; it routes the build (capability lists) and is the `<build-target>` segment of the checkout/output path.

### Changed
- **Platform tokens shortened** — `WinStandalone`→`Windows`, `MacStandalone`→`Mac`, `LinuxStandalone`→`Linux` (short for Windows `MAX_PATH`); the `BuildPlatform` enum identifier IS the token.
- `ServerBuildDefinition` → `LinuxServerBuildDefinition` (Windows/macOS dedicated server are now first-class siblings).
- `BuildRunner` sets the standalone subtarget (Player/Server) on the built-in path (profile builds carry it); the no-profile path resolves `-buildTarget` from the token on the agent.

## [0.3.0] - 2026-07-01
Self-service checkout + authoring refinements on top of v2. **Trigger contract change:** the build parameter is now `unitybuild.project` (a *project key*), replacing `unitybuild.game`/the old "slug"; update any custom triggers.

### Added
- **Per-definition Unity version override** — a `BuildDefinition` can pin its own *Unity Version* to target a different editor than the rest of the project. The agent resolves the version build definition → Project Config → `ProjectSettings/ProjectVersion.txt`; empty inherits.
- **Self-service checkout** — the project-setup wizard writes a `vcs-<project-key>` record (repo URL, VCS type, credential name) to the secret provider, so the server resolves the repo + credentials from the project key alone (CI-ready; no per-project server admin).
- **Build-name identity** — an optional free-text name appended to a build's identity and its §12.2 on-disk folder (sanitized).

### Changed
- **Project key rename** — `unitybuild.game` / "slug" → `unitybuild.project`, enforced lowercase `[a-z0-9-]`, across the panel, wizard and CI contract.
- **Secret provider contract v2** — scheme-tagged factory, a `ReadRecord` verb, and *mandatory* write (a provider is the client→server channel).
- **One §12.2 on-disk layout** shared by local output and server-build downloads (`Builds/<definition>/<version>[_<buildNumber>]/`).
- **Build Panel** — server links open on the configured origin (not TeamCity's root URL); bottom-pinned dismissable status bar; responsive, deduplicated build rows.
- **Project-setup wizard** auto-detects the git remote as SSH (checkout is SSH-only).

### Fixed
- Editor freezes on the project-setup wizard — the SSH keypair prompt hang and a sync-over-async deadlock on the provider write.
- `op item create` failing with "invalid JSON in piped input" (the op CLI no longer has stdin redirected).
- Credentials stored under the bare name instead of the `cred-<name>` contract name.
- Executor discovery now splits the comma-separated platform set (picked the wrong executor before).
- Build Panel download fetched directory entries (404); now only artifact files.

## [0.2.0] - 2026-06-29
v2 — the plugin-driven re-architecture. **Breaking:** the data model is now polymorphic, which invalidates flat v0.1.x `BuildDefinition` assets (re-create them via the wizard).

### Added
- **Polymorphic build definitions** — `abstract BuildDefinition` + per-target subclasses (`AndroidBuildDefinition`, `iOSBuildDefinition`, `WindowsBuildDefinition`, `MacOSBuildDefinition`, `LinuxBuildDefinition`, `WebGLBuildDefinition`, `ServerBuildDefinition`); the type *is* the platform, each declares its `OutputKind`. Building *from* a Unity 6 Build Profile now works.
- **Post-build action pipeline** — typed `PostBuildAction<TDef>` with declarative artifact-flow dependencies (`Consumes`/`Produces`), run-location + host-capability gates, and per-build skip. Shipped catalog: `BuildIPA`, `AscUpload`, `GooglePlayUpload`, `ExtractApk`, `AdbInstall`, `SteamUpload`, `EpicUpload`, `ItchUpload`, `Notarize`, `FtpUpload`, `ServerDeploy`.
- **Secret provider abstraction** — `ISecretProvider` with scheme-tagged `op://…` references + a 1Password implementation; values resolved just-in-time at build, never committed.
- **Build Panel** (Odin) — the control plane: target-grouped sidebar, Build/Configure tabs, Activity (team in-flight), Secrets registry, history, and local/server triggering.
- **Authoring wizards** — first-run project-setup + create-definition (floating windows; keystore generation, credential/license registries).
- Split into four Editor-only assemblies (`Data` / `Editor.Core` / `Editor.UI` [Odin-gated] / reserved fallback).

### Changed
- Build number is the committed `PlayerSettings` value (no CI counter).
- `BuildStep` (pre/post) is superseded by the `PostBuildAction` pipeline.

## [0.1.1] - 2026-06-27
### Fixed
- Build Panel: Refresh Builds queried an empty buildTypeId when no manual executor was set; now queries the auto-discovered executors (+ manual fallback).

## [0.1.0] - 2026-06-27
v1 skeleton.

### Added
- `BuildDefinition` / `ProjectConfig` ScriptableObjects (authored under `Assets/BuildConfigs/`).
- `BuildRunner.Run` — unified headless entry point (`-executeMethod Ateo.Build.BuildRunner.Run -buildDefinition <name>`); same path for CI and local builds.
- `VersionStamp` — version overrides from `BUILD_VERSION_NAME` / `ANDROID_VERSION_CODE` / `IOS_BUILD_NUMBER` (closes the version-name gap from the param contract).
- `BuildStep` framework (pre/post step ScriptableObjects) for project-defined extensibility.
- Build-method shim — invoke a game's own static headless builder (e.g. `AndroidBuilder.BuildFromCommandLine`) unchanged.
- Android signing applied from env references (passwords never in the asset).
- `BuildResult` JSON output for CI.

### Not yet implemented
- Building *from* a Unity 6 Build Profile (the built-in path currently uses the scene list).
- In-Editor Build Panel and built-in `BuildStep` implementations (v2).
- iOS / standalone output-path specifics beyond Android AAB/APK.

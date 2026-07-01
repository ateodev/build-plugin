# Changelog

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

## [0.1.1] - 2026-06-27
### Fixed
- Build Panel: Refresh Builds queried an empty buildTypeId when no manual executor was set; now queries the auto-discovered executors (+ manual fallback).

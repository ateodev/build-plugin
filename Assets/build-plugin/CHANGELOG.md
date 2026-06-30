# Changelog

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

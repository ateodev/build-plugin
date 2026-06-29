# Changelog

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

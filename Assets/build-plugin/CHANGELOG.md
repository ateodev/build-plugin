# Changelog

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
</content>

## [0.1.1] - 2026-06-27
### Fixed
- Build Panel: Refresh Builds queried an empty buildTypeId when no manual executor was set; now queries the auto-discovered executors (+ manual fallback).

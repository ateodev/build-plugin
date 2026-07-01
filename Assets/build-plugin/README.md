# Ateo Build (`com.ateo.build`)

Author your builds as committed assets in the game repo, then build locally or trigger the
Ateo build server -- all from the Unity editor. Build definitions are ScriptableObjects under
`Assets/BuildConfigs/`; the server executes exactly what the repo says (same code path as a
local build), and artifacts, history and in-flight activity show up in the Build Panel.

Design of record: `build-server/docs/build-plugin-architecture.md` (in the `build-server` repo).

## Install

Add via UPM git URL (Package Manager > `+` > *Install package from git URL...*), or directly in
`Packages/manifest.json`:

```json
"com.ateo.build": "https://github.com/ateodev/build-plugin.git?path=/Assets/build-plugin#v0.8.0"
```

- **Pin a version tag** (e.g. `#v0.8.0`) for reproducible builds, or use the moving `#latest`
  tag to follow releases. See `CHANGELOG.md` for what each version ships.
- The `?path=/Assets/build-plugin` part is **required**: the repo is an openable Unity project
  and the package lives in that subfolder.

### Requirements

- **Unity 6+** (`6000.0` or newer).
- **Odin Inspector** for the Build Panel and wizards. The `Editor.UI` assembly is gated behind
  `ODIN_INSPECTOR`; without Odin the data model and the headless build entry point still work,
  you just get no panel/wizard UI.
- The team's secret-provider CLI for local builds and onboarding (1Password `op` by default).

## Getting started

1. Open **Window > Build Panel**.
2. First run offers the **Project Setup Wizard**: pick your team from the dropdown, confirm the
   project key, VCS (auto-detected) + a deploy-key checkout credential (generated for you, with
   host-specific instructions and a verify button), Unity license, and Slack channel. *Create
   ProjectConfig* provisions everything -- no server-admin step.
3. In the panel's **Settings**, paste your TeamCity access token once (machine-local, never
   committed).
4. Click **+ Add Build Definition** and follow the wizard. This creates one target-typed asset
   (e.g. `AndroidBuildDefinition`) under `Assets/BuildConfigs/`. Commit it.
5. Select the definition and hit **Build Local** or **Build On Server**. Server builds stream
   status into the panel; finished artifacts download into the same on-disk layout local builds
   use (`Builds/<definition>/<version>[_<buildNumber>]/`).

## Concepts

- **BuildDefinition** -- one asset per buildable thing; the asset's concrete type *is* the target
  platform (Android, iOS, Windows, ... -- 18 authorable types incl. dedicated servers and
  consoles). Wraps a Unity 6 Build Profile (preferred) and adds output naming, a default branch,
  per-definition Unity version override, and the post-build pipeline.
- **ProjectConfig** -- the one per-project asset: project key, team, server URL, VCS kind, Unity
  version/license, Slack channel, and the secret registry. The project key is the join key: the
  server resolves repo, credentials, secrets and license from it alone.
- **Post-build actions** -- a typed pipeline run on the built artifact (BuildIPA, AscUpload,
  GooglePlayUpload, SteamUpload, EpicUpload, ItchUpload, ExtractApk, AdbInstall, Notarize,
  FtpUpload, ServerDeploy). Each action declares what it *Consumes* and *Produces*, so the chain
  validates itself; run-location and host-capability gates decide where it executes.
- **Secrets** -- assets and the registry only ever hold scheme-tagged *references*
  (e.g. `op://...`); the values live in your team's secret provider (1Password by default,
  pluggable via `ISecretProvider`) and are resolved just-in-time at build. **No secret is ever
  committed.**

## CI entry point (reference)

The server runs the same headless entry point you can run yourself:

```
Unity -batchmode -nographics -quit -projectPath <proj> \
      -executeMethod Ateo.Build.BuildRunner.Run \
      -buildDefinition "<definition name>" \
      [-buildResult "<path>.json"]
```

Unity cannot switch build targets mid-batch, so the launch must already carry
`-activeBuildProfile <profile path>` (profile-based definitions) or `-buildTarget <target>`;
`BuildRunner` validates and fails with an actionable message otherwise. The executor derives
both from the definition -- you only need this when wiring custom CI.

## Releasing (maintainers)

1. Bump `version` in `package.json` and add a `CHANGELOG.md` entry.
2. Tag `vX.Y.Z` and **force-move the `latest` tag** to the same commit; push both.
3. UPM locks git dependencies: consumers do not auto-update. To pick up a release, re-pin the
   tag in `manifest.json`, re-add via the Package Manager UI, or clear the package's entry in
   `packages-lock.json`.

## Version / status

The current version lives in `package.json`; what shipped when (including breaking changes and
migration notes) is in `CHANGELOG.md`.

# Ateo Build (`com.ateo.build`)

In-repo build definitions and a unified headless build entry point for the Ateo Unity build server.

**Core idea:** build configuration is versioned **data living in the game repo**, authored in the Editor. CI becomes a dumb executor — it passes a definition *name* and this package loads, applies, and builds it. The same code path runs locally and on CI (local/CI parity). See the design of record: `build-server/docs/build-plugin-architecture.md`.

## Install

UPM git URL, pinned per project (in `Packages/manifest.json`):

```json
"com.ateo.build": "git+https://github.com/ateodev/<repo>.git#0.1.0"
```

## Authoring

Create assets under `Assets/BuildConfigs/`:

- **Project Config** (`Build/Project Config`) — one per project. Game token (the join key the server resolves repo/secrets/license from), VCS info, team, server URL.
- **Build Definition** (`Build/Build Definition`) — one per buildable thing (e.g. "Android-AAB-Release"). Platform + output format, scenes/defines (or a Unity 6 Build Profile), output naming, signing *references* (alias + env-var names — never the secret), ordered pre/post steps, default branch, and an optional build-method shim.

Secrets are never in the asset: a definition names the env vars that will hold the passwords; CI injects them agent-side, local devs export them from a gitignored override.

## CI entry point

```
Unity -batchmode -nographics -quit -projectPath <proj> \
      -executeMethod Ateo.Build.BuildRunner.Run \
      -buildDefinition "Android-AAB-Release" \
      [-buildResult "<path>.json"]
```

`BuildRunner.Run` loads the named definition → switches target → layers defines → stamps version
(`BUILD_VERSION_NAME` / `ANDROID_VERSION_CODE` / `IOS_BUILD_NUMBER`) → applies signing → runs pre-steps →
builds (built-in **or** the game's own method via the shim) → runs post-steps → writes a JSON
`BuildResult` → sets the process exit code.

## Naming

The package id and namespace carry the org (`com.ateo.build`, `Ateo.Build`); **type, method, and file
names are de-branded** (`BuildDefinition`, `BuildRunner.Run`, not `Ateo*`).

## Status — v1 (0.1.0)

Data model + unified entry point + version stamping + steps framework + named-method shim. **Not yet
wired:** building *from* a Unity 6 Build Profile (v1 built-in path uses the scene list); the in-Editor
Build Panel and built-in `BuildStep` implementations are v2. Needs a compile pass in a Unity 6 project
(which also generates the `.meta` files to commit for stable GUIDs).
</content>

# Contributing to SeaPowerUX

Notes for anyone building on or changing the pack. If you just want to install and play, see
the README.

## Layout

The pack is a shared library plus independent plugins, not one mod:

| Project | Role |
|---|---|
| `SeaPowerUX.Core` | Shared library. **Not** a BepInEx plugin — no `[BepInPlugin]`. Deploys to its own `BepInEx/plugins/SeaPowerUX.Core/` folder so there is exactly one loaded copy with one shared static state. |
| `SeaPowerUX.TacticalPiP` | Plugin. Picture-in-picture. |
| `SeaPowerUX.FlightDeck` | Plugin. Air Boss. |
| `SeaPowerUX.WindowMemory` | Plugin. Window position memory. |
| `SeaPowerUX.FormationManager` | Plugin. Inert placeholder. |

Every plugin references the deployed `SeaPowerUX.Core.dll` by path with `<Private>False</Private>`
(it is not copied into each plugin's output), and each carries its own `Harmony` instance so it
patches and unpatches independently. Plugin GUIDs follow `com.moseysauce.seapowerux.<name>`.

## Building

Core must be built first — the plugins resolve its DLL from the deployed path.

```
scripts/deploy-local.sh
```

builds Core, then every plugin, and installs them into a Sea Power directory on this machine
(auto-detected in the usual Steam locations, or set `SEA_POWER_GAME_DIR`). To build one project
by hand:

```
dotnet build SeaPowerUX.Core/SeaPowerUX.Core.csproj -c Release -p:GameDir="<game dir>"
dotnet build SeaPowerUX.TacticalPiP/SeaPowerUX.TacticalPiP.csproj        -c Release -p:GameDir="<game dir>"
```

Each project's post-build step copies its output into `GameDir`, so `dotnet build` installs on
its own. `scripts/deploy-steam.sh` does the same over SSH to a separate test box — a personal
setup; nothing in the project requires it.

## Two hard constraints

These are not style preferences. Each one crashes the game natively and uncatchably from C# —
you get a hard process exit and, usually, an empty `BepInEx/LogOutput.log`.

- **Never Harmony-patch a class deriving from `Behavior<T>` or `TriggerAction<T>`.** IL-detouring
  these Noesis/Blend base types takes down the Mono runtime with no recovery. *Calling, reading,
  and writing their plain members is fine* — the pack does this throughout. Only patching is
  fatal. (`MapDragger` confirmed this the hard way.)

- **Never touch Noesis types from a plugin's `Awake()`.** Forcing a SWIG native-backed Noesis
  type to initialise before Noesis itself is ready hard-crashes the process. `typeof()` and
  `AccessTools` reflection are safe; `harmony.Patch()` is not, because it forces static
  initialisation of the target. Defer any Noesis work to a runner gated on a live `NoesisView`
  — this is what `Core/Foundation/WindowLifecycle.cs` does.

## Dead ends, so they aren't re-tried

- **XAML injection does not work on this build.** Noesis parses and caches the game's XAML
  before any BepInEx plugin's `Awake()` runs, so a replaced XAML asset is never re-read. This was
  proven with a probe that set literal `X`/`Y` on a window's XAML and saw no effect. Extend
  native panels by opening the game's own windows and binding to its view models, not by editing
  XAML. See `CHANGELOG.md` for the full history.

## Compatibility checks

Each plugin probes the specific game members it uses at startup via
`Core/Foundation/PluginSupport.cs` (`GameApiValidator`). A probe must match the member's declared
kind — a property probed as a field reports a false incompatibility and disables the plugin. The
declared kind can be confirmed offline against `Seapower-Scripts.dll` with
`System.Reflection.Metadata` without a Unity runtime. On a real mismatch the plugin disables
itself and reports what moved, both in the log and via `Core/Foundation/StartupAlert.cs`.

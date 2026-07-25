# Backlog

Deferred work, kept in-repo. (The prior task tracker was an external tool no longer connected.)

## Code health

- **Replace deprecated `Object.FindObjectsOfType<T>()`** with `FindObjectsByType<T>(FindObjectSortMode.None)`.
  Emits a CS0618 warning on every build. Call sites: `SeaPowerUX.Core/Foundation/NoesisInterop.cs`,
  `SeaPowerUX.FlightDeck/Plugin.cs`, `SeaPowerUX.TacticalPiP/Plugin.cs`. Use `FindObjectSortMode.None`
  unless a call actually depends on InstanceID ordering.

## Release readiness

- **Build-time probe verification** — assert every `GameApiValidator` probe resolves against the
  shipped `Seapower-Scripts.dll` (via `System.Reflection.Metadata`) so a bad probe can't silently
  disable a plugin.
- **Release artifact + CI** — no zip/bundle step exists yet; `manifest.json` lists no BepInEx
  dependency. The install scripts expect `install.*` beside a `BepInEx/` payload at the zip root.
- **Verify unproven runtime assumptions** — glyph coverage, resize cursors under Proton,
  `IsPositionUsable`'s `Screen` timing, and the never-executed error paths (validator fallback,
  `StartupAlert`).

## Design

- **Resolution independence for window defaults** — the coded defaults (toolbar `597,1050`, PiP
  `1,642` at `400x355`) are absolute pixels captured at ~1080p. Position could be expressed as
  screen fractions (PiP already has the machinery), but sizes stay fixed pixels, and PiP matching
  the native tactical map exactly would need it to read the map's live rect / DIP scale, since the
  game scales that window by `UIScale * height/1080`. Also the toolbar's "up from bottom" Y makes an
  upper-corner default resolution-fragile — a real corner would anchor to the top.

- **Reset/Set Default don't reach every native window.**
  - *Already-open native windows (e.g. Event Log)*: **implemented** — WindowMemory now learns each
    window's game-default position the first time it sees it unsaved, and a Reset (which clears the
    saved value) sends it back there. Remaining gap: the default is only learned if the window opens
    *without* a saved position at least once in the session. If a saved value already exists from a
    prior session, the game default is never observed, so Reset can't restore it. A more robust
    version would capture the default before applying a saved value, or persist it to config.
  - *Native Flight Deck*: popup-hosted, so it's deliberately excluded from WindowMemory (writing
    screen coords into a popup made it vanish). Neither Reset nor Set Default can touch it today;
    handling it needs a popup-aware path.

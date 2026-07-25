# SeaPowerUX

A [BepInEx](https://github.com/BepInEx/BepInEx) mod pack for **Sea Power** that adds new UI and
improves the UI the game already has.

It is a small shared library plus independent plugins rather than one monolithic mod. Quality-of-life
behaviour that isn't tied to a single feature — remembering where you put windows, hiding with the
HUD, docking, window chrome — lives in one place and applies everywhere, including to windows the
pack didn't create.

## Contents

| Component | What it is |
|---|---|
| `SeaPowerUX.Core` | Shared library. Not a plugin — no `[BepInPlugin]`. |
| `SeaPowerUX.TacticalPiP` | Picture-in-picture mirror of the 3D view. |
| `SeaPowerUX.FlightDeck` | **Air Boss** — unified flight deck manager. |
| `SeaPowerUX.WindowMemory` | Remembers positions of the game's own windows. Removable. |
| `SeaPowerUX.FormationManager` | Placeholder — no features yet. |

## What Core provides

These apply to **the game's own windows**, not just the pack's:

- **HUD-aware hiding.** The hide-HUD key hides the pack's UI along with the game's.
- **A shared toolbar** with a toggle per plugin, a draggable position, and a
  `Set Default | Reset` split button.
- **Set Default / Reset** for the layout — capture the current window arrangement as your default
  and restore it at any time. Out of the box, windows use built-in, screen-relative defaults.
- **Window chrome** — drag, collapse, close, desktop-style edge and corner resizing with proper
  resize cursors, and a lock button cycling nothing / position / size / both.
- **Docking** — presenting one of the game's native windows as part of a larger custom tool
  (see Air Boss), the two moving as a single control.

## Window memory (removable by design)

Sea Power doesn't remember where you move its windows; they reopen at their default spot.
`SeaPowerUX.WindowMemory` restores each window's saved position before it is first drawn, so it
lands in place with no visible jump.

It is a **separate plugin on purpose**: it's the only component that is purely a fix to the base
game's UI, and the most exposed thing the pack does — it writes positions into windows it didn't
create. If a game update reworks those windows, delete `SeaPowerUX.WindowMemory.dll` and the rest
of the pack carries on.

## Air Boss (Flight Deck Manager)

One window listing every ship and air base with a flight deck, so you don't have to hunt for each
carrier. Clicking a row opens the game's **own** flight deck panel, docked beside the list — the
real panel with all its controls, not a reimplementation.

Columns show live operations state: a summary (`Launching`, `Recovering`, `Ready`, …) and an `Ops`
column of coloured glyphs giving every concurrent state at once with aircraft counts (`▲1 ◆2 ◇1`).
The list updates as flight operations change and as units enter or leave the scene — no polling, no
refresh button.

## Picture-in-picture

Mirrors the main camera rather than rendering a second view, so there are no water-rendering
artifacts and no extra scene cost. Shows automatically when the fullscreen map (Tab) covers the 3D
view. Dragging its body orbits the real camera and scrolling zooms it, whether the PiP sits over the
3D view or the map.

The window is free-form: it **crops** the mirrored frame rather than scaling it, so narrowing the
window shows a narrower slice of the scene at unchanged height — what you want on an ultra-wide
display. A fit-to-aspect button snaps back to the uncropped shape.

## Planned

- **Formation Manager** — the shell plugin exists but is inert; formation features are still to be
  designed.
- **Contacts** — a unified contact report, status and management panel, as its own plugin.

## Installing

Requires BepInEx 5 (x64). Download the release, extract it, and run the installer for your system
— `install.bat` on Windows, `install.sh` on Linux / Steam Deck. It finds the game, checks BepInEx
is present, and copies everything in. Full step-by-step instructions, including installing BepInEx
and uninstalling, are in [INSTALL.md](INSTALL.md).

To place the files by hand instead, copy into the game directory so you get:

```
BepInEx/plugins/SeaPowerUX.Core/SeaPowerUX.Core.dll
BepInEx/plugins/SeaPowerUX.TacticalPiP/SeaPowerUX.TacticalPiP.dll
BepInEx/plugins/SeaPowerUX.FlightDeck/SeaPowerUX.FlightDeck.dll
BepInEx/plugins/SeaPowerUX.WindowMemory/SeaPowerUX.WindowMemory.dll
BepInEx/plugins/SeaPowerUX.FormationManager/SeaPowerUX.FormationManager.dll
```

Core must be present and come from the same release as the plugins — each plugin checks this on
load and disables itself with an explanatory message rather than failing obscurely.

Individual plugins can be removed; Core is required by all of them. Air Boss lists WindowMemory as a
*soft* dependency — recommended, but it runs without it. Plugins accept any Core on the same
`major.minor` line, so a binding redirect pointing several plugins at one newer Core is supported; a
different feature line is refused with an explanation.

## Compatibility

Each plugin probes the specific game members it depends on at startup. If a game update changes one,
the plugin disables itself with a list of what moved instead of failing at runtime. This is a
targeted capability check, not a version comparison, so updates that don't touch the integration
points still work.

## Building and contributing

Build everything and install it into a Sea Power directory on this machine:

```
scripts/deploy-local.sh
```

It finds the game in the usual Steam locations; if yours is elsewhere, point it at the folder
containing `Sea Power.exe`:

```
SEA_POWER_GAME_DIR="/path/to/Sea Power" scripts/deploy-local.sh
```

Build details, the architecture, and the safety constraints for changing UI code are in
[CONTRIBUTING.md](CONTRIBUTING.md).

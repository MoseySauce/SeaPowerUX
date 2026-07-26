# Changelog

All notable changes to SeaPowerUX. Versions follow the pack as a whole; individual
plugins carry their own version numbers in their BepInEx metadata.

## 1.0.0 — 2026-07-25

First release of the pack. Earlier versions shipped only the picture-in-picture plugin.

### Added

- **SeaPowerUX.Core** — a shared library the plugins build on, rather than each
  re-deriving the same Noesis/UGUI plumbing. Not a BepInEx plugin itself; it deploys to
  its own folder so exactly one copy is loaded.
- **Air Boss / Flight Deck Manager** — one window listing every ship and air base with a
  flight deck, showing live operations state. Clicking a row opens the game's own flight
  deck panel, docked beside the list so the two move as one control.
  - Status column (highest-priority activity) and an Ops column of coloured glyphs
    showing every concurrent state with aircraft counts.
  - Both are event-driven: `FlightDeck.FlightDeckTasks` is a `TrulyObservableCollection`
    that re-raises on item changes, so a task moving from "preparing" to "ready to
    launch" arrives as an event. No polling.
  - The ship list itself binds to `ObjectsManager._listOfAllPlayerObjects`, so units
    entering or leaving the scene update it without a manual refresh.
- **Window position memory** — the game has no position persistence at all; a window's
  element is destroyed on close and rebuilt from its DataTemplate. Positions are now
  remembered for every window, and applied during `Window.OnInitialized` so they land
  before the first render instead of visibly jumping.
- **Shared toolbar** with per-plugin toggles, a draggable position, and a
  `Set Default | Reset` split button.
- **Set Default / Reset** — capture the current window layout as your default and restore it at
  any time from the toolbar; windows otherwise use built-in, screen-relative defaults.
- **HUD-aware UI** — the hide-HUD key now hides the pack's windows too. The game hides
  its own HUD by switching off the Noesis UI camera, which never touched our separate
  canvas.
- **Desktop-style window resizing** — drag any edge or corner, with generated resize
  cursors, replacing the corner grab handle.
- **Window lock** — a title-bar button cycling nothing / position / size / both.

### Changed

- **SeaPowerUX.TacticalPiP** builds its window chrome from Core instead of its own copy (−183
  lines), and inherits edge resizing, the lock button and the border.
- **PiP crops rather than scales.** Narrowing the window now shows a narrower slice of
  the scene at unchanged height, which is what's wanted when the source is ultra-wide.
  A fit-to-aspect button snaps back to the uncropped shape.
- **SeaPowerUX.FormationManager** dropped its own position machinery (258 lines to ~85)
  once Core handled every window generically.
- Pixel↔DIP conversion now includes the player's UI scale, matching the game's own
  `Window.HeaderDragDelta`. It was previously wrong for anyone not at the default scale.

### Architecture

- **`SeaPowerUX.WindowMemory` split out of Core.** Native window position memory is the only
  part of the pack that is purely a quality-of-life change to the base game's UI and is needed
  by nothing else — and it's the most exposed, since it patches a game UI class and writes
  positions into windows it didn't create. As its own plugin it can be deleted in an emergency
  (a game update reworking those windows) while PiP and Air Boss keep working.
- **`WindowLifecycle`** in Core is now a neutral pre-render window hook that any plugin can
  subscribe to. Both window memory and Air Boss's docking need that moment; neither now depends
  on the other installing it.
- Air Boss declares WindowMemory as a **soft** dependency: recommended, load-order aware, and
  still functional without it.

### Removed

- **XAML injection.** It cannot work on this build: Noesis parses and caches the game's
  XAML before any BepInEx plugin can reach it. Proven by injecting a fixed position into
  every window in `MainGameView.xaml` and observing no effect, with the bytes verifiably
  replaced beforehand. The subsystem was deleted rather than left as a trap.

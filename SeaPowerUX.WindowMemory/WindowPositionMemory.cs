using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeaPowerUX.Core.Foundation;
using UnityEngine;

namespace SeaPowerUX.WindowMemory
{
    /// <summary>
    /// Remembers where the player put each of the game's own windows, and puts them back there
    /// the next time they open.
    ///
    /// The game itself does none of this. <c>SeapowerUI.Window</c> has X/Y dependency properties
    /// and drag handling, but no save/restore path anywhere — a window's element is destroyed on
    /// close and rebuilt from its DataTemplate on the next open, so its position resets to
    /// whatever the template/`AbsoluteInitialPosition` says. (The native Flight Deck panel appears
    /// to remember its position only because it's a Popup whose element is hidden rather than
    /// destroyed, so its X/Y happen to survive.) This class supplies the missing persistence for
    /// every window uniformly.
    ///
    /// Deliberately not Harmony: `Window` is a plain `HeaderedContentControl`, so reading and
    /// writing X/Y are ordinary property accesses. `WindowHost` — whose `Windows` collection we
    /// watch — IS a `Behavior&lt;FrameworkElement&gt;` and must never be patched, but subscribing
    /// to a public ObservableCollection it exposes is just a plain call and is safe.
    /// </summary>
    public static class WindowPositionMemory
    {
        private const string Section = "WindowPositions";

        /// <summary>How long a window must sit still after moving before we write the position.</summary>
        private const float StableSecondsToPersist = 0.30f;

        /// <summary>Normal sweep interval. A full Noesis visual-tree walk per frame is far too
        /// expensive to run continuously, and drag capture doesn't need better than this.</summary>
        private const float IdleSweepInterval = 0.10f;

        /// <summary>
        /// After WindowHost.Windows changes, sweep every frame for this many frames. A newly
        /// opened window's element doesn't exist yet when the collection fires — the hosting
        /// ItemsControl generates its container a frame or more later — so we have to look
        /// repeatedly to catch it as early as possible and restore its position before the
        /// player sees it at the default one.
        /// </summary>
        private const int BurstFrames = 20;

        private class Tracked
        {
            public ConfigEntry<string> Entry;
            public float LastX;
            public float LastY;
            public float LastW = -1f;   // -1 = size not tracked yet (or the window isn't resizable)
            public float LastH = -1f;
            public bool Dirty;
            public float StableSeconds;
        }

        private static readonly Dictionary<string, Tracked> TrackedWindows = new Dictionary<string, Tracked>();
        private static readonly HashSet<string> ExcludedKeys = new HashSet<string>();
        private static readonly HashSet<string> SeenThisSweep = new HashSet<string>();
        private static readonly List<string> ClosedKeys = new List<string>();

        private static ManualLogSource _logger;
        private static Runner _runner;
        private static bool _enabled;
        private static Harmony _pendingHarmony;

        /// <summary>When set, restore/correct decisions log at Info level for diagnosis. Off by default.</summary>
        public static bool Verbose;

        /// <summary>A window whose PositionChanged we're watching briefly after placement, to catch and
        /// undo the game's on-load bottom-anchor before the player can interact.</summary>
        private class AnchorGuard
        {
            public SeapowerUI.Window Window;
            public int FramesLeft;
        }

        private static readonly List<AnchorGuard> AnchorGuards = new List<AnchorGuard>();

        /// <summary>Where the game itself first placed each window, learned the first time we see it
        /// with no saved position. Lets a Reset (which clears the saved value) send a window back to
        /// the game's default instead of leaving it where the player last dragged it. Deliberately
        /// NOT cleared by ReapplyAll — that's what a reset relies on.</summary>
        private static readonly Dictionary<string, Vector4> GameDefaults = new Dictionary<string, Vector4>();

        /// <summary>The game's own default size for each resizable window, learned the first time we
        /// see it — before we ever restore a size (we never touch size pre-render, so the first size
        /// we measure is the template default). Lets a Reset restore the default size even when the
        /// saved/default entry only carries a position. Session-lived, like <see cref="GameDefaults"/>.</summary>
        private static readonly Dictionary<string, Vector2> GameDefaultSizes = new Dictionary<string, Vector2>();

        /// <summary>Idempotent — every plugin in the pack can call this from its own Awake().</summary>
        public static void Enable(ManualLogSource logger)
        {
            Enable(logger, null);
        }

        /// <summary>
        /// Pass a Harmony instance to also install the pre-render placement hook, which is what
        /// removes the visible flick. Without one, windows are still restored, but only after
        /// they have rendered once at their default position.
        /// </summary>
        public static void Enable(ManualLogSource logger, Harmony harmony)
        {
            if (harmony != null) _pendingHarmony = harmony;

            if (_enabled) return;
            _enabled = true;
            _logger = logger;

            // Core defers the actual patch until Noesis is up; subscribing is just a delegate add.
            if (_pendingHarmony != null) WindowLifecycle.Install(_pendingHarmony, logger);
            WindowLifecycle.WindowInitialized += PlaceOnInitialize;

            GameObject go = new GameObject("SeaPowerUX_WindowPositionMemory");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();

            // A reset writes new positions into config, but a window already on screen won't move
            // on its own — this class only applies a position when it first sees a window.
            UxSettings.DefaultsRestored += ReapplyAll;
        }

        /// <summary>
        /// Excludes a window from position memory entirely, keyed by view-model type name.
        /// </summary>
        public static void Exclude(string viewModelTypeName)
        {
            if (string.IsNullOrEmpty(viewModelTypeName)) return;
            ExcludedKeys.Add(UxSettings.SanitizeKey(viewModelTypeName));
        }


        /// <summary>
        /// The saved position for a window key, if one has been recorded. Public so the
        /// pre-render placement hook reads exactly what this class writes.
        /// </summary>
        public static bool TryGetSavedPosition(string viewModelTypeName, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (string.IsNullOrEmpty(viewModelTypeName)) return false;

            string key = UxSettings.SanitizeKey(viewModelTypeName);
            ConfigEntry<string> entry = UxSettings.Bind(
                Section, key, string.Empty,
                "Remembered geometry of the '" + key + "' window as \"x,y\" (or \"x,y,w,h\" if resized) in Noesis DIPs. Clear to reset to the game's default.");

            return TryParsePosition(entry.Value, out x, out y);
        }

        /// <summary>
        /// The config key for a live window, or null if it can't be identified. Public so the
        /// pre-render hook files a window under exactly the same key this class saves it under.
        /// </summary>
        public static string KeyFor(SeapowerUI.Window window)
        {
            if (window == null) return null;
            string key = BuildKey(window);
            if (key == null || ExcludedKeys.Contains(key)) return null;
            return key;
        }

        /// <summary>
        /// Records that a window has just been placed at (x, y) before rendering, so the next
        /// sweep treats that as its baseline. Without this the sweep sees a position differing
        /// from nothing-yet-tracked, and could re-save a value the player never chose.
        /// </summary>
        public static void NotePlacedAt(string key, float x, float y)
        {
            if (string.IsNullOrEmpty(key)) return;

            if (!TrackedWindows.TryGetValue(key, out Tracked tracked))
            {
                tracked = new Tracked
                {
                    Entry = UxSettings.Bind(
                        Section, key, string.Empty,
                        "Remembered geometry of the '" + key + "' window as \"x,y\" (or \"x,y,w,h\" if resized) in Noesis DIPs. Clear to reset to the game's default."),
                };
                TrackedWindows[key] = tracked;
            }

            tracked.LastX = x;
            tracked.LastY = y;
            tracked.Dirty = false;
            tracked.StableSeconds = 0f;
        }

        /// <summary>
        /// Places a window at its remembered position from inside the window's own initialisation,
        /// before it is first rendered — subscribed to Core's WindowLifecycle seam.
        /// </summary>
        private static void PlaceOnInitialize(SeapowerUI.Window window)
        {
            if (window == null) return;

            // A window bound for a dock is positioned by its dock, not by us. Asked of Core rather
            // than of whichever plugin docked it, so this plugin stays independent of them.
            if (DockedWindowRegistry.IsDocked(SafeDataContext(window))) return;

            // Only windows the host is managing: a popup-hosted window (the native flight deck)
            // interprets X/Y in its popup's coordinate space, so writing screen coordinates into
            // one makes it vanish.
            if (!NoesisInterop.IsHostManagedWindow(window)) return;

            string key = KeyFor(window);
            if (key == null) return;

            if (!TryGetSavedPosition(key, out float x, out float y)) return;
            if (!IsPositionUsable(x, y)) return;

            if (NoesisInterop.TrySetWindowXY(window, x, y)) NotePlacedAt(key, x, y);

            // Some windows carry the game's AbsoluteInitialPosition behavior, which re-anchors their
            // Y to a fixed gap above the screen bottom (the tactical display). It runs from the same
            // Loaded event that invokes this OnInitialized hook, but *after* us, and it can't be
            // stopped: it can't be patched (it derives from Behavior<Window>), and its Loaded handler
            // is already in that dispatch's snapshot, so nothing we subscribe during Loaded runs in
            // time. Instead we watch the window's PositionChanged — the anchor writes through
            // Window.Y, which raises it — and undo that one write. Disarmed a few frames later so it
            // can never touch a genuine player drag.
            window.PositionChanged += OnAnchorReposition;
            AnchorGuards.Add(new AnchorGuard { Window = window, FramesLeft = 3 });
            if (Verbose) _logger?.LogInfo($"[place] {key}: applied ({x},{y}) at init, watching for the anchor.");
        }

        /// <summary>
        /// Undoes the game's on-load bottom-anchor: when the AbsoluteInitialPosition behavior writes
        /// Window.Y after our placement, this fires and restores our saved position. A no-op once the
        /// window already sits where we want (including our own corrective write).
        /// </summary>
        private static void OnAnchorReposition(object sender, Noesis.EventArgs e)
        {
            SeapowerUI.Window window = sender as SeapowerUI.Window;
            if (window == null) return;

            string key = KeyFor(window);
            if (key == null) return;
            if (!TryGetSavedPosition(key, out float sx, out float sy)) return;
            if (!NoesisInterop.TryGetWindowXY(window, out double cx, out double cy)) return;

            if (Mathf.Abs((float)cx - sx) < 0.5f && Mathf.Abs((float)cy - sy) < 0.5f) return;

            if (NoesisInterop.TrySetWindowXY(window, sx, sy)) NotePlacedAt(key, sx, sy);
            if (Verbose) _logger?.LogInfo($"[anchor-correct] {key}: game moved it to ({cx:0},{cy:0}); restored ({sx},{sy}).");
        }

        /// <summary>
        /// Counts down each armed window's watch and stops watching once expired. Long enough to
        /// catch the game's on-load re-anchor (same frame the window opens), short enough that it
        /// can never intercept a real player drag, which can't happen this soon after open.
        /// </summary>
        private static void TickAnchorGuards()
        {
            for (int i = AnchorGuards.Count - 1; i >= 0; i--)
            {
                if (--AnchorGuards[i].FramesLeft > 0) continue;

                SeapowerUI.Window window = AnchorGuards[i].Window;
                AnchorGuards.RemoveAt(i);
                if (window == null) continue;
                try { window.PositionChanged -= OnAnchorReposition; }
                catch { }
            }
        }

        private static object SafeDataContext(SeapowerUI.Window window)
        {
            try { return window.DataContext; }
            catch { return null; }
        }

        /// <summary>
        /// Re-applies saved positions to every window currently on screen.
        ///
        /// Works by forgetting what's being tracked: the next sweep then treats each live window
        /// as freshly opened and runs the normal restore path, so there's one code path for
        /// placing a window rather than a separate "move it now" one that could drift from it.
        /// </summary>
        public static void ReapplyAll()
        {
            TrackedWindows.Clear();
        }

        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;

            UxSettings.DefaultsRestored -= ReapplyAll;
            WindowLifecycle.WindowInitialized -= PlaceOnInitialize;

            if (_runner != null)
            {
                UnityEngine.Object.Destroy(_runner.gameObject);
                _runner = null;
            }

            TrackedWindows.Clear();
        }

        /// <summary>
        /// Identifies a window across sessions. The DataContext's type is the most stable handle
        /// available — the Window element itself is a fresh object every open, and a SWIG proxy
        /// besides, so it can't be used. Falls back to the header text for windows whose
        /// DataContext is null or shared.
        ///
        /// Callers append an ordinal when several windows of the same view-model type are open at
        /// once (two flight decks, say). That's positional rather than truly identifying, so the
        /// second and later windows of a type can swap remembered positions between sessions —
        /// acceptable, and much better than having them all fight over one entry.
        /// </summary>
        private static string BuildKey(SeapowerUI.Window window)
        {
            string name = null;

            try
            {
                object dataContext = window.DataContext;
                if (dataContext != null) name = dataContext.GetType().Name;
            }
            catch { }

            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    object header = window.Header;
                    if (header != null) name = header.ToString();
                }
                catch { }
            }

            if (string.IsNullOrEmpty(name)) return null;

            return UxSettings.SanitizeKey(name);
        }

        /// <summary>
        /// Windows whose position isn't ours to own. Docked windows (AttachedWindow != null)
        /// continuously recompute X/Y from the window they're attached to, and a maximized one
        /// is by definition not where the player dragged it — writing to either would fight the
        /// game's own layout code every frame.
        /// </summary>
        private static bool ShouldSkip(SeapowerUI.Window window)
        {
            try
            {
                if (!window.Draggable) return true;
                if (window.Maximized) return true;
                if (window.AttachedWindow != null) return true;

                // Popup-hosted windows position themselves. Skipping them here also stops one
                // overwriting the saved entry for a key it shares with a real window — the native
                // flight deck vs. SeaPowerUX.FlightDeck's own FlightDeckViewModel windows. The
                // visual-tree check can't see a Popup ancestor (the child sits under its own
                // popup root), so fall back to "is the host managing this?" for windows whose key
                // is also used by a host-managed window.
                if (NoesisInterop.IsInsidePopup(window)) return true;
                if (!NoesisInterop.IsHostManagedWindow(window) && SharesKeyWithHostedWindow(window)) return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when a non-host window would be filed under a key that a host-managed window is
        /// also using. Deliberately narrow: plenty of legitimate windows aren't host-managed (and
        /// must keep their memory), so this only rejects the ambiguous case where two different
        /// windows would fight over one config entry.
        /// </summary>
        private static bool SharesKeyWithHostedWindow(SeapowerUI.Window window)
        {
            string key = BuildKey(window);
            if (key == null) return false;

            try
            {
                if (!SeaPower.Singleton<SeapowerUI.WindowHostManager>.InstanceExists()) return false;
                SeapowerUI.WindowHost host = SeaPower.Singleton<SeapowerUI.WindowHostManager>.Instance.CurrentHost;
                if (host == null) return false;

                foreach (object item in host.Windows)
                {
                    if (item != null && UxSettings.SanitizeKey(item.GetType().Name) == key) return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool _scaleLogged;

        private static void Sweep(float deltaSeconds)
        {
            List<SeapowerUI.Window> windows;
            try { windows = NoesisInterop.FindAllWindows(); }
            catch { return; }

            // Once, in-scene (windows present -> resolution is real), dump the scale context so a
            // "does this match my desktop scaling" question can be answered from the log.
            if (Verbose && !_scaleLogged && windows.Count > 0)
            {
                _scaleLogged = true;
                _logger?.LogInfo("[scale] " + NoesisInterop.ScaleReport());
            }

            SeenThisSweep.Clear();
            Dictionary<string, int> ordinals = new Dictionary<string, int>();

            foreach (SeapowerUI.Window window in windows)
            {
                if (window == null || ShouldSkip(window)) continue;

                string baseKey = BuildKey(window);
                if (baseKey == null || ExcludedKeys.Contains(baseKey)) continue;

                // Same type open more than once -> suffix by order of appearance.
                int ordinal = 0;
                ordinals.TryGetValue(baseKey, out ordinal);
                ordinals[baseKey] = ordinal + 1;

                string key = ordinal > 0 ? UxSettings.SanitizeKey(baseKey + "#" + ordinal) : baseKey;
                SeenThisSweep.Add(key);

                if (!NoesisInterop.TryGetWindowXY(window, out double x, out double y)) continue;

                // Size is only tracked for resizable windows; -1 means "don't touch size".
                float w = -1f, h = -1f;
                if (NoesisInterop.IsResizable(window)) NoesisInterop.TryGetWindowSize(window, out w, out h);

                if (!TrackedWindows.TryGetValue(key, out Tracked tracked))
                {
                    TrackedWindows[key] = AdoptNewlyOpened(window, key, (float)x, (float)y, w, h);
                    continue;
                }

                UpdateTracked(tracked, window, (float)x, (float)y, w, h, key, deltaSeconds);
            }

            ForgetClosedWindows();
        }

        /// <summary>
        /// First sight of a window: restore its saved position if we have one, otherwise start
        /// tracking wherever the game put it.
        /// </summary>
        private static Tracked AdoptNewlyOpened(SeapowerUI.Window window, string key, float currentX, float currentY, float currentW, float currentH)
        {
            ConfigEntry<string> entry = UxSettings.Bind(
                Section, key, string.Empty,
                "Remembered geometry of the '" + key + "' window as \"x,y\" (or \"x,y,w,h\" if resized) in Noesis DIPs. Clear to reset to the game's default.");

            Tracked tracked = new Tracked { Entry = entry, LastX = currentX, LastY = currentY, LastW = currentW, LastH = currentH };

            LearnDefaultSize(key, currentW, currentH);

            if (!TryParseGeometry(entry.Value, out float savedX, out float savedY, out float savedW, out float savedH))
            {
                // No saved geometry. Learn the game's own default the first time we see this window
                // untouched; on later opens (e.g. after a Reset cleared a value the player had saved)
                // put it back there rather than leaving it wherever it currently sits.
                if (!GameDefaults.TryGetValue(key, out Vector4 def))
                {
                    GameDefaults[key] = new Vector4(currentX, currentY, currentW, currentH);
                    if (Verbose) _logger?.LogInfo($"[game-default] {key}: learned ({currentX},{currentY},{currentW},{currentH}).");
                    return tracked;
                }

                bool posOff = !Mathf.Approximately(currentX, def.x) || !Mathf.Approximately(currentY, def.y);
                bool sizeOff = currentW >= 0f && def.z > 0f &&
                               (!Mathf.Approximately(currentW, def.z) || !Mathf.Approximately(currentH, def.w));
                if (posOff && NoesisInterop.TrySetWindowXY(window, def.x, def.y)) { tracked.LastX = def.x; tracked.LastY = def.y; }
                if (sizeOff && NoesisInterop.TrySetWindowSize(window, def.z, def.w)) { tracked.LastW = def.z; tracked.LastH = def.w; }
                if ((posOff || sizeOff) && Verbose)
                    _logger?.LogInfo($"[reset] {key}: no saved geometry; restored game default ({def.x},{def.y},{def.z},{def.w}).");
                return tracked;
            }
            if (!IsOnScreen(window, savedX, savedY)) return tracked;

            // Record what we just applied, not what the window had a moment ago — otherwise the very
            // next sweep reads our own write as a player move and re-saves it.
            if (NoesisInterop.TrySetWindowXY(window, savedX, savedY))
            {
                tracked.LastX = savedX;
                tracked.LastY = savedY;
            }
            if (savedW > 0f && currentW >= 0f && NoesisInterop.TrySetWindowSize(window, savedW, savedH))
            {
                tracked.LastW = savedW;
                tracked.LastH = savedH;
            }
            else if (savedW <= 0f && currentW >= 0f &&
                     GameDefaultSizes.TryGetValue(key, out Vector2 ds) &&
                     (!Mathf.Approximately(currentW, ds.x) || !Mathf.Approximately(currentH, ds.y)) &&
                     NoesisInterop.TrySetWindowSize(window, ds.x, ds.y))
            {
                // Saved/default entry has no size (a position-only default) — reset the size to the
                // game's own default so a Reset restores size, not just position.
                tracked.LastW = ds.x;
                tracked.LastH = ds.y;
                if (Verbose) _logger?.LogInfo($"[reset] {key}: default had no size; restored game default size ({ds.x},{ds.y}).");
            }

            return tracked;
        }

        private static void UpdateTracked(Tracked tracked, SeapowerUI.Window window, float x, float y, float w, float h, string key, float deltaSeconds)
        {
            // First real size for a window that was placed pre-render (position came through
            // NotePlacedAt, which has no size): learn the game default (this is it — nothing has
            // touched size yet), restore any saved size, then baseline it — don't treat this first
            // measurement as a player resize.
            if (tracked.LastW < 0f && w >= 0f)
            {
                LearnDefaultSize(key, w, h);

                if (TryParseGeometry(tracked.Entry.Value, out _, out _, out float sw, out float sh) && sw > 0f &&
                    NoesisInterop.TrySetWindowSize(window, sw, sh))
                {
                    tracked.LastW = sw;
                    tracked.LastH = sh;
                }
                else
                {
                    tracked.LastW = w;
                    tracked.LastH = h;
                }
                return;
            }

            bool moved = !Mathf.Approximately(x, tracked.LastX) || !Mathf.Approximately(y, tracked.LastY) ||
                         (w >= 0f && (!Mathf.Approximately(w, tracked.LastW) || !Mathf.Approximately(h, tracked.LastH)));

            if (moved)
            {
                // Still being dragged/resized — restart the debounce.
                tracked.LastX = x;
                tracked.LastY = y;
                if (w >= 0f) { tracked.LastW = w; tracked.LastH = h; }
                tracked.Dirty = true;
                tracked.StableSeconds = 0f;
                return;
            }

            if (!tracked.Dirty) return;

            tracked.StableSeconds += deltaSeconds;
            if (tracked.StableSeconds < StableSecondsToPersist) return;

            tracked.Dirty = false;
            tracked.StableSeconds = 0f;

            string serialized = SerializeGeometry(x, y, w, h);
            tracked.Entry.Value = serialized;

            if (Verbose) _logger?.LogInfo("[save] " + key + ": " + serialized);
            else if (_logger != null) _logger.LogDebug("Saved geometry for " + key + ": " + serialized);
        }

        /// <summary>
        /// Drops tracking for windows that have closed, so the next open is treated as a fresh
        /// open and gets its saved position re-applied. The saved config entry itself is kept.
        /// </summary>
        private static void ForgetClosedWindows()
        {
            ClosedKeys.Clear();

            foreach (KeyValuePair<string, Tracked> pair in TrackedWindows)
            {
                if (!SeenThisSweep.Contains(pair.Key)) ClosedKeys.Add(pair.Key);
            }

            for (int i = 0; i < ClosedKeys.Count; i++)
            {
                TrackedWindows.Remove(ClosedKeys[i]);
            }
        }

        private static bool TryParsePosition(string raw, out float x, out float y)
        {
            return TryParseGeometry(raw, out x, out y, out _, out _);
        }

        /// <summary>
        /// Parses a saved entry: "x,y" (size unset -> w,h = -1) or "x,y,w,h". Tolerant of extra
        /// components so an older two-value entry still restores its position.
        /// </summary>
        private static bool TryParseGeometry(string raw, out float x, out float y, out float w, out float h)
        {
            x = 0f; y = 0f; w = -1f; h = -1f;
            if (string.IsNullOrEmpty(raw)) return false;

            string[] parts = raw.Split(',');
            if (parts.Length < 2) return false;

            System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;

            if (!float.TryParse(parts[0], style, inv, out x) || !float.TryParse(parts[1], style, inv, out y))
                return false;

            if (parts.Length >= 4)
            {
                float.TryParse(parts[2], style, inv, out w);
                float.TryParse(parts[3], style, inv, out h);
            }
            return true;
        }

        /// <summary>Records the game's default size for a resizable window the first time it's seen,
        /// before any size restore. No-op after the first call per key, so it captures the true
        /// default rather than a later, player-resized value.</summary>
        private static void LearnDefaultSize(string key, float w, float h)
        {
            if (w > 0f && h > 0f && !GameDefaultSizes.ContainsKey(key))
                GameDefaultSizes[key] = new Vector2(w, h);
        }

        /// <summary>Serializes geometry as "x,y", plus ",w,h" when a size is being tracked.</summary>
        private static string SerializeGeometry(float x, float y, float w, float h)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            string s = x.ToString("0.##", inv) + "," + y.ToString("0.##", inv);
            if (w > 0f && h > 0f) s += "," + w.ToString("0.##", inv) + "," + h.ToString("0.##", inv);
            return s;
        }

        /// <summary>
        /// Guards against restoring a window to coordinates that no longer exist — the player may
        /// have saved a position at a larger resolution, or on a second monitor. A window stranded
        /// off-screen can't be dragged back, so fall back to the game's default rather than
        /// applying a position we can't verify is reachable.
        /// </summary>
        private static bool IsOnScreen(SeapowerUI.Window window, float x, float y)
        {
            return IsPositionUsable(x, y);
        }

        /// <summary>
        /// True if a remembered position still lands somewhere the player can reach on the current
        /// display. Public because the attached-property restore path (WindowMemory) applies
        /// positions during XAML parse, before any window object exists to consult.
        /// </summary>
        public static bool IsPositionUsable(float x, float y)
        {
            if (x < 0f || y < 0f) return false;

            float scale = NoesisInterop.NoesisDipScale;
            float viewportWidth = Screen.width / scale;
            float viewportHeight = Screen.height / scale;

            // Require the title bar to remain grabbable rather than the whole window to fit.
            const float MinVisible = 48f;
            return x <= viewportWidth - MinVisible && y <= viewportHeight - MinVisible;
        }

        private class Runner : MonoBehaviour
        {
            private float _sweepTimer;
            private float _sinceLastSweep;
            private int _burstFramesLeft;
            private System.Collections.Specialized.INotifyCollectionChanged _subscribed;

            /// <summary>
            /// LateUpdate rather than Update: NoesisView pushes its tree to the renderer after
            /// the normal update phase, so writing X/Y here lands in the same frame the window
            /// first draws — which is what stops a restored window flashing at its default
            /// position for a frame before jumping.
            /// </summary>
            private void LateUpdate()
            {
                EnsureSubscribedToWindowHost();
                TickAnchorGuards();

                _sinceLastSweep += Time.unscaledDeltaTime;

                if (_burstFramesLeft > 0)
                {
                    _burstFramesLeft--;
                    RunSweep();
                    return;
                }

                _sweepTimer += Time.unscaledDeltaTime;
                if (_sweepTimer < IdleSweepInterval) return;
                _sweepTimer = 0f;
                RunSweep();
            }

            private void RunSweep()
            {
                float delta = _sinceLastSweep;
                _sinceLastSweep = 0f;
                Sweep(delta);
            }

            /// <summary>
            /// Watches WindowHost.Windows so a newly opened window triggers an immediate
            /// every-frame sweep instead of waiting up to IdleSweepInterval — that delay is the
            /// difference between the player seeing the window appear in place and seeing it
            /// jump. The host is recreated per scene, so re-check which collection we're on.
            /// </summary>
            private void EnsureSubscribedToWindowHost()
            {
                System.Collections.Specialized.INotifyCollectionChanged current = null;
                try
                {
                    if (SeaPower.Singleton<SeapowerUI.WindowHostManager>.InstanceExists())
                    {
                        SeapowerUI.WindowHost host = SeaPower.Singleton<SeapowerUI.WindowHostManager>.Instance.CurrentHost;
                        if (host != null) current = host.Windows;
                    }
                }
                catch
                {
                    current = null;
                }

                if (ReferenceEquals(current, _subscribed)) return;

                if (_subscribed != null) _subscribed.CollectionChanged -= OnWindowsChanged;
                _subscribed = current;
                if (_subscribed != null) _subscribed.CollectionChanged += OnWindowsChanged;
            }

            private void OnWindowsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                _burstFramesLeft = BurstFrames;
            }

            private void OnDestroy()
            {
                if (_subscribed != null)
                {
                    _subscribed.CollectionChanged -= OnWindowsChanged;
                    _subscribed = null;
                }
            }
        }
    }
}

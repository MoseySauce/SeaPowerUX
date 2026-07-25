using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Owns a UGUI overlay window's geometry lifecycle so no plugin re-implements it: resolving the
    /// saved position/size — or a coded default when unset — into a pixel rect, applying it at build
    /// and on a Reset, keeping it on-screen, and persisting the player's moves and resizes.
    ///
    /// Consolidated from the near-identical <c>ApplyWindowConfig</c>/<c>SaveWindow*</c> code PiP and
    /// Air Boss each carried. Both had independently made the same mistake — building the window from
    /// the raw config values but only resolving them on Reset, so a fresh install placed the window
    /// off-screen until the player hit Reset. Doing the apply here, from <see cref="Install"/>, closes
    /// that gap once for every window plugin, present and future.
    ///
    /// CONFIG CONVENTION: X/Y default to <see cref="WindowDefaults.Auto"/> (-1) and Width/Height to 0.
    /// Those sentinels mean "unset — use the coded default"; each is resolved independently, so a
    /// window the player has moved but not resized keeps its position and takes the default size.
    /// Once moved/resized, real pixel values are written and used verbatim.
    ///
    /// OVERRIDE POINTS for plugin authors:
    ///  - the <see cref="DefaultRect"/> delegate decides how an unset component resolves — use a
    ///    built-in (<see cref="UiScale"/>, <see cref="Fraction"/>) or supply any lambda;
    ///  - <paramref name="onApplied"/> runs after each apply, for side effects like reallocating a
    ///    render target or re-laying-out content.
    /// Anything more bespoke can still hand-roll instead of using this — it's a convenience, not a
    /// gate.
    /// </summary>
    public sealed class WindowGeometry
    {
        /// <summary>Produces the default rect (pixels; height is CONTENT height, excluding the title
        /// bar) for whichever config components are still unset. Invoked on every apply, so it sees
        /// the live screen size and UI scale.</summary>
        public delegate RectInt DefaultRect();

        private readonly UguiWindow _window;
        private readonly ConfigEntry<int> _x, _y, _width, _height;
        private readonly ConfigEntry<WindowLock> _lock;
        private readonly DefaultRect _default;
        private readonly Action _onApplied;
        private bool _installed;

        public WindowGeometry(
            UguiWindow window,
            ConfigEntry<int> x, ConfigEntry<int> y,
            ConfigEntry<int> width, ConfigEntry<int> height,
            DefaultRect resolveDefault,
            ConfigEntry<WindowLock> lockEntry = null,
            Action onApplied = null)
        {
            _window = window;
            _x = x; _y = y; _width = width; _height = height;
            _default = resolveDefault;
            _lock = lockEntry;
            _onApplied = onApplied;
        }

        /// <summary>Applies the geometry now and wires save/reset. Call once, after the window and its
        /// content are built. Idempotent.</summary>
        public void Install()
        {
            if (_installed) return;
            _installed = true;

            Apply();
            _window.MoveEnded += SavePosition;
            _window.ResizeEnded += SaveSize;
            if (_lock != null) _window.LockChanged += OnLockChanged;
            UxSettings.DefaultsRestored += Apply;
        }

        public void Dispose()
        {
            if (!_installed) return;
            _installed = false;

            _window.MoveEnded -= SavePosition;
            _window.ResizeEnded -= SaveSize;
            if (_lock != null) _window.LockChanged -= OnLockChanged;
            UxSettings.DefaultsRestored -= Apply;
        }

        /// <summary>Pushes the resolved geometry onto the live window. Also the Reset path — the
        /// stored numbers changing don't move anything by themselves.</summary>
        public void Apply()
        {
            if (_window?.PanelRect == null) return;

            RectInt def = _default();

            int w = _width.Value > 0 ? _width.Value : def.width;
            int h = _height.Value > 0 ? _height.Value : def.height;
            int panelH = h + _window.TitleBarHeight;

            int x = _x.Value == WindowDefaults.Auto ? def.x : _x.Value;
            int y = _y.Value == WindowDefaults.Auto ? def.y : _y.Value;
            x = Mathf.Clamp(x, 0, Mathf.Max(0, Screen.width - w));
            y = Mathf.Clamp(y, 0, Mathf.Max(0, Screen.height - panelH));

            _window.PanelRect.anchoredPosition = new Vector2(x, -y);
            _window.PanelRect.sizeDelta = new Vector2(w, panelH);
            if (_lock != null) _window.Lock = _lock.Value;
            _window.ClampIntoScreen();

            _onApplied?.Invoke();
        }

        /// <summary>Saves the window's current position and size to config. For a programmatic change
        /// (e.g. fit-to-aspect) that doesn't arrive through the drag-end events.</summary>
        public void Persist() => SaveSize();   // SaveSize also persists position

        private void SavePosition()
        {
            Vector2 p = _window.PanelRect.anchoredPosition;
            _x.Value = Mathf.RoundToInt(p.x);
            _y.Value = Mathf.RoundToInt(-p.y);
        }

        private void SaveSize()
        {
            Vector2 s = _window.PanelRect.sizeDelta;
            _width.Value = Mathf.RoundToInt(s.x);
            _height.Value = Mathf.RoundToInt(s.y) - _window.TitleBarHeight;

            // Resizing from the left or top edge moves the window too, so persist position as well —
            // otherwise that placement is lost.
            SavePosition();
        }

        private void OnLockChanged(WindowLock l) => _lock.Value = l;

        // ---- Built-in default strategies -------------------------------------------------------

        /// <summary>
        /// A DIP (logical) default scaled by the game's UI scale, so the window tracks the native UI
        /// across resolutions and UI-scale settings. Both position and size scale. Width/height are
        /// CONTENT dimensions (title bar added by <see cref="Apply"/>).
        /// </summary>
        public static DefaultRect UiScale(int dipX, int dipY, int dipWidth, int dipHeight)
        {
            return () =>
            {
                float s = NoesisInterop.NoesisDipScale;
                if (s <= 0f) s = 1f;
                return new RectInt(
                    Mathf.RoundToInt(dipX * s), Mathf.RoundToInt(dipY * s),
                    Mathf.RoundToInt(dipWidth * s), Mathf.RoundToInt(dipHeight * s));
            };
        }

        /// <summary>
        /// Position expressed as a fraction of the screen (so it stays in the same region on any
        /// resolution); size in fixed pixels. Width/height are CONTENT dimensions.
        /// </summary>
        public static DefaultRect Fraction(float fracX, float fracY, int pxWidth, int pxHeight)
        {
            return () => new RectInt(
                Mathf.RoundToInt(fracX * Screen.width), Mathf.RoundToInt(fracY * Screen.height),
                pxWidth, pxHeight);
        }
    }
}

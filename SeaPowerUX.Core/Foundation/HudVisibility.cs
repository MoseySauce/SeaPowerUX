using System;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Keeps the pack's UI in step with the game's own "hide the HUD" state, so pressing the
    /// HideHUD key (Backspace by default) hides our windows and toolbar along with everything
    /// else instead of leaving them stranded over a clean view.
    ///
    /// Why they didn't hide already: the game hides its HUD in
    /// <c>MainGameViewModel.RefreshHudCameraVisibility</c> by switching off the Noesis
    /// <c>UiCamera</c>. Our overlay lives on a separate UGUI ScreenSpaceOverlay canvas that camera
    /// never touched, so it stayed on screen.
    ///
    /// The fix operates on the shared canvas rather than on individual windows: setting
    /// <c>Canvas.enabled = false</c> stops the whole subtree rendering in one step and, crucially,
    /// leaves every window's own shown/hidden state untouched — so unhiding the HUD brings back
    /// exactly the windows that were open before, with no per-plugin bookkeeping.
    ///
    /// This also fixes the toolbar appearing over the loading screen: <c>GetHudVisible()</c>
    /// returns false whenever there's no live <c>_mainGameViewModel</c>, which covers menus and
    /// loads as well as an explicit hide.
    /// </summary>
    public static class HudVisibility
    {
        private static Runner _runner;
        private static bool _lastKnown = true;

        /// <summary>Current HUD state, as the game reports it.</summary>
        public static bool IsHudVisible
        {
            get
            {
                try { return SeapowerUI.ViewModels.MainGameViewModel.GetHudVisible(); }
                catch { return true; }
            }
        }

        /// <summary>Raised when the game's HUD is hidden or shown.</summary>
        public static event Action<bool> Changed;

        /// <summary>Idempotent — safe for every plugin to call from its own Awake().</summary>
        public static void Enable()
        {
            if (_runner != null) return;

            GameObject go = new GameObject("SeaPowerUX_HudVisibility");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
        }

        private static void Tick()
        {
            bool visible = IsHudVisible;
            if (visible == _lastKnown) return;
            _lastKnown = visible;

            ApplyToSharedCanvas(visible);

            Action<bool> handler = Changed;
            if (handler != null)
            {
                try { handler(visible); } catch { }
            }
        }

        private static void ApplyToSharedCanvas(bool visible)
        {
            Canvas canvas = UguiWindowFactory.ExistingSharedCanvas;
            if (canvas != null) canvas.enabled = visible;
        }

        private class Runner : MonoBehaviour
        {
            private void Update() => Tick();

            private void OnDestroy()
            {
                // Never leave the pack's UI switched off because our watcher went away.
                ApplyToSharedCanvas(true);
            }
        }
    }
}

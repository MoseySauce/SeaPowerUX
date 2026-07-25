using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Stops the game's Noesis UI (tactical map pan/zoom, HUD clicks) from receiving mouse input
    /// while the pointer is over one of our UGUI overlay windows. Without this, clicking or
    /// dragging inside an overlay also pans the map underneath it.
    ///
    /// Mechanism (proven in SeaPowerUX.TacticalPiP): toggle the public `NoesisView.EnableMouse` property on
    /// every active view. This is a plain property set — deliberately NOT a Harmony patch of the
    /// map's input behavior, which is a Noesis `Behavior&lt;T&gt;` and crashes the Mono runtime
    /// natively when patched.
    ///
    /// WHY THIS IS CENTRAL (and not per-plugin): `EnableMouse` is global shared state. If two
    /// plugins each tracked "am I suppressing?" independently, they would clobber each other —
    /// moving the pointer from one overlay to another (or overlapping windows) would have one
    /// plugin restore input while the other still needs it suppressed. One arbiter over the union
    /// of all registered regions is the only correct arrangement.
    ///
    /// Regions register a RectTransform plus a visibility predicate; a hidden or collapsed window
    /// must not keep suppressing input, and callers know their own visibility rules better than
    /// this class does.
    /// </summary>
    public static class NoesisInputSuppressor
    {
        private class Region
        {
            public RectTransform Rect;
            public Func<bool> IsActive;
        }

        private static readonly List<Region> Regions = new List<Region>();
        private static readonly List<NoesisView> SuppressedViews = new List<NoesisView>();
        private static bool _suppressing;
        private static bool _forcedGameMouseOverUi;
        private static Runner _runner;

        /// <summary>
        /// Starts suppressing Noesis mouse input whenever the pointer is inside
        /// <paramref name="rect"/> and <paramref name="isActive"/> returns true (pass null to
        /// mean "always active"). Safe to call repeatedly for the same rect.
        /// </summary>
        public static void Register(RectTransform rect, Func<bool> isActive)
        {
            if (rect == null) return;

            for (int i = 0; i < Regions.Count; i++)
            {
                if (Regions[i].Rect == rect)
                {
                    Regions[i].IsActive = isActive;
                    return;
                }
            }

            Regions.Add(new Region { Rect = rect, IsActive = isActive });
            EnsureRunner();
        }

        public static void Unregister(RectTransform rect)
        {
            Regions.RemoveAll(r => r.Rect == rect || r.Rect == null);
            if (Regions.Count == 0)
            {
                // Nothing left to suppress for — make sure we never strand the game's UI with
                // mouse input switched off.
                Restore();
            }
        }

        /// <summary>True when the pointer is inside any registered, currently-active region.</summary>
        public static bool PointerOverAnyRegion()
        {
            // Regions are hit-tested by rect, not by raycast, so a window that's been hidden
            // wholesale (HUD hidden via Backspace) would otherwise keep suppressing input over
            // empty screen — the player would see nothing there but still couldn't click through.
            Canvas canvas = UguiWindowFactory.ExistingSharedCanvas;
            if (canvas != null && !canvas.enabled) return false;

            for (int i = Regions.Count - 1; i >= 0; i--)
            {
                Region region = Regions[i];
                if (region.Rect == null)
                {
                    Regions.RemoveAt(i);
                    continue;
                }
                if (region.IsActive != null && !region.IsActive()) continue;
                if (RectHitTest.PointerOverRect(region.Rect, true)) return true;
            }
            return false;
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            GameObject go = new GameObject("SeaPowerUX_NoesisInputSuppressor");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
        }

        private static void Tick()
        {
            bool over = PointerOverAnyRegion();

            // Tell the game its own mouse is "over a UI window" EVERY frame we're over one, not
            // just on the transition. MouseControlState.OnUpdate recomputes
            // (_isMouseOverContextMenu | _isMouseOverUIWindow) each frame and, when set, forces
            // State.UI — and InputHandler.handleMouseInputs() early-returns unless the state is
            // State.All. Without this, NoesisView.EnableMouse alone only stops Noesis widgets;
            // the game's own 3D picking (unit select / right-click orders) still fires straight
            // through our overlay windows.
            SetGameMouseOverUi(over);

            if (over == _suppressing) return;

            try
            {
                if (over)
                {
                    SuppressedViews.Clear();
                    foreach (NoesisView view in UnityEngine.Object.FindObjectsOfType<NoesisView>())
                    {
                        if (view != null && view.EnableMouse)
                        {
                            view.EnableMouse = false;
                            SuppressedViews.Add(view);
                        }
                    }
                }
                else
                {
                    Restore();
                }
                _suppressing = over;
            }
            catch (Exception)
            {
                // Keep state consistent with what we attempted so we don't get stuck inverted.
                _suppressing = over;
            }
        }

        /// <summary>
        /// Only ever writes `true` while the pointer is over one of our regions, and writes
        /// `false` exactly once when it leaves — the game sets this flag itself from its own
        /// mouse-enter/leave handlers, so continuously forcing `false` would fight it.
        /// </summary>
        private static void SetGameMouseOverUi(bool over)
        {
            if (!over && !_forcedGameMouseOverUi) return;

            try
            {
                if (!Singleton<MouseControlState>.InstanceExists()) return;
                Singleton<MouseControlState>.Instance.setMouseIsOverUIWindow(over);
                _forcedGameMouseOverUi = over;
            }
            catch
            {
                _forcedGameMouseOverUi = false;
            }
        }

        /// <summary>Re-enables mouse input on exactly the views we switched off.</summary>
        private static void Restore()
        {
            foreach (NoesisView view in SuppressedViews)
            {
                if (view != null)
                {
                    try { view.EnableMouse = true; } catch { }
                }
            }
            SuppressedViews.Clear();
            _suppressing = false;
            SetGameMouseOverUi(false);
        }

        /// <summary>Drives the per-frame check; separate so callers don't each need an Update.</summary>
        private class Runner : MonoBehaviour
        {
            private void Update() => Tick();

            private void OnDestroy()
            {
                // Never leave the game's UI unable to receive mouse input.
                Restore();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Noesis;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Safe, non-patching helpers for reading/writing live Noesis UI state: geometry, window
    /// position, and game-configured UI parameters. Every member here is a plain property
    /// get/set or method call on a Noesis object — never a Harmony IL-patch — which keeps it on
    /// the safe side of the crash rule documented in SeaPowerUX.TacticalPiP (patching Behavior&lt;T&gt;/
    /// TriggerAction&lt;T&gt; subclasses crashes the Mono runtime natively; calling their plain
    /// members, or plain-DependencyObject members like these, does not).
    /// </summary>
    public static class NoesisInterop
    {
        public static bool TryGetActualSize(FrameworkElement element, out float width, out float height)
        {
            width = 0f;
            height = 0f;
            if (element == null) return false;
            try
            {
                width = element.ActualWidth;
                height = element.ActualHeight;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Live on-screen rect of a Noesis element, in Unity screen pixels (top-left origin).</summary>
        public static bool TryGetScreenRect(FrameworkElement element, out UnityEngine.Rect screenRect)
        {
            screenRect = default;
            if (element == null) return false;
            try
            {
                Point topLeft = element.PointToScreen(new Point(0f, 0f));
                screenRect = new UnityEngine.Rect((float)topLeft.X, (float)topLeft.Y, element.ActualWidth, element.ActualHeight);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetWindowXY(SeapowerUI.Window window, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (window == null) return false;
            try
            {
                x = window.X;
                y = window.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Plain property set on Window.X/Window.Y — not a patch, safe per the rule above.</summary>
        public static bool TrySetWindowXY(SeapowerUI.Window window, double x, double y)
        {
            if (window == null) return false;
            try
            {
                window.X = (float)x;
                window.Y = (float)y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Whether a window allows resizing (Window.CanResize). Size memory only applies to
        /// these — forcing a size on a content-sized window would fight its layout.</summary>
        public static bool IsResizable(SeapowerUI.Window window)
        {
            if (window == null) return false;
            try { return window.CanResize; }
            catch { return false; }
        }

        /// <summary>
        /// Reads the window's size. Prefers the explicitly-set Width/Height (what the resize grip
        /// writes); falls back to the rendered ActualWidth/ActualHeight when those are unset (NaN —
        /// the window is auto-sizing to content), so a real size is always available to remember.
        /// </summary>
        public static bool TryGetWindowSize(SeapowerUI.Window window, out float w, out float h)
        {
            w = 0f; h = 0f;
            if (window == null) return false;
            try
            {
                w = window.Width;
                h = window.Height;
                if (float.IsNaN(w) || w <= 0f) w = window.ActualWidth;
                if (float.IsNaN(h) || h <= 0f) h = window.ActualHeight;
                return w > 0f && h > 0f;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Plain property set on Window.Width/Window.Height — not a patch, safe per the rule
        /// above.</summary>
        public static bool TrySetWindowSize(SeapowerUI.Window window, float w, float h)
        {
            if (window == null) return false;
            try
            {
                window.Width = w;
                window.Height = h;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Finds a live SeapowerUI.Window whose DataContext is an instance of the given
        /// ViewModel type, by walking the visual tree of every active NoesisView. Deliberately
        /// does not depend on locating a WindowManager singleton (no confirmed global access
        /// path for one exists) — this only relies on generic, confirmed-safe Noesis APIs
        /// (VisualTreeHelper, FrameworkElement.DataContext), so it works regardless of how the
        /// game wires up any particular WindowManager instance.
        /// </summary>
        /// <summary>
        /// Finds the live SeapowerUI.Window whose DataContext is this exact object. Use when
        /// several windows of the same view-model type may be open and you need the one for a
        /// specific instance (FindWindowByViewModelType would return an arbitrary match).
        /// </summary>
        public static SeapowerUI.Window FindWindowByDataContext(object dataContext)
        {
            if (dataContext == null) return null;

            foreach (NoesisView view in UnityEngine.Object.FindObjectsOfType<NoesisView>())
            {
                if (view == null) continue;

                FrameworkElement root;
                try { root = view.Content; }
                catch { continue; }
                if (root == null) continue;

                SeapowerUI.Window found = FindDescendant<SeapowerUI.Window>(root, w =>
                {
                    try { return ReferenceEquals(w.DataContext, dataContext); }
                    catch { return false; }
                });
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>
        /// Pixels-per-DIP for the game's Noesis UI: the UI is laid out in a logical space 1080
        /// units tall, then multiplied by the player's UI scale setting.
        ///
        /// The UIScale term is not optional — the game's own Window.HeaderDragDelta computes
        /// exactly `(OptionsManager.Instance.UIScale) * Screen.height / 1080f` when clamping a
        /// dragged window against the screen edges. Omitting it makes every pixel↔DIP conversion
        /// wrong by the scale factor for any player who isn't on the default setting, which shows
        /// up as our context menus and windows landing at the wrong place.
        /// </summary>
        public static float NoesisDipScale
        {
            get
            {
                float uiScale = 1f;
                try
                {
                    if (SeaPower.Singleton<SeaPower.OptionsManager>.InstanceExists())
                    {
                        uiScale = SeaPower.Singleton<SeaPower.OptionsManager>.Instance.UIScale;
                    }
                }
                catch
                {
                    uiScale = 1f;
                }

                return Mathf.Max(0.0001f, uiScale * Screen.height / 1080f);
            }
        }

        /// <summary>
        /// One-line dump of everything that feeds the DIP scale, for diagnosing whether Unity is
        /// seeing the true render resolution or a desktop-scaled one. Compares Screen.width/height
        /// (what our maths and the game's own UI both use) against the reported physical resolution
        /// and DPI, plus the game's UIScale and the resulting NoesisDipScale.
        /// </summary>
        public static string ScaleReport()
        {
            float uiScale = 1f;
            try
            {
                if (SeaPower.Singleton<SeaPower.OptionsManager>.InstanceExists())
                    uiScale = SeaPower.Singleton<SeaPower.OptionsManager>.Instance.UIScale;
            }
            catch { }

            Resolution cur = Screen.currentResolution;
            return $"Screen {Screen.width}x{Screen.height} px, currentResolution {cur.width}x{cur.height}, " +
                   $"fullScreen={Screen.fullScreen}, dpi={Screen.dpi:0.#}, UIScale={uiScale:0.###}, " +
                   $"NoesisDipScale={NoesisDipScale:0.####}";
        }

        /// <summary>
        /// Converts a Unity screen point (pixels, bottom-left origin) to the Noesis DIP space the
        /// game's Window.X/Y use (top-left origin).
        /// </summary>
        public static Vector2 ScreenPointToNoesisDip(Vector2 unityScreenPoint)
        {
            float scale = NoesisDipScale;
            return new Vector2(
                unityScreenPoint.x / scale,
                (Screen.height - unityScreenPoint.y) / scale);
        }


        /// <summary>
        /// True if this element sits inside a Noesis Popup.
        ///
        /// Position memory must leave those alone. A Popup positions itself relative to its
        /// placement target, and a Window inside one has its X/Y interpreted in the popup's own
        /// coordinate space — so applying a screen-space position translates it clean out of the
        /// popup's visible area and the panel appears to vanish. The game's native flight deck is
        /// exactly this shape: a Popup whose content is a Window bound to a FlightDeckViewModel,
        /// which is also the key SeaPowerUX.FlightDeck's own child windows save under.
        /// </summary>
        public static bool IsInsidePopup(DependencyObject element)
        {
            DependencyObject node = element;

            // Guarded: a malformed or cyclic tree must not spin here, since this runs inside the
            // game's window initialisation.
            for (int depth = 0; node != null && depth < 128; depth++)
            {
                if (node is Popup) return true;

                try { node = VisualTreeHelper.GetParent(node); }
                catch { return false; }
            }

            return false;
        }

        /// <summary>
        /// True if this window was opened through <c>WindowHost</c> — i.e. its DataContext is
        /// currently an item in <c>WindowHost.Windows</c>.
        ///
        /// This is a positive test, and that's the point. Trying to identify windows we must NOT
        /// touch by detecting a Popup ancestor doesn't work: a Popup's child lives under its own
        /// popup root and the Popup itself is in the logical tree, not the child's visual
        /// ancestry, so a visual-tree walk never finds it. Asking instead "is this one of the
        /// windows the host is managing?" needs no tree walking and is exactly the set whose X/Y
        /// are screen coordinates we're entitled to set.
        ///
        /// The window that forced this: the game's native flight deck is a Popup whose content is
        /// a Window bound to a FlightDeckViewModel — the same type SeaPowerUX.FlightDeck opens its
        /// own windows with. Placing it by screen coordinates inside the popup's coordinate space
        /// pushed it out of view entirely, so the panel simply never appeared.
        /// </summary>
        public static bool IsHostManagedWindow(SeapowerUI.Window window)
        {
            if (window == null) return false;

            try
            {
                object dataContext = window.DataContext;
                if (dataContext == null) return false;

                // Routed from the window itself, so it reaches whichever host owns it. Do NOT use
                // WindowHostManager.CurrentHost here: that is merely the last WindowHost
                // constructed, and a mission has several (the Tactical Display declares its own),
                // so a window can easily belong to a different host than CurrentHost points at.
                // CanExecute on CloseWindowCommand is the game's own "is this window open" test —
                // it's what ToggleWindowBroker uses to track its own window's state.
                return SeapowerUI.WindowHostCommands.CloseWindowCommand.CanExecute(dataContext, window);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Every live SeapowerUI.Window across every active NoesisView, in visual-tree order.
        /// Unlike the FindWindowBy* helpers this does not stop at the first match — position
        /// memory has to see all of them each sweep to notice opens and closes.
        /// </summary>
        public static List<SeapowerUI.Window> FindAllWindows()
        {
            List<SeapowerUI.Window> found = new List<SeapowerUI.Window>();

            foreach (NoesisView view in UnityEngine.Object.FindObjectsOfType<NoesisView>())
            {
                if (view == null) continue;

                FrameworkElement root;
                try { root = view.Content; }
                catch { continue; }
                if (root == null) continue;

                CollectDescendants(root, found);
            }

            return found;
        }

        private static void CollectDescendants<T>(DependencyObject root, List<T> into) where T : DependencyObject
        {
            if (root == null) return;

            if (root is T typed) into.Add(typed);

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(root); }
            catch { return; }

            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(root, i); }
                catch { continue; }
                if (child == null) continue;

                CollectDescendants(child, into);
            }
        }

        private static T FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
        {
            if (root == null) return null;

            if (root is T typed && predicate(typed))
            {
                return typed;
            }

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(root); }
            catch { return null; }

            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(root, i); }
                catch { continue; }
                if (child == null) continue;

                T result = FindDescendant(child, predicate);
                if (result != null) return result;
            }

            return null;
        }

    }
}

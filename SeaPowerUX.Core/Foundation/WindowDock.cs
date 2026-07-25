using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>Where a docked element sits relative to the region it's docked to.</summary>
    public enum DockSide
    {
        /// <summary>Fills the target region exactly — the "embedded panel" look.</summary>
        Fill,
        Right,
        Left,
        Above,
        Below,
    }

    /// <summary>
    /// Something that follows a host window. Implemented by native Noesis windows today; the
    /// interface exists so future children (further native panels, extra UGUI surfaces) can be
    /// attached to the same host without the host needing to know what they are.
    /// </summary>
    public interface IDockedElement
    {
        /// <summary>False once the element has gone away, so the host can drop it.</summary>
        bool IsAlive { get; }

        /// <summary>
        /// Size this element wants, in Unity screen pixels, or Vector2.zero if unknown. The host
        /// uses this to reserve space so the composite reads as one control.
        /// </summary>
        Vector2 DesiredSizePx { get; }

        /// <summary>Place the element against a region given in screen pixels, top-left origin.</summary>
        void Apply(Rect targetPx);

        /// <summary>Host is hidden/collapsed — get out of the way without closing.</summary>
        void SetVisible(bool visible);
    }

    /// <summary>
    /// Makes a set of child elements move and hide as one with a UGUI host window, so a native
    /// game window can be presented as part of a larger custom tool rather than as a separate
    /// floating panel.
    ///
    /// Why this can look genuinely embedded: Noesis composites above Unity's ScreenSpaceOverlay
    /// canvases regardless of sorting order (established while building the PiP context menu). A
    /// native window positioned exactly over a reserved slot in our window therefore draws on top
    /// of that slot, and our chrome frames it. The illusion holds as long as we keep re-asserting
    /// the position, which also has the side effect of making the native title bar non-draggable —
    /// correct here, since the child is no longer independently positioned.
    ///
    /// Attach to the host window's GameObject; it drives itself from LateUpdate.
    /// </summary>
    public class WindowDock : MonoBehaviour
    {
        /// <summary>The host window's panel — the rect children are positioned against.</summary>
        public RectTransform Host;

        /// <summary>
        /// Optional sub-region of the host that children dock against (e.g. a reserved slot inside
        /// the content area). Falls back to <see cref="Host"/> when null.
        /// </summary>
        public RectTransform Target;

        /// <summary>Extra predicate for "the host is showing" beyond the GameObject being active.</summary>
        public Func<bool> IsHostVisible;

        private readonly List<IDockedElement> _elements = new List<IDockedElement>();
        private bool _lastVisible = true;

        public void Add(IDockedElement element)
        {
            if (element == null || _elements.Contains(element)) return;
            _elements.Add(element);
            DockedWindowRegistry.Register(element as NativeWindowDockedElement);
        }

        public void Remove(IDockedElement element)
        {
            _elements.Remove(element);
            DockedWindowRegistry.Unregister(element as NativeWindowDockedElement);
        }

        public void Clear()
        {
            _elements.Clear();
        }

        /// <summary>Largest desired size across live children, for the host to reserve space.</summary>
        public Vector2 DesiredChildSizePx()
        {
            Vector2 max = Vector2.zero;
            for (int i = 0; i < _elements.Count; i++)
            {
                if (!_elements[i].IsAlive) continue;
                Vector2 size = _elements[i].DesiredSizePx;
                if (size.x > max.x) max.x = size.x;
                if (size.y > max.y) max.y = size.y;
            }
            return max;
        }

        /// <summary>
        /// LateUpdate so the host's own layout (drag, resize, content-size fitting) has already
        /// settled for the frame — otherwise children would chase the host a frame behind and
        /// visibly lag during a drag.
        /// </summary>
        private void LateUpdate()
        {
            for (int i = _elements.Count - 1; i >= 0; i--)
            {
                if (_elements[i].IsAlive) continue;
                DockedWindowRegistry.Unregister(_elements[i] as NativeWindowDockedElement);
                _elements.RemoveAt(i);
            }
            if (_elements.Count == 0) return;

            // Host.activeInHierarchy, not this component's own GameObject: this deliberately runs
            // on a separate always-active root so it keeps ticking after the host is hidden, which
            // is the only way it can tell its children to hide too.
            bool visible = Host != null
                && Host.gameObject.activeInHierarchy
                && (IsHostVisible == null || IsHostVisible())
                && HudVisibility.IsHudVisible;

            if (visible != _lastVisible)
            {
                _lastVisible = visible;
                for (int i = 0; i < _elements.Count; i++) _elements[i].SetVisible(visible);
            }

            if (!visible) return;

            RectTransform target = Target != null ? Target : Host;
            if (target == null) return;

            Rect targetPx = ScreenRectOf(target);
            for (int i = 0; i < _elements.Count; i++) _elements[i].Apply(targetPx);
        }

        /// <summary>
        /// A RectTransform's screen rect in pixels with a TOP-LEFT origin, matching the convention
        /// Noesis window coordinates use. On a ScreenSpaceOverlay canvas world corners are already
        /// screen pixels, so this only has to flip Y.
        /// </summary>
        public static Rect ScreenRectOf(RectTransform rect)
        {
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            float left = corners[0].x;
            float width = corners[2].x - corners[0].x;
            float height = corners[1].y - corners[0].y;
            float topFromBottom = corners[1].y;

            return new Rect(left, Screen.height - topFromBottom, width, height);
        }
    }

    /// <summary>
    /// Docks one of the game's own Noesis windows to a host region.
    ///
    /// The window is re-resolved by DataContext every frame rather than cached:
    /// <c>SeapowerUI.Window</c> is a SWIG proxy and the hosting ItemsControl regenerates its
    /// containers whenever <c>WindowHost.Windows</c> changes, so a stored reference goes stale.
    /// </summary>
    public class NativeWindowDockedElement : IDockedElement
    {
        private readonly Func<object> _dataContext;
        private readonly DockSide _side;
        private readonly float _gapPx;

        private Vector2 _desiredPx;
        private bool _closed;
        private bool _chromeSuppressed;
        private bool _placed;
        private bool _hostVisible = true;
        private int _framesWaiting;

        /// <summary>
        /// Strips the native window's own title bar, close button and resize grips, so a docked
        /// panel doesn't present a second set of window controls inside the host's frame.
        ///
        /// Everything this needs is public on <c>SeapowerUI.Window</c> or reachable from it:
        /// the buttons are entirely a <c>ButtonsTemplate</c>, the resize grips are bound to
        /// <c>CanResize</c>, and the header row can be found from the public <c>HeaderThumb</c>.
        /// No template surgery, no Harmony.
        /// </summary>
        public bool SuppressChrome;

        public NativeWindowDockedElement(Func<object> dataContext, DockSide side, float gapPx = 0f)
        {
            _dataContext = dataContext;
            _side = side;
            _gapPx = gapPx;
        }

        public bool IsAlive => !_closed && _dataContext != null && _dataContext() != null;

        public Vector2 DesiredSizePx => _desiredPx;

        /// <summary>Marks this element finished so the host drops it (the window was closed).</summary>
        public void Close() => _closed = true;

        private SeapowerUI.Window Resolve()
        {
            object dc = _dataContext?.Invoke();
            return dc == null ? null : NoesisInterop.FindWindowByDataContext(dc);
        }

        public void Apply(Rect targetPx)
        {
            SeapowerUI.Window window = Resolve();
            if (window == null) return;

            if (SuppressChrome) ApplyChromeSuppression(window);

            float scale = NoesisInterop.NoesisDipScale;

            // Measure first: the native window sizes itself from its own Min/Max constraints, so
            // we fit our layout around it rather than forcing dimensions it would fight.
            if (NoesisInterop.TryGetActualSize(window, out float wDip, out float hDip) && wDip > 0f)
            {
                _desiredPx = new Vector2(wDip * scale, hDip * scale);
            }

            // The slot is only sized once we've reported a measurement, so on the first frame of
            // a new child the target is still degenerate. Placing against it would put the window
            // somewhere meaningless — and revealing it there is exactly the first-open flick.
            // Measure this frame, place and reveal the next.
            bool targetReady = targetPx.width > 1f && targetPx.height > 1f;
            if (!targetReady)
            {
                // Safety valve: never leave a window invisible forever if a target rect never
                // materialises. Better a mispositioned panel than one the player can't see.
                if (!_placed && ++_framesWaiting > 120)
                {
                    _placed = true;
                    if (_hostVisible) SetWindowVisible(window, true);
                }
                return;
            }

            Vector2 topLeftPx;
            switch (_side)
            {
                case DockSide.Right:
                    topLeftPx = new Vector2(targetPx.xMax + _gapPx, targetPx.yMin);
                    break;
                case DockSide.Left:
                    topLeftPx = new Vector2(targetPx.xMin - _gapPx - _desiredPx.x, targetPx.yMin);
                    break;
                case DockSide.Above:
                    topLeftPx = new Vector2(targetPx.xMin, targetPx.yMin - _gapPx - _desiredPx.y);
                    break;
                case DockSide.Below:
                    topLeftPx = new Vector2(targetPx.xMin, targetPx.yMax + _gapPx);
                    break;
                default: // Fill
                    topLeftPx = new Vector2(targetPx.xMin, targetPx.yMin);
                    break;
            }

            // Screen px (top-left origin) -> Noesis DIPs (also top-left origin): just the scale.
            NoesisInterop.TrySetWindowXY(window, topLeftPx.x / scale, topLeftPx.y / scale);

            // Reveal only once the window has actually been measured and moved. It is created
            // Hidden (see PrepareOnInit) because Noesis renders it at its default 0,0 a frame or
            // two before we can position it — the same flick that affects every window in this
            // game. Hidden rather than Collapsed so it still lays out and can be measured; a
            // Collapsed element has no size, so we'd never learn where to put it.
            if (!_placed && _desiredPx.x > 1f)
            {
                _placed = true;
                if (_hostVisible) SetWindowVisible(window, true);
            }
        }

        /// <summary>
        /// Removes the docked window's own window controls. Re-checked rather than done once:
        /// <c>Window.OnInitialized()</c> re-runs on every Loaded and re-reads HeaderThumb from the
        /// template, so a re-templated window would otherwise get its drag handlers back.
        /// </summary>
        private void ApplyChromeSuppression(SeapowerUI.Window window)
        {
            try
            {
                // Buttons are hidden with an EMPTY template, never by nulling ButtonsTemplate.
                // The template renders `Content="{TemplateBinding Content}"` — the window's own
                // content — so with no ContentTemplate the presenter falls back to the implicit
                // DataTemplate for that content's type. For a FlightDeckViewModel that implicit
                // template is the whole flight deck window, so nulling it made the title bar
                // recursively render a nested copy of the window and the panel came out blank.
                Noesis.DataTemplate empty = EmptyTemplate();
                if (empty != null && !ReferenceEquals(window.ButtonsTemplate, empty))
                {
                    window.ButtonsTemplate = empty;
                }

                // Resize grips are bound to CanResize; the host owns sizing now.
                if (window.CanResize) window.CanResize = false;

                // HeaderDragDelta only checks Maximized, so Draggable alone gates the START of a
                // drag but not the movement. Hiding the thumb removes the grab target as well.
                if (window.Draggable) window.Draggable = false;

                if (_chromeSuppressed) return;

                // Only the thumb itself — a leaf. Collapsing its ancestors was tried and is not
                // worth the risk: the header row and the window body are siblings under the same
                // Grid, and getting the walk wrong takes the entire panel with it.
                Noesis.Thumb thumb = window.HeaderThumb;
                if (thumb == null) return;

                thumb.Visibility = Noesis.Visibility.Collapsed;
                window.Header = null;
                _chromeSuppressed = true;
            }
            catch
            {
                // Cosmetic only — never let chrome removal break the dock.
            }
        }

        private static Noesis.DataTemplate _emptyTemplate;
        private static bool _emptyTemplateTried;

        /// <summary>
        /// A DataTemplate that renders nothing, built once. Used to blank a ContentPresenter
        /// without letting it fall back to an implicit template for the content's type.
        /// </summary>
        private static Noesis.DataTemplate EmptyTemplate()
        {
            if (_emptyTemplateTried) return _emptyTemplate;
            _emptyTemplateTried = true;

            try
            {
                _emptyTemplate = Noesis.GUI.ParseXaml(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                    "<Grid Width=\"0\" Height=\"0\"/></DataTemplate>") as Noesis.DataTemplate;
            }
            catch
            {
                _emptyTemplate = null;
            }

            return _emptyTemplate;
        }

        public void SetVisible(bool visible)
        {
            _hostVisible = visible;

            SeapowerUI.Window window = Resolve();
            if (window == null) return;

            // Don't reveal a window we haven't positioned yet just because the host became
            // visible — that would put the flick back.
            SetWindowVisible(window, visible && _placed);
        }

        private static void SetWindowVisible(SeapowerUI.Window window, bool visible)
        {
            try
            {
                // Hidden, never Collapsed: hiding with the host must not tear down the window or
                // its view model, and Hidden keeps it laid out so it stays measurable.
                window.Visibility = visible ? Noesis.Visibility.Visible : Noesis.Visibility.Hidden;
            }
            catch
            {
                // Never let a visibility flip break the host's update loop.
            }
        }

        /// <summary>
        /// Called from the pre-render window hook: hides a window that is about to be docked, so it
        /// never renders at its default position before the dock has placed it.
        /// </summary>
        internal void PrepareOnInit(SeapowerUI.Window window)
        {
            _placed = false;
            _framesWaiting = 0;
            if (SuppressChrome) ApplyChromeSuppression(window);
            SetWindowVisible(window, false);
        }

        /// <summary>The object whose window this element is docking, or null.</summary>
        internal object CurrentDataContext => _dataContext?.Invoke();
    }
}

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Lets the pre-render window hook recognise a window that is about to be docked, so it can be
    /// hidden before its first render instead of flashing at its default position.
    ///
    /// This is the same problem — and the same fix — as remembered window positions: a window is
    /// created, laid out and rendered inside one Noesis pass, so nothing that reacts afterwards can
    /// beat the first frame. WindowInitHook runs during the window's own initialisation, which is
    /// early enough to hide it.
    /// </summary>
    public static class DockedWindowRegistry
    {
        private static readonly List<NativeWindowDockedElement> Elements = new List<NativeWindowDockedElement>();

        internal static void Register(NativeWindowDockedElement element)
        {
            if (element == null || Elements.Contains(element)) return;

            if (Elements.Count == 0) WindowLifecycle.WindowInitialized += OnWindowInitialized;
            Elements.Add(element);
        }

        private static void OnWindowInitialized(SeapowerUI.Window window) => TryPrepare(window);

        /// <summary>
        /// True if this window belongs to a dock. Public so window position memory — which lives
        /// in a separate, removable plugin — can leave docked windows alone without depending on
        /// whichever plugin docked them.
        /// </summary>
        public static bool IsDocked(object dataContext)
        {
            if (dataContext == null) return false;

            for (int i = 0; i < Elements.Count; i++)
            {
                if (ReferenceEquals(Elements[i].CurrentDataContext, dataContext)) return true;
            }
            return false;
        }

        internal static void Unregister(NativeWindowDockedElement element)
        {
            if (element == null) return;
            Elements.Remove(element);
            if (Elements.Count == 0) WindowLifecycle.WindowInitialized -= OnWindowInitialized;
        }

        /// <summary>
        /// True if this window belongs to a dock (in which case it has been hidden pending
        /// placement), so the caller knows not to apply remembered-position handling to it.
        /// </summary>
        internal static bool TryPrepare(SeapowerUI.Window window)
        {
            if (window == null || Elements.Count == 0) return false;

            object dataContext;
            try { dataContext = window.DataContext; }
            catch { return false; }
            if (dataContext == null) return false;

            for (int i = 0; i < Elements.Count; i++)
            {
                if (!ReferenceEquals(Elements[i].CurrentDataContext, dataContext)) continue;
                Elements[i].PrepareOnInit(window);
                return true;
            }

            return false;
        }
    }
}

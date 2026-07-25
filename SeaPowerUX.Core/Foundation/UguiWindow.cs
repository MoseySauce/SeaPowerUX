using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>What a window's lock button is currently holding in place.</summary>
    public enum WindowLock
    {
        None,
        Position,
        Size,
        Both,
    }

    /// <summary>Options for a new overlay window, analogous to SeaPowerUX.TacticalPiP's config-driven PipPanel setup.</summary>
    public class UguiWindowOptions
    {
        public string Title = "Window";
        public int InitialX = 20;
        public int InitialY = 20;
        public int InitialWidth = 300;
        public int InitialHeight = 200;
        public Vector2 MinContentSize = new Vector2(160f, 90f);
        public int TitleBarHeight = 24;

        /// <summary>PiP needs the content locked to screen aspect; generic panels don't.</summary>
        public bool AspectLocked = false;
        public bool ShowResizeHandle = true;
        public bool ShowCollapseButton = true;
        public bool ShowCloseButton = true;

        /// <summary>
        /// Inset in pixels between the window's edges and its content area, so content isn't flush
        /// against the border (which clips the first character of left-aligned text).
        /// </summary>
        public int ContentPadding = 6;

        /// <summary>Draws a thin border around the window's outer edge.</summary>
        public bool ShowBorder = true;
        public Color BorderColor = new Color(0.25f, 0.27f, 0.30f, 1f);
    }

    /// <summary>
    /// A draggable/resizable/collapsible overlay window, generalized from SeaPowerUX.TacticalPiP's
    /// PipPanel/title-bar/drag/resize code so any SeaPowerUX.* plugin can build new UI (the
    /// "genuinely new windows" technique) without re-deriving that plumbing. Callers populate
    /// ContentRect with their own children; this class only owns the chrome.
    /// </summary>
    public class UguiWindow
    {
        public Canvas Canvas { get; }
        public RectTransform PanelRect { get; }
        public RectTransform ContentRect { get; }

        /// <summary>Title-bar height in pixels. The panel is this taller than its content, so callers
        /// converting between content height and panel height (e.g. WindowGeometry) need it.</summary>
        public int TitleBarHeight => _options.TitleBarHeight;

        /// <summary>Fires once when a title-bar drag ends — debounced-persist hook (position).</summary>
        public event Action MoveEnded;
        /// <summary>Fires continuously while resizing — live hook (e.g. reallocate a render target).</summary>
        public event Action<Vector2> Resized;
        /// <summary>Fires once when a resize drag ends — debounced-persist hook (size).</summary>
        public event Action ResizeEnded;
        public event Action CloseClicked;
        /// <summary>Fires when the window is collapsed or expanded — for callers that need to
        /// stop work while collapsed (PiP disables its camera capture).</summary>
        public event Action<bool> CollapsedChanged;

        /// <summary>Grab band along each edge, in pixels. Wide enough to hit without hunting.</summary>
        private const float EdgeThickness = 6f;
        private static readonly Vector2 CornerSize = new Vector2(12f, 12f);

        private readonly UguiWindowOptions _options;
        private WindowLock _lock = WindowLock.None;
        private Text _lockGlyph;

        /// <summary>Raised when the lock state changes, so callers can persist it.</summary>
        public event Action<WindowLock> LockChanged;

        public WindowLock Lock
        {
            get => _lock;
            set
            {
                if (_lock == value) return;
                _lock = value;
                if (_lockGlyph != null) _lockGlyph.text = GlyphFor(value);
                LockChanged?.Invoke(value);
            }
        }

        public bool PositionLocked => _lock == WindowLock.Position || _lock == WindowLock.Both;
        public bool SizeLocked => _lock == WindowLock.Size || _lock == WindowLock.Both;

        private RectTransform _titleBarRect;
        private RectTransform _titleTextRect;
        private float _nextTitleButtonOffset;
        private bool _collapsed;
        private float _expandedHeight;

        internal UguiWindow(Canvas canvas, UguiWindowOptions options)
        {
            Canvas = canvas;
            _options = options;

            GameObject panelGo = new GameObject("UguiWindow_" + options.Title);
            panelGo.transform.SetParent(canvas.transform, false);
            PanelRect = panelGo.AddComponent<RectTransform>();
            PanelRect.anchorMin = new Vector2(0f, 1f);
            PanelRect.anchorMax = new Vector2(0f, 1f);
            PanelRect.pivot = new Vector2(0f, 1f);
            PanelRect.anchoredPosition = new Vector2(options.InitialX, -options.InitialY);
            PanelRect.sizeDelta = new Vector2(options.InitialWidth, options.InitialHeight + options.TitleBarHeight);

            Image windowBg = panelGo.AddComponent<Image>();
            windowBg.color = new Color(0.10f, 0.11f, 0.13f, 1f);

            if (options.ShowBorder)
            {
                // Outline on the panel graphic itself, so the border follows the window's outer
                // edge at whatever size it's dragged to — same approach as the shared toolbar.
                Outline border = panelGo.AddComponent<Outline>();
                border.effectColor = options.BorderColor;
                border.effectDistance = new Vector2(1f, -1f);
            }

            BuildTitleBar(panelGo.transform);
            ContentRect = BuildContent(panelGo.transform);

            if (options.ShowResizeHandle)
            {
                // On the panel, after the title bar, so edge zones win over the drag bar.
                BuildResizeBorders(panelGo.transform);
            }
        }

        /// <summary>Reorders this window's RectTransform to the last sibling under the shared
        /// canvas so it renders/hit-tests above other windows sharing the same Canvas — the
        /// UGUI equivalent of the game's own Window.BringToFront.</summary>
        public void BringToFront()
        {
            PanelRect.SetAsLastSibling();
        }

        /// <summary>
        /// Pulls the window fully back on screen, shrinking it first if it's larger than the
        /// display. Position is anchored top-left with a top-left pivot, so x runs right from the
        /// left edge and y runs negative downward from the top.
        /// </summary>
        public void ClampIntoScreen()
        {
            if (PanelRect == null) return;

            Vector2 size = PanelRect.sizeDelta;
            size.x = Mathf.Min(size.x, Screen.width);
            size.y = Mathf.Min(size.y, Screen.height);
            PanelRect.sizeDelta = size;

            Vector2 pos = PanelRect.anchoredPosition;
            pos.x = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, Screen.width - size.x));
            pos.y = Mathf.Clamp(pos.y, -Mathf.Max(0f, Screen.height - size.y), 0f);
            PanelRect.anchoredPosition = pos;
        }

        public void SetVisible(bool visible)
        {
            PanelRect.gameObject.SetActive(visible);
        }

        public void SetCollapsed(bool collapsed)
        {
            if (_collapsed == collapsed) return;
            _collapsed = collapsed;

            ContentRect.gameObject.SetActive(!collapsed);
            CollapsedChanged?.Invoke(collapsed);

            if (collapsed)
            {
                _expandedHeight = PanelRect.sizeDelta.y;
                PanelRect.sizeDelta = new Vector2(PanelRect.sizeDelta.x, _options.TitleBarHeight);
            }
            else
            {
                PanelRect.sizeDelta = new Vector2(PanelRect.sizeDelta.x, _expandedHeight);
            }
        }

        private void BuildTitleBar(Transform parent)
        {
            GameObject barGo = new GameObject("TitleBar");
            barGo.transform.SetParent(parent, false);
            _titleBarRect = barGo.AddComponent<RectTransform>();
            _titleBarRect.anchorMin = new Vector2(0f, 1f);
            _titleBarRect.anchorMax = new Vector2(1f, 1f);
            _titleBarRect.pivot = new Vector2(0.5f, 1f);
            _titleBarRect.sizeDelta = new Vector2(0f, _options.TitleBarHeight);
            _titleBarRect.anchoredPosition = Vector2.zero;

            Image barBg = barGo.AddComponent<Image>();
            barBg.color = new Color(0.16f, 0.17f, 0.20f, 1f);
            barBg.raycastTarget = true;

            WindowDragHandler drag = barGo.AddComponent<WindowDragHandler>();
            drag.Target = PanelRect;
            drag.OnMoveEnd = () => MoveEnded?.Invoke();
            drag.OnBeginDragCallback = BringToFront;
            drag.CanDrag = () => !PositionLocked;

            GameObject titleGo = new GameObject("Title");
            titleGo.transform.SetParent(barGo.transform, false);
            RectTransform titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 0f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.offsetMin = new Vector2(8f, 0f);
            _titleTextRect = titleRect;
            Text title = titleGo.AddComponent<Text>();
            title.text = _options.Title;
            title.font = GetUiFont();
            title.fontSize = 13;
            title.alignment = TextAnchor.MiddleLeft;
            title.color = new Color(0.85f, 0.87f, 0.90f, 1f);
            title.raycastTarget = false;

            // Right to left: close, then collapse, then anything a caller adds.
            if (_options.ShowCloseButton)
            {
                CreateTitleButton(barGo.transform, "✕", _nextTitleButtonOffset, () => CloseClicked?.Invoke());
                _nextTitleButtonOffset += _options.TitleBarHeight;
            }
            if (_options.ShowCollapseButton)
            {
                CreateTitleButton(barGo.transform, "–", _nextTitleButtonOffset, () => SetCollapsed(!_collapsed));
                _nextTitleButtonOffset += _options.TitleBarHeight;
            }

            ReserveTitleWidth();
        }

        /// <summary>
        /// Adds a caller-supplied button to the title bar, placed to the LEFT of the built-in
        /// collapse/close buttons. Returns the button so the caller can retint or relabel it.
        /// </summary>
        public Button AddTitleButton(string glyph, Action onClick)
        {
            if (_titleBarRect == null) return null;

            Button button = CreateTitleButton(_titleBarRect.transform, glyph, _nextTitleButtonOffset, onClick);
            _nextTitleButtonOffset += _options.TitleBarHeight;
            ReserveTitleWidth();
            return button;
        }

        /// <summary>
        /// Adds the multi-function lock button. One button, four states, cycled on each click:
        /// nothing locked → position → size → both → nothing. The glyph fills in as more is
        /// held: an empty circle through half-filled to solid.
        ///
        /// One cycling button rather than two toggles because the title bar is 24px tall and
        /// already carries close and collapse; a second toggle would crowd it for a setting that
        /// is changed rarely.
        /// </summary>
        public Button AddLockButton()
        {
            Button button = AddTitleButton(GlyphFor(_lock), CycleLock);
            _lockGlyph = button != null ? button.GetComponentInChildren<Text>() : null;
            return button;
        }

        private void CycleLock()
        {
            switch (_lock)
            {
                case WindowLock.None: Lock = WindowLock.Position; break;
                case WindowLock.Position: Lock = WindowLock.Size; break;
                case WindowLock.Size: Lock = WindowLock.Both; break;
                default: Lock = WindowLock.None; break;
            }
        }

        private static string GlyphFor(WindowLock state)
        {
            switch (state)
            {
                case WindowLock.Position: return "◐";
                case WindowLock.Size: return "◑";
                case WindowLock.Both: return "●";
                default: return "○";
            }
        }

        /// <summary>Keeps the title text clear of however many buttons now sit on the bar.</summary>
        private void ReserveTitleWidth()
        {
            if (_titleTextRect == null) return;
            _titleTextRect.offsetMax = new Vector2(-(_nextTitleButtonOffset + 4f), 0f);
        }

        private Button CreateTitleButton(Transform bar, string glyph, float offsetFromRight, Action onClick)
        {
            GameObject go = new GameObject("Btn_" + glyph);
            go.transform.SetParent(bar, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(_options.TitleBarHeight, _options.TitleBarHeight);
            rect.anchoredPosition = new Vector2(-offsetFromRight, 0f);

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0f);

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            ColorBlock colors = btn.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.15f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.30f);
            btn.colors = colors;
            btn.onClick.AddListener(() => onClick());

            GameObject labelGo = new GameObject("Glyph");
            labelGo.transform.SetParent(go.transform, false);
            RectTransform labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            Text label = labelGo.AddComponent<Text>();
            label.text = glyph;
            label.font = GetUiFont();
            label.fontSize = 15;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = new Color(0.85f, 0.87f, 0.90f, 1f);
            label.raycastTarget = false;

            return btn;
        }

        private RectTransform BuildContent(Transform parent)
        {
            GameObject contentGo = new GameObject("Content");
            contentGo.transform.SetParent(parent, false);
            RectTransform contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 0f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            float pad = _options.ContentPadding;
            contentRect.offsetMin = new Vector2(pad, pad);
            contentRect.offsetMax = new Vector2(-pad, -(_options.TitleBarHeight + pad));
            return contentRect;
        }

        /// <summary>
        /// Invisible resize zones along every edge and corner, the way a desktop window behaves.
        ///
        /// Replaces a visible grab square in the bottom-right corner, which is a touch-first
        /// affordance and reads as a button rather than a resize grip on a mouse-driven game.
        ///
        /// These are added AFTER the title bar so they are later siblings: UGUI raycasts pick the
        /// topmost hit, so the thin band along the window's top edge resizes while the rest of the
        /// title bar still drags.
        /// </summary>
        private void BuildResizeBorders(Transform parent)
        {
            // NO top edge and no top corners: the title bar owns the whole top of the window, and
            // a resize band there fights the drag grip for the same pixels. Windows grow from the
            // left, right and bottom instead.
            CreateResizeZone(parent, "ResizeLeft",   new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(EdgeThickness, 0f), new Vector2(0f, 0f), ResizeCursor.Horizontal, left: true);
            CreateResizeZone(parent, "ResizeRight",  new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(EdgeThickness, 0f), new Vector2(1f, 0f), ResizeCursor.Horizontal, right: true);
            CreateResizeZone(parent, "ResizeBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, EdgeThickness), new Vector2(0f, 0f), ResizeCursor.Vertical, bottom: true);

            CreateResizeZone(parent, "ResizeBL", new Vector2(0f, 0f), new Vector2(0f, 0f), CornerSize, new Vector2(0f, 0f), ResizeCursor.DiagonalNESW, left: true,  bottom: true);
            CreateResizeZone(parent, "ResizeBR", new Vector2(1f, 0f), new Vector2(1f, 0f), CornerSize, new Vector2(1f, 0f), ResizeCursor.DiagonalNWSE, right: true, bottom: true);
        }

        private void CreateResizeZone(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 sizeDelta, Vector2 pivot, ResizeCursor cursor,
            bool left = false, bool right = false, bool top = false, bool bottom = false)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = sizeDelta;
            rect.anchoredPosition = Vector2.zero;

            // Fully transparent but still hit-testable — the grip is the window edge itself.
            Image hit = go.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;

            WindowResizeHandler handler = go.AddComponent<WindowResizeHandler>();
            handler.PanelRect = PanelRect;
            handler.MinContentSize = _options.MinContentSize;
            handler.TitleBarHeight = _options.TitleBarHeight;
            handler.AspectLocked = _options.AspectLocked;
            handler.Cursor = cursor;
            handler.Left = left;
            handler.Right = right;
            handler.Top = top;
            handler.Bottom = bottom;
            handler.CanResize = () => !SizeLocked;
            handler.OnResized = size => Resized?.Invoke(size);
            handler.OnResizeEnd = () => ResizeEnded?.Invoke();
        }

        public static Font GetUiFont()
        {
            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            if (font == null)
            {
                font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Arial", "Liberation Sans", "DejaVu Sans" }, 14);
            }
            return font;
        }
    }

    /// <summary>
    /// Lazily creates and hands out ONE shared overlay Canvas (ScreenSpaceOverlay, high sorting
    /// order to stay above the game's own UI) so windows from different SeaPowerUX.* plugins
    /// don't each fight over their own sorting order — matches SeaPowerUX.TacticalPiP's proven
    /// PipSortingOrder approach, but shared across the whole pack instead of per-plugin.
    /// </summary>
    public static class UguiWindowFactory
    {
        private const int SharedSortingOrder = 32760;
        private static Canvas _sharedCanvas;

        /// <summary>
        /// The shared canvas if one has been created, otherwise null — for callers that want to
        /// act on it without forcing it into existence (HudVisibility, which may run before any
        /// plugin has actually opened a window).
        /// </summary>
        public static Canvas ExistingSharedCanvas => _sharedCanvas;

        public static Canvas GetOrCreateSharedCanvas()
        {
            if (_sharedCanvas != null) return _sharedCanvas;

            GameObject canvasGo = new GameObject("SeaPowerUX_SharedCanvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);

            _sharedCanvas = canvasGo.AddComponent<Canvas>();
            _sharedCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _sharedCanvas.sortingOrder = SharedSortingOrder;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            canvasGo.AddComponent<GraphicRaycaster>();

            // The canvas can come into existence at any point, including while the HUD is already
            // hidden (during a load, or after the player pressed the hide-HUD key). Start in
            // whatever state the game's HUD is in rather than unconditionally visible.
            HudVisibility.Enable();
            _sharedCanvas.enabled = HudVisibility.IsHudVisible;

            return _sharedCanvas;
        }

        public static UguiWindow Create(UguiWindowOptions options)
        {
            return new UguiWindow(GetOrCreateSharedCanvas(), options);
        }

        /// <summary>
        /// Builds a window on a caller-supplied canvas instead of the shared one. SeaPowerUX.TacticalPiP needs
        /// this: it keeps its own canvas at a distinct sorting order because it hides that canvas
        /// wholesale while a Noesis context menu is open, which it can't do to a canvas shared with
        /// every other plugin in the pack.
        /// </summary>
        public static UguiWindow Create(Canvas canvas, UguiWindowOptions options)
        {
            return new UguiWindow(canvas ?? GetOrCreateSharedCanvas(), options);
        }
    }

    /// <summary>Drags a RectTransform by its own pointer events (window move via title bar).</summary>
    public class WindowDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform Target;
        public Action OnMoveEnd;
        public Action OnBeginDragCallback;

        /// <summary>Null means always draggable; a window's position lock supplies this.</summary>
        public Func<bool> CanDrag;

        private bool Allowed => CanDrag == null || CanDrag();

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!Allowed) return;
            OnBeginDragCallback?.Invoke();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!Allowed || Target == null) return;
            Target.anchoredPosition += eventData.delta;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!Allowed) return;
            OnMoveEnd?.Invoke();
        }
    }

    /// <summary>
    /// Resizes a RectTransform from a corner handle. When AspectLocked, the content area is kept
    /// at the screen's aspect ratio (PiP's requirement, so a mirrored frame isn't stretched);
    /// otherwise width/height follow the drag independently. Debounced persistence: Resized
    /// fires every drag frame (for live effects like reallocating a render target), ResizeEnd
    /// fires once when the drag finishes (for writing to config).
    /// </summary>
    public class WindowResizeHandler : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public RectTransform PanelRect;
        public ResizeCursor Cursor = ResizeCursor.None;

        /// <summary>Null means always resizable; a window's size lock supplies this.</summary>
        public Func<bool> CanResize;

        private bool Allowed => CanResize == null || CanResize();
        public Vector2 MinContentSize = new Vector2(160f, 90f);
        public float TitleBarHeight;
        public bool AspectLocked;

        /// <summary>Which edges this zone drags. Corners set two.</summary>
        public bool Left, Right, Top, Bottom;

        public Action<Vector2> OnResized;
        public Action OnResizeEnd;

        private bool _hovering;
        private bool _dragging;

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;
            // No resize cursor while the size is locked — showing one would promise something the
            // window won't do.
            if (Allowed) ResizeCursors.Set(Cursor);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            // Keep the resize cursor for the whole drag even if the pointer runs off the thin
            // band, which it will as soon as the window starts following it.
            if (!_dragging) ResizeCursors.Clear();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!Allowed) return;
            _dragging = true;
            ResizeCursors.Set(Cursor);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _dragging = false;
            if (!_hovering) ResizeCursors.Clear();
            OnResizeEnd?.Invoke();
        }

        private void OnDisable()
        {
            // The window can be hidden mid-hover; never strand the pointer as a resize arrow.
            _hovering = false;
            _dragging = false;
            ResizeCursors.Clear();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!Allowed || PanelRect == null) return;

            Vector2 delta = eventData.delta;
            Vector2 size = PanelRect.sizeDelta;
            Vector2 pos = PanelRect.anchoredPosition;

            float minWidth = MinContentSize.x;
            float minHeight = MinContentSize.y + TitleBarHeight;

            // The panel is anchored top-left with a top-left pivot, so dragging the left or top
            // edge has to move the window as well as resize it — otherwise the opposite edge
            // walks across the screen instead of staying put.
            if (Right) size.x = Mathf.Max(minWidth, size.x + delta.x);

            if (Left)
            {
                float width = Mathf.Max(minWidth, size.x - delta.x);
                pos.x += size.x - width;
                size.x = width;
            }

            if (Bottom) size.y = Mathf.Max(minHeight, size.y - delta.y);

            if (Top)
            {
                float height = Mathf.Max(minHeight, size.y + delta.y);
                pos.y += height - size.y;
                size.y = height;
            }

            if (AspectLocked)
            {
                float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
                float contentHeight = Mathf.Max(MinContentSize.y, size.x / aspect);
                size.y = contentHeight + TitleBarHeight;
            }

            PanelRect.sizeDelta = size;
            PanelRect.anchoredPosition = pos;

            OnResized?.Invoke(size);
        }
    }

    /// <summary>
    /// Direct rect hit-testing, generalized from SeaPowerUX.TacticalPiP's PipInputState.PointerOverPip().
    /// Deliberately NOT EventSystem.IsPointerOverGameObject() — that was found unreliable with
    /// the game's Input System UI module.
    /// </summary>
    public static class RectHitTest
    {
        public static bool PointerOverRect(RectTransform rect, bool visible)
        {
            if (!visible || rect == null) return false;
            // ScreenSpaceOverlay canvas -> pass null camera.
            return RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null);
        }
    }
}

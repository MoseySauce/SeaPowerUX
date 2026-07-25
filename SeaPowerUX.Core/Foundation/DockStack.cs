using UnityEngine;
using UnityEngine.UI;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>Which way a dock stack grows when a child appears.</summary>
    public enum DockExpand
    {
        /// <summary>Pick whichever side has room on screen, re-evaluated as the host moves.</summary>
        Auto,
        Right,
        Left,
        Up,
        Down,
    }

    /// <summary>
    /// Arranges a host window and a child area as a single composite control.
    ///
    /// The host panel keeps its own chrome (title bar, background, content) and is the "parent"
    /// container. Next to it sits a HOLLOW slot: a RectTransform that reserves space and paints
    /// nothing. That hollowness is the whole point — this pack's overlay canvas has a very high
    /// sorting order and draws ABOVE Noesis, so any background we put over a docked native window
    /// would hide it. Leaving the slot empty lets the native panel show through while a thin frame
    /// drawn around the union of the two makes them read as one control.
    ///
    /// The slot grows to whatever the child needs and collapses to nothing when there is no child,
    /// which shrinks the composite back to just the host panel. The host panel itself never moves
    /// when the child appears — the stack expands away from it, in a direction that Auto picks from
    /// the room actually available on screen (a window near the right edge expands left, one near
    /// the bottom expands up), so a child can never open off-screen.
    /// </summary>
    public class DockStack : MonoBehaviour
    {
        private const float DefaultGap = 4f;
        private const float BorderThickness = 1f;

        private static readonly Color FrameColor = HexColor("#3f4147");

        public DockExpand Expand = DockExpand.Auto;
        public float Gap = DefaultGap;

        /// <summary>The host window's panel — the parent container of the stack.</summary>
        public RectTransform Host;

        /// <summary>The hollow child area. Reserves space, paints nothing.</summary>
        public RectTransform Slot { get; private set; }

        private RectTransform _frame;
        private Image[] _frameStrips;
        private Vector2 _childSizePx;
        private DockExpand _resolved = DockExpand.Right;

        /// <summary>The always-active object this stack runs on. Attach related components here.</summary>
        public GameObject Root { get; private set; }

        /// <summary>
        /// The stack lives on its OWN GameObject under the canvas, never on the host panel.
        /// Hiding a UguiWindow deactivates its panel GameObject, which would stop this component's
        /// LateUpdate — leaving the slot, the frame and any docked native window stranded on screen
        /// with no one left running to hide them.
        /// </summary>
        public static DockStack Attach(RectTransform host, Canvas canvas)
        {
            GameObject rootGo = new GameObject("DockRoot");
            rootGo.transform.SetParent(canvas.transform, false);

            DockStack stack = rootGo.AddComponent<DockStack>();
            stack.Root = rootGo;
            stack.Host = host;
            stack.Build(canvas);
            return stack;
        }

        private void Build(Canvas canvas)
        {
            GameObject slotGo = new GameObject("DockSlot");
            Slot = slotGo.AddComponent<RectTransform>();
            Slot.SetParent(canvas.transform, false);
            ConfigureTopLeft(Slot);
            // Deliberately NO Image component. See the class remarks: this must stay hollow.

            GameObject frameGo = new GameObject("DockFrame");
            _frame = frameGo.AddComponent<RectTransform>();
            _frame.SetParent(canvas.transform, false);
            ConfigureTopLeft(_frame);

            _frameStrips = new Image[4];
            for (int i = 0; i < 4; i++)
            {
                GameObject strip = new GameObject("Edge" + i);
                RectTransform rect = strip.AddComponent<RectTransform>();
                rect.SetParent(_frame, false);
                ConfigureTopLeft(rect);

                Image image = strip.AddComponent<Image>();
                image.color = FrameColor;
                image.raycastTarget = false;
                _frameStrips[i] = image;
            }

            // Behind the host panel, so the host's own chrome always wins where they overlap.
            _frame.SetSiblingIndex(Mathf.Max(0, Host.GetSiblingIndex()));
            SetChildSizePx(Vector2.zero);
        }

        private static void ConfigureTopLeft(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
        }

        /// <summary>
        /// Sets how much room the child needs. Vector2.zero collapses the stack back to the host
        /// panel alone.
        /// </summary>
        public void SetChildSizePx(Vector2 sizePx)
        {
            _childSizePx = sizePx;

            bool hasChild = sizePx.x > 1f && sizePx.y > 1f;
            if (Slot != null && Slot.gameObject.activeSelf != hasChild) Slot.gameObject.SetActive(hasChild);
            if (_frame != null && _frame.gameObject.activeSelf != hasChild) _frame.gameObject.SetActive(hasChild);
        }

        /// <summary>The slot's on-screen rect in pixels, top-left origin — where a child should go.</summary>
        public Rect SlotScreenRect()
        {
            return WindowDock.ScreenRectOf(Slot);
        }

        private void LateUpdate()
        {
            if (Host == null || Slot == null) return;

            // Host hidden (its GameObject deactivated) — collapse with it rather than leaving the
            // slot and frame floating.
            if (!Host.gameObject.activeInHierarchy)
            {
                if (Slot.gameObject.activeSelf) Slot.gameObject.SetActive(false);
                if (_frame != null && _frame.gameObject.activeSelf) _frame.gameObject.SetActive(false);
                return;
            }

            if (!Slot.gameObject.activeSelf) return;

            _resolved = ResolveDirection();
            LayoutSlot();
            LayoutFrame();
        }

        /// <summary>
        /// Auto picks the side with enough screen room, preferring horizontal (a flight deck panel
        /// beside a list reads better than stacked). Re-evaluated every frame so dragging the host
        /// toward an edge flips the child to the other side rather than pushing it off-screen.
        /// </summary>
        private DockExpand ResolveDirection()
        {
            if (Expand != DockExpand.Auto) return Expand;

            Rect host = WindowDock.ScreenRectOf(Host);
            float need = _childSizePx.x + Gap;

            if (Screen.width - host.xMax >= need) return DockExpand.Right;
            if (host.xMin >= need) return DockExpand.Left;

            float needV = _childSizePx.y + Gap;
            if (Screen.height - host.yMax >= needV) return DockExpand.Down;
            if (host.yMin >= needV) return DockExpand.Up;

            return DockExpand.Right;
        }

        private void LayoutSlot()
        {
            Vector2 hostPos = Host.anchoredPosition;
            Vector2 hostSize = Host.sizeDelta;

            Slot.sizeDelta = _childSizePx;

            switch (_resolved)
            {
                case DockExpand.Left:
                    Slot.anchoredPosition = new Vector2(hostPos.x - Gap - _childSizePx.x, hostPos.y);
                    break;
                case DockExpand.Up:
                    Slot.anchoredPosition = new Vector2(hostPos.x, hostPos.y + Gap + _childSizePx.y);
                    break;
                case DockExpand.Down:
                    Slot.anchoredPosition = new Vector2(hostPos.x, hostPos.y - hostSize.y - Gap);
                    break;
                default: // Right
                    Slot.anchoredPosition = new Vector2(hostPos.x + hostSize.x + Gap, hostPos.y);
                    break;
            }
        }

        /// <summary>
        /// Draws four thin strips around the union of host and slot. Strips rather than a filled
        /// Image because the interior has to stay see-through for the native panel.
        /// </summary>
        private void LayoutFrame()
        {
            Vector2 hostPos = Host.anchoredPosition;
            Vector2 hostSize = Host.sizeDelta;
            Vector2 slotPos = Slot.anchoredPosition;
            Vector2 slotSize = Slot.sizeDelta;

            float left = Mathf.Min(hostPos.x, slotPos.x);
            float right = Mathf.Max(hostPos.x + hostSize.x, slotPos.x + slotSize.x);
            float top = Mathf.Max(hostPos.y, slotPos.y);
            float bottom = Mathf.Min(hostPos.y - hostSize.y, slotPos.y - slotSize.y);

            float width = right - left;
            float height = top - bottom;

            _frame.anchoredPosition = new Vector2(left, top);
            _frame.sizeDelta = new Vector2(width, height);

            SetStrip(_frameStrips[0], new Vector2(0f, 0f), new Vector2(width, BorderThickness));                     // top
            SetStrip(_frameStrips[1], new Vector2(0f, -height + BorderThickness), new Vector2(width, BorderThickness)); // bottom
            SetStrip(_frameStrips[2], new Vector2(0f, 0f), new Vector2(BorderThickness, height));                    // left
            SetStrip(_frameStrips[3], new Vector2(width - BorderThickness, 0f), new Vector2(BorderThickness, height)); // right
        }

        private static void SetStrip(Image strip, Vector2 anchoredPos, Vector2 size)
        {
            RectTransform rect = strip.rectTransform;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;
        }

        private void OnDestroy()
        {
            if (Slot != null) Destroy(Slot.gameObject);
            if (_frame != null) Destroy(_frame.gameObject);
        }

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }
    }
}

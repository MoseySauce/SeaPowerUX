using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using BepInEx.Logging;
using SeapowerUI.ViewModels;
using UniRx;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// A UGUI-rendered clone of the game's own right-click context menu, styled to match the
    /// native NTDS theme as closely as practical. Built as an alternative to
    /// <see cref="GameContextMenu"/> (which triggers the game's real Noesis
    /// <c>ContextMenu</c>): Noesis draws via a camera-attached pass that always composites
    /// BEFORE Unity's ScreenSpaceOverlay canvases, so a native menu triggered from a UGUI window
    /// forces that window to hide itself for the menu to be visible at all. Because this menu is
    /// itself plain UGUI on the SAME canvas as the host window, it renders above the window
    /// naturally via normal sibling order — no hiding required.
    ///
    /// Colors/metrics extracted from the game's own theme resources
    /// (Sea Power_Data/globalgamemanagers.assets, ContextMenu/MenuItem styles and their
    /// Color.* resource values) so this reads as close to the native menu as reasonably
    /// achievable without using Noesis: background #2b2d31, border/hover #393d40, hover text
    /// #d7d7d7, disabled text #3f4147, shortcut/placeholder text #949aa1, 12px font.
    ///
    /// Renders <see cref="ContextMenuItem"/> data directly (same model the native menu binds
    /// to) and executes <see cref="ContextMenuItem.Command"/> on click — functionally identical
    /// actions, just our own rendering/interaction layer.
    ///
    /// LIVE UPDATES: IsEnabled/IsChecked are UniRx ReadOnlyReactiveProperty&lt;bool&gt; — each
    /// row subscribes directly to them (they implement IObservable&lt;bool&gt;), so a state
    /// change while the menu is open (e.g. an order becoming unavailable mid-engagement)
    /// updates that row in place. The item collection itself
    /// (TrulyObservableCollection&lt;ContextMenuItem&gt; : ObservableCollection&lt;T&gt;,
    /// INotifyCollectionChanged) is also watched; any add/remove/reset triggers a full row
    /// rebuild (simpler and safer than incremental patching, at the cost of a visible refresh
    /// for the rare case of the item set itself changing shape while open).
    ///
    /// REMAINING SIMPLIFICATIONS (first pass — "good enough for now, refine as we go"):
    /// - Submenus open on click, not hover, and clicking outside a submenu closes only that
    ///   submenu, not the whole chain up to the root.
    /// - No separator support, no keyboard navigation.
    /// - Label is assumed string-convertible (ContextMenuItem.Label is typed as `object`).
    /// </summary>
    public static class UguiContextMenu
    {
        private static readonly Color BgColor = HexColor("#2b2d31");
        private static readonly Color BorderColor = HexColor("#393d40");
        private static readonly Color HoverBgColor = HexColor("#393d40");
        private static readonly Color NormalTextColor = HexColor("#d7d7d7");
        private static readonly Color DisabledTextColor = HexColor("#3f4147");
        private static readonly Color ShortcutTextColor = HexColor("#949aa1");
        private const int FontSize = 12;
        private const int RowPaddingH = 10;
        private const int RowPaddingV = 6;
        private const int RowHeight = 26;
        private const int CheckColumnWidth = 18;
        private const int ArrowColumnWidth = 16;
        private const int MinRowWidth = 140;

        /// <summary>
        /// Opens a context menu for <paramref name="items"/> at <paramref name="unityScreenPoint"/>
        /// (Unity screen space, bottom-left origin) as the last sibling under <paramref name="canvas"/>.
        /// <paramref name="onClosed"/> fires once, whenever this menu (and any open submenu chain
        /// under it) is fully closed — click-away, Escape, or a leaf item being clicked.
        /// </summary>
        public static GameObject Show(Canvas canvas, IEnumerable<ContextMenuItem> items, Vector2 unityScreenPoint, Action onClosed, ManualLogSource logger)
        {
            return BuildMenuPanel(canvas, items, unityScreenPoint, onClosed, logger);
        }

        private static GameObject BuildMenuPanel(Canvas canvas, IEnumerable<ContextMenuItem> items, Vector2 unityScreenPoint, Action onClosed, ManualLogSource logger)
        {
            GameObject root = new GameObject("UguiContextMenu");
            root.transform.SetParent(canvas.transform, false);
            root.transform.SetAsLastSibling();

            RectTransform panelRect = root.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            float topLeftY = Screen.height - unityScreenPoint.y;
            panelRect.anchoredPosition = new Vector2(unityScreenPoint.x, -topLeftY);

            Image bg = root.AddComponent<Image>();
            bg.color = BgColor;

            Outline border = root.AddComponent<Outline>();
            border.effectColor = BorderColor;
            border.effectDistance = new Vector2(1f, -1f);

            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(2, 2, 4, 4);

            ContentSizeFitter fitter = root.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement minWidth = root.AddComponent<LayoutElement>();
            minWidth.minWidth = MinRowWidth;

            UguiContextMenuController controller = root.AddComponent<UguiContextMenuController>();
            controller.Initialize(canvas, items, onClosed, logger);

            return root;
        }

        internal static void BuildRow(Transform parent, ContextMenuItem item, Canvas canvas, UguiContextMenuController controller, ManualLogSource logger)
        {
            bool hasSubmenu = item.Items != null && item.Items.Count > 0;

            GameObject rowGo = new GameObject("Row");
            rowGo.transform.SetParent(parent, false);
            rowGo.AddComponent<RectTransform>();

            LayoutElement rowLayout = rowGo.AddComponent<LayoutElement>();
            rowLayout.minHeight = RowHeight;
            rowLayout.preferredHeight = RowHeight;

            Image rowBg = rowGo.AddComponent<Image>();
            rowBg.color = new Color(0f, 0f, 0f, 0f);

            Button rowButton = rowGo.AddComponent<Button>();
            rowButton.targetGraphic = rowBg;
            ColorBlock colors = rowButton.colors;
            colors.normalColor = new Color(0f, 0f, 0f, 0f);
            colors.highlightedColor = HoverBgColor;
            colors.pressedColor = HoverBgColor;
            colors.disabledColor = new Color(0f, 0f, 0f, 0f);
            rowButton.colors = colors;

            HorizontalLayoutGroup rowLayoutGroup = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowLayoutGroup.padding = new RectOffset(RowPaddingH, RowPaddingH, RowPaddingV, RowPaddingV);
            rowLayoutGroup.childAlignment = TextAnchor.MiddleLeft;
            rowLayoutGroup.childControlWidth = false;
            rowLayoutGroup.childControlHeight = true;
            rowLayoutGroup.childForceExpandWidth = false;
            rowLayoutGroup.childForceExpandHeight = true;
            rowLayoutGroup.spacing = 4;

            // Checkmark column.
            Text check = CreateText(rowGo.transform, string.Empty, CheckColumnWidth, NormalTextColor, TextAnchor.MiddleCenter);

            // Label (flexible width).
            Text label = CreateText(rowGo.transform, item.Label?.ToString() ?? string.Empty, 0, NormalTextColor, TextAnchor.MiddleLeft);
            LayoutElement labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.flexibleWidth = 1f;
            labelLayout.minWidth = 60f;

            // Shortcut text, if any.
            string shortcut = item.ShortcutText;
            if (!string.IsNullOrEmpty(shortcut))
            {
                CreateText(rowGo.transform, shortcut, 0, ShortcutTextColor, TextAnchor.MiddleRight);
            }

            // Submenu arrow column.
            Text arrow = CreateText(rowGo.transform, hasSubmenu ? "▶" : "", ArrowColumnWidth, NormalTextColor, TextAnchor.MiddleCenter);

            UguiContextMenuRow rowHandler = rowGo.AddComponent<UguiContextMenuRow>();
            rowHandler.Initialize(item, canvas, controller, hasSubmenu, rowButton, check, label, arrow, logger);
            rowButton.onClick.AddListener(rowHandler.HandleClick);
        }

        internal static Text CreateText(Transform parent, string content, float fixedWidth, Color color, TextAnchor alignment)
        {
            GameObject go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            if (fixedWidth > 0f)
            {
                LayoutElement le = go.AddComponent<LayoutElement>();
                le.preferredWidth = fixedWidth;
                le.minWidth = fixedWidth;
            }

            Text text = go.AddComponent<Text>();
            text.text = content;
            text.font = UguiWindow.GetUiFont();
            text.fontSize = FontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        internal static Color NormalColor => NormalTextColor;
        internal static Color DisabledColor => DisabledTextColor;

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        internal static GameObject BuildSubmenu(Canvas canvas, IEnumerable<ContextMenuItem> items, Vector2 unityScreenPoint, Action onClosed, ManualLogSource logger)
        {
            return BuildMenuPanel(canvas, items, unityScreenPoint, onClosed, logger);
        }
    }

    /// <summary>
    /// Owns one menu panel's lifecycle: the full-screen click-away blocker, Escape-to-close,
    /// closing exactly one open submenu (not the whole chain — see the class doc on
    /// <see cref="UguiContextMenu"/>), and rebuilding rows when the underlying item collection
    /// itself changes shape.
    /// </summary>
    internal class UguiContextMenuController : MonoBehaviour
    {
        private Canvas _canvas;
        private Action _onClosed;
        private ManualLogSource _logger;
        private GameObject _blocker;
        private GameObject _openChild;
        private IEnumerable<ContextMenuItem> _items;
        private INotifyCollectionChanged _itemsNotifier;

        public void Initialize(Canvas canvas, IEnumerable<ContextMenuItem> items, Action onClosed, ManualLogSource logger)
        {
            _canvas = canvas;
            _items = items;
            _onClosed = onClosed;
            _logger = logger;
            BuildBlocker();
            RebuildRows();

            if (items is INotifyCollectionChanged notifier)
            {
                _itemsNotifier = notifier;
                _itemsNotifier.CollectionChanged += OnItemsCollectionChanged;
            }
        }

        private void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Any structural change (add/remove/reset) — full rebuild rather than incremental
            // patching, simpler and safer for a first pass. Per-item enabled/checked changes are
            // handled live per-row (see UguiContextMenuRow), not through this event.
            RebuildRows();
        }

        private void RebuildRows()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            foreach (ContextMenuItem item in _items)
            {
                UguiContextMenu.BuildRow(transform, item, _canvas, this, _logger);
            }
        }

        private void BuildBlocker()
        {
            GameObject blockerGo = new GameObject("UguiContextMenuBlocker");
            blockerGo.transform.SetParent(_canvas.transform, false);
            blockerGo.transform.SetSiblingIndex(transform.GetSiblingIndex());

            RectTransform rect = blockerGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image img = blockerGo.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);

            Button btn = blockerGo.AddComponent<Button>();
            btn.onClick.AddListener(Close);

            _blocker = blockerGo;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
            }
        }

        /// <summary>Closes any previously open submenu before tracking a new one.</summary>
        public void SetOpenChild(GameObject child)
        {
            CloseChildOnly();
            _openChild = child;
        }

        private void CloseChildOnly()
        {
            if (_openChild == null) return;
            UguiContextMenuController childController = _openChild.GetComponent<UguiContextMenuController>();
            if (childController != null)
            {
                childController.Close();
            }
            else
            {
                Destroy(_openChild);
            }
            _openChild = null;
        }

        public void Close()
        {
            CloseChildOnly();
            if (_itemsNotifier != null)
            {
                _itemsNotifier.CollectionChanged -= OnItemsCollectionChanged;
                _itemsNotifier = null;
            }
            if (_blocker != null)
            {
                Destroy(_blocker);
                _blocker = null;
            }
            try { _onClosed?.Invoke(); }
            catch (Exception e) { _logger?.LogWarning($"UguiContextMenu onClosed callback failed: {e.Message}"); }
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Per-row behavior: executes the item's command or opens its submenu on click, and keeps
    /// its own visuals (enabled/disabled dimming, interactability, checkmark) live-synced to
    /// the item's IsEnabled/IsChecked reactive properties for as long as the row exists.
    /// </summary>
    internal class UguiContextMenuRow : MonoBehaviour
    {
        private ContextMenuItem _item;
        private Canvas _canvas;
        private UguiContextMenuController _owner;
        private bool _hasSubmenu;
        private Button _button;
        private Text _checkText;
        private Text _labelText;
        private Text _arrowText;
        private ManualLogSource _logger;
        private bool _currentlyEnabled = true;

        private IDisposable _enabledSubscription;
        private IDisposable _checkedSubscription;

        public void Initialize(
            ContextMenuItem item,
            Canvas canvas,
            UguiContextMenuController owner,
            bool hasSubmenu,
            Button button,
            Text checkText,
            Text labelText,
            Text arrowText,
            ManualLogSource logger)
        {
            _item = item;
            _canvas = canvas;
            _owner = owner;
            _hasSubmenu = hasSubmenu;
            _button = button;
            _checkText = checkText;
            _labelText = labelText;
            _arrowText = arrowText;
            _logger = logger;

            ApplyEnabled(item.IsEnabled == null || item.IsEnabled.Value);
            if (item.IsCheckable)
            {
                ApplyChecked(item.IsChecked != null && item.IsChecked.Value);
            }

            if (item.IsEnabled != null)
            {
                _enabledSubscription = item.IsEnabled.Subscribe(ApplyEnabled);
            }
            if (item.IsCheckable && item.IsChecked != null)
            {
                _checkedSubscription = item.IsChecked.Subscribe(ApplyChecked);
            }
        }

        private void ApplyEnabled(bool enabled)
        {
            _currentlyEnabled = enabled;
            if (_button != null) _button.interactable = enabled;
            Color color = enabled ? UguiContextMenu.NormalColor : UguiContextMenu.DisabledColor;
            if (_labelText != null) _labelText.color = color;
            if (_checkText != null) _checkText.color = color;
            if (_arrowText != null) _arrowText.color = color;
        }

        private void ApplyChecked(bool isChecked)
        {
            if (_checkText != null) _checkText.text = isChecked ? "✓" : string.Empty;
        }

        private void OnDestroy()
        {
            _enabledSubscription?.Dispose();
            _checkedSubscription?.Dispose();
        }

        public void HandleClick()
        {
            if (!_currentlyEnabled) return;

            if (_hasSubmenu)
            {
                OpenSubmenu();
                return;
            }

            try
            {
                if (_item.Command != null && _item.Command.CanExecute(null))
                {
                    _item.Command.Execute(null);
                }
            }
            catch (Exception e)
            {
                _logger?.LogWarning($"UguiContextMenu row command failed: {e.Message}");
            }

            _owner.Close();
        }

        private void OpenSubmenu()
        {
            RectTransform rowRect = (RectTransform)transform;
            Vector3[] corners = new Vector3[4];
            rowRect.GetWorldCorners(corners); // [0]=bottomLeft [1]=topLeft [2]=topRight [3]=bottomRight
            Vector2 topRightScreen = RectTransformUtility.WorldToScreenPoint(null, corners[2]);

            GameObject submenu = UguiContextMenu.BuildSubmenu(_canvas, _item.Items, topRightScreen, null, _logger);
            _owner.SetOpenChild(submenu);
        }
    }
}

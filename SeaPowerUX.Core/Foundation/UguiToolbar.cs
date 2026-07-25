using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// A single small toolbar shared by every SeaPowerUX plugin, giving each one a clickable
    /// toggle for its window instead of requiring a hotkey.
    ///
    /// NOT the game's own bottom bar. Adding a button to the real bar would mean injecting into
    /// the native Noesis visual tree, and the bottom bar has no `x:Name` anywhere in the shipped
    /// XAML to anchor onto — identifying the right container blind would be guesswork. This is a
    /// separate UGUI strip instead, positioned just above the native bar. (If the real bar is
    /// wanted later, the path is to inspect the live visual tree with UnityExplorer, find the
    /// container element, and add a `Noesis.Button` to it programmatically — plain Noesis calls,
    /// no XAML injection or Harmony patching needed.)
    ///
    /// Buttons are drag-repositionable (the whole strip moves) and reflect their on/off state by
    /// polling the predicate supplied at registration, so a window toggled by hotkey still shows
    /// the right state on its button.
    /// </summary>
    public static class UguiToolbar
    {
        private static readonly Color BarColor = HexColor("#2b2d31");
        private static readonly Color BorderColor = HexColor("#3f4147");
        private static readonly Color TextColor = HexColor("#d7d7d7");
        private static readonly Color OnColor = HexColor("#4a7ba7");

        private const float ButtonHeight = 22f;

        // Coded default toolbar position, in DIP (logical) units in the bar's bottom-centre-anchored
        // space: X is offset from screen centre, Y is measured up from the bottom edge. Captured
        // in-game (upper-right). Multiplied by NoesisDipScale at apply time so it tracks the game's
        // own UI scaling across resolutions; ClampIntoScreen keeps it reachable regardless.
        private const float DefaultOffsetX = 597f;
        private const float DefaultOffsetY = 1050f;

        private static RectTransform _bar;
        private static ConfigEntry<string> _positionEntry;

        /// <summary>
        /// Adds a toggle button. <paramref name="isOn"/> is polled to tint the button when the
        /// window is open; <paramref name="onClick"/> should flip that state.
        /// </summary>
        /// <summary>
        /// A two-segment button sharing one frame: the main action on the left, a related one on
        /// the right. Used for the defaults pair, where "Set Default" and "Reset" belong together
        /// but must not be confusable with each other.
        /// </summary>
        /// <summary>
        /// A split button: a primary action shown as a normal button, plus a caret that drops down a
        /// menu of secondary actions — hidden until the caret is clicked. Reusable for any future
        /// split-button need; the current use is Reset (primary) with Set Default tucked in the menu.
        /// </summary>
        public static void AddSplitButton(string primaryLabel, Action primaryAction, params (string label, Action action)[] menuItems)
        {
            RectTransform bar = EnsureBar();

            GameObject groupGo = new GameObject("Split_" + primaryLabel);
            groupGo.transform.SetParent(bar, false);
            RectTransform groupRect = groupGo.AddComponent<RectTransform>();

            LayoutElement groupLayout = groupGo.AddComponent<LayoutElement>();
            groupLayout.preferredWidth = Width(primaryLabel) + CaretWidth + 1f;
            groupLayout.minWidth = groupLayout.preferredWidth;
            groupLayout.preferredHeight = ButtonHeight;

            HorizontalLayoutGroup groupLayoutGroup = groupGo.AddComponent<HorizontalLayoutGroup>();
            groupLayoutGroup.spacing = 1f; // the gap reads as the divider between primary and caret
            groupLayoutGroup.childControlWidth = true;
            groupLayoutGroup.childControlHeight = true;
            groupLayoutGroup.childForceExpandWidth = false;
            groupLayoutGroup.childForceExpandHeight = true;

            CreateSegment(groupGo.transform, primaryLabel, primaryAction);
            CreateCaret(groupGo.transform, () => ToggleDropdown(groupRect, menuItems));
        }

        private const float CaretWidth = 16f;

        private static GameObject _openDropdown;
        private static GameObject _dropdownBlocker;

        private static void CreateCaret(Transform parent, Action onClick)
        {
            GameObject go = new GameObject("Caret");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.preferredWidth = CaretWidth;
            le.minWidth = CaretWidth;
            le.preferredHeight = ButtonHeight;

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.10f);

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener(() => onClick());

            CreateLabel(go.transform, "▾"); // ▾
        }

        private static void ToggleDropdown(RectTransform anchor, (string label, Action action)[] items)
        {
            if (_openDropdown != null)
            {
                CloseDropdown();
                return;
            }
            OpenDropdown(anchor, items);
        }

        private static void OpenDropdown(RectTransform anchor, (string label, Action action)[] items)
        {
            Canvas canvas = UguiWindowFactory.GetOrCreateSharedCanvas();

            // Full-screen invisible catcher: a click anywhere off the menu closes it. Same pattern as
            // UguiContextMenu. Created before the panel so the panel sits above it in sibling order.
            GameObject blocker = new GameObject("SplitDropdownBlocker");
            blocker.transform.SetParent(canvas.transform, false);
            RectTransform brect = blocker.AddComponent<RectTransform>();
            brect.anchorMin = Vector2.zero;
            brect.anchorMax = Vector2.one;
            brect.offsetMin = Vector2.zero;
            brect.offsetMax = Vector2.zero;
            blocker.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            blocker.AddComponent<Button>().onClick.AddListener(CloseDropdown);
            _dropdownBlocker = blocker;

            // GetWorldCorners on a ScreenSpaceOverlay canvas yields screen pixels (bottom-left
            // origin): corner 0 is the button's bottom-left, corner 1 its top-left.
            Vector3[] corners = new Vector3[4];
            anchor.GetWorldCorners(corners);

            GameObject panel = new GameObject("SplitDropdown");
            panel.transform.SetParent(canvas.transform, false);
            panel.transform.SetAsLastSibling();
            RectTransform prect = panel.AddComponent<RectTransform>();
            prect.anchorMin = new Vector2(0f, 1f);
            prect.anchorMax = new Vector2(0f, 1f);

            panel.AddComponent<Image>().color = BarColor;
            Outline border = panel.AddComponent<Outline>();
            border.effectColor = BorderColor;
            border.effectDistance = new Vector2(1f, -1f);

            VerticalLayoutGroup vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(1, 1, 1, 1);
            vlg.spacing = 1f;

            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach ((string label, Action action) in items)
            {
                Action captured = action;
                // Close first so a Reset/Set-Default that repositions the bar doesn't fight the menu.
                CreateSegment(panel.transform, label, () => { CloseDropdown(); captured?.Invoke(); });
            }

            // Place only after the content is measured: drop DOWN from the button's bottom edge if the
            // menu fits, otherwise flip UP from its top edge (toolbar parked near the screen bottom).
            LayoutRebuilder.ForceRebuildLayoutImmediate(prect);
            bool flipUp = corners[0].y - prect.rect.height < 0f;
            Vector3 attach = flipUp ? corners[1] : corners[0];   // top-left when flipped, else bottom-left
            prect.pivot = new Vector2(0f, flipUp ? 0f : 1f);
            prect.anchoredPosition = new Vector2(attach.x, -(Screen.height - attach.y));

            _openDropdown = panel;
        }

        private static void CloseDropdown()
        {
            if (_openDropdown != null) { UnityEngine.Object.Destroy(_openDropdown); _openDropdown = null; }
            if (_dropdownBlocker != null) { UnityEngine.Object.Destroy(_dropdownBlocker); _dropdownBlocker = null; }
        }

        private static void CreateSegment(Transform parent, string label, Action onClick)
        {
            GameObject go = new GameObject("Segment_" + label);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.preferredWidth = Width(label);
            le.minWidth = le.preferredWidth;
            le.preferredHeight = ButtonHeight;

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.10f);

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener(() => onClick());

            CreateLabel(go.transform, label);
        }

        private static float Width(string label)
        {
            return Mathf.Max(48f, label.Length * 7.5f + 14f);
        }

        private static void CreateLabel(Transform parent, string label)
        {
            GameObject labelGo = new GameObject("Label");
            RectTransform labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.SetParent(parent, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            Text text = labelGo.AddComponent<Text>();
            text.text = label;
            text.font = UguiWindow.GetUiFont();
            text.fontSize = 12;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = TextColor;
            text.raycastTarget = false;
        }

        public static void AddToggle(string label, Func<bool> isOn, Action onClick)
        {
            RectTransform bar = EnsureBar();

            GameObject go = new GameObject("Toggle_" + label);
            go.transform.SetParent(bar, false);
            go.AddComponent<RectTransform>();

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.preferredWidth = Mathf.Max(64f, label.Length * 7.5f + 18f);
            le.minWidth = le.preferredWidth;
            le.preferredHeight = ButtonHeight;

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.10f);

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.onClick.AddListener(() => onClick());

            GameObject labelGo = new GameObject("Label");
            RectTransform labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.SetParent(go.transform, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            Text text = labelGo.AddComponent<Text>();
            text.text = label;
            text.font = UguiWindow.GetUiFont();
            text.fontSize = 12;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = TextColor;
            text.raycastTarget = false;

            ToggleState state = go.AddComponent<ToggleState>();
            state.Initialize(bg, isOn);
        }

        private static void CaptureDefaults()
        {
            UxSettings.CaptureCurrentAsDefaults();
        }

        private static void ResetDefaults()
        {
            UxSettings.ResetAllToDefaults();
        }

        private static RectTransform EnsureBar()
        {
            if (_bar != null) return _bar;

            Canvas canvas = UguiWindowFactory.GetOrCreateSharedCanvas();

            GameObject barGo = new GameObject("SeaPowerUX_Toolbar");
            _bar = barGo.AddComponent<RectTransform>();
            _bar.SetParent(canvas.transform, false);
            // Bottom-centre, lifted clear of the game's own status bar.
            _bar.anchorMin = new Vector2(0.5f, 0f);
            _bar.anchorMax = new Vector2(0.5f, 0f);
            _bar.pivot = new Vector2(0.5f, 0f);
            _bar.anchoredPosition = LoadSavedPosition();

            Image bg = barGo.AddComponent<Image>();
            bg.color = BarColor;

            Outline border = barGo.AddComponent<Outline>();
            border.effectColor = BorderColor;
            border.effectDistance = new Vector2(1f, -1f);

            HorizontalLayoutGroup hlg = barGo.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(4, 4, 4, 4);
            hlg.spacing = 4f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            ContentSizeFitter fitter = barGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Dragging the bar itself repositions it (buttons sit on top and take their own clicks).
            WindowDragHandler drag = barGo.AddComponent<WindowDragHandler>();
            drag.Target = _bar;
            drag.OnMoveEnd = SaveCurrentPosition;

            // The toolbar is UI too — don't let clicks on it fall through and pan the map.
            NoesisInputSuppressor.Register(_bar, () => _bar != null && _bar.gameObject.activeInHierarchy);

            // Owned by the bar rather than by a plugin: the defaults apply pack-wide, so this
            // shouldn't disappear because one plugin happens to be disabled. Reset is the primary
            // action; the rarer Set Default is tucked behind the caret.
            AddSplitButton("Reset", ResetDefaults, ("Set Default", CaptureDefaults));

            // Re-apply the toolbar's own saved position after a reset — the value changing behind
            // it doesn't move it.
            UxSettings.DefaultsRestored += RepositionForScreen;

            // Plugins register their buttons at Awake, which is long before any mission exists —
            // without this the bar would float over the main menu and loading screens.
            barGo.AddComponent<TacticalSceneGate>();

            return _bar;
        }

        /// <summary>
        /// Position is stored relative to the bar's bottom-centre anchor, so it survives a
        /// resolution change sensibly rather than drifting off one edge.
        /// </summary>
        private static ConfigEntry<string> PositionEntry
        {
            get
            {
                if (_positionEntry == null)
                {
                    _positionEntry = UxSettings.Bind(
                        "Toolbar", "Position", string.Empty,
                        "Where the SeaPowerUX toolbar sits, as \"x,y\" offsets from the bottom-centre of the screen. Clear to reset.");
                }
                return _positionEntry;
            }
        }

        private static Vector2 LoadSavedPosition()
        {
            string raw = PositionEntry.Value;
            if (!string.IsNullOrEmpty(raw))
            {
                string[] parts = raw.Split(',');
                if (parts.Length == 2 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    // Deliberately NOT screen-bounds-checked here. This runs from a plugin's
                    // Awake(), long before the game applies its resolution, so Screen.width is
                    // still Unity's startup window size — a perfectly good saved position tested
                    // against it looks out of range and gets thrown away, losing the placement on
                    // every launch. Bounds are enforced later by ClampIntoScreen(), which nudges
                    // rather than discards.
                    return new Vector2(x, y);
                }
            }

            return ResolveDefaultOffset();
        }

        /// <summary>
        /// The coded default position in pixels, scaled from DIP by the game's UI scale so a fresh
        /// install places the bar consistently across resolutions. Meaningful only once the screen
        /// size is real (in-scene) — <see cref="RepositionForScreen"/> recomputes it then.
        /// </summary>
        private static Vector2 ResolveDefaultOffset()
        {
            float scale = NoesisInterop.NoesisDipScale;
            if (scale <= 0f) scale = 1f;
            return new Vector2(DefaultOffsetX * scale, DefaultOffsetY * scale);
        }

        /// <summary>
        /// Re-applies the bar's position (saved pixels, or the UIScale-aware default) and clamps it
        /// on-screen. Called once the screen size is known and whenever it changes, so the scaled
        /// default is resolved against the real resolution rather than Unity's startup window size.
        /// </summary>
        private static void RepositionForScreen()
        {
            if (_bar == null) return;
            _bar.anchoredPosition = LoadSavedPosition();
            ClampIntoScreen();
        }

        private static void SaveCurrentPosition()
        {
            if (_bar == null) return;

            Vector2 pos = _bar.anchoredPosition;
            PositionEntry.Value =
                pos.x.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                pos.y.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Keeps the bar reachable without throwing the player's placement away: a position saved
        /// at a larger resolution is pulled back inside the current screen rather than reset.
        /// Runs once the screen size is real, and again whenever it changes.
        /// </summary>
        private static void ClampIntoScreen()
        {
            if (_bar == null) return;

            // The bar is sized by a ContentSizeFitter, which may not have run yet when this fires on
            // the first in-scene frame. Force the layout so sizeDelta is the real content size —
            // otherwise the clamp reads the RectTransform's default 100px height and drags the bar
            // down from a top-edge default (the "too far down on cold start" symptom).
            LayoutRebuilder.ForceRebuildLayoutImmediate(_bar);

            // Anchored bottom-centre, so x is an offset either side of the middle.
            float halfWidth = Mathf.Max(1f, Screen.width * 0.5f);
            float barHalfWidth = _bar.sizeDelta.x * 0.5f;

            Vector2 pos = _bar.anchoredPosition;
            pos.x = Mathf.Clamp(pos.x, -halfWidth + barHalfWidth, halfWidth - barHalfWidth);
            pos.y = Mathf.Clamp(pos.y, 0f, Mathf.Max(0f, Screen.height - _bar.sizeDelta.y));

            if (pos != _bar.anchoredPosition) _bar.anchoredPosition = pos;
        }

        /// <summary>
        /// Shows the toolbar only while an actual tactical scene is up. `Globals._mainGameViewModel`
        /// is the game's own in-mission HUD view model (and the game itself checks
        /// `.gameObject.activeSelf` on it for exactly this kind of "is the HUD live" test).
        /// </summary>
        private class TacticalSceneGate : MonoBehaviour
        {
            private const float CheckInterval = 0.25f;
            private float _timer;
            private Vector2Int _lastScreen;

            private void Start()
            {
                Apply(InTacticalScene());
            }

            private void Update()
            {
                _timer += Time.unscaledDeltaTime;
                if (_timer < CheckInterval) return;
                _timer = 0f;
                Apply(InTacticalScene());

                // The real resolution isn't known at Awake, and can change at runtime, so bounds
                // are enforced here rather than when the saved position was read.
                var screen = new Vector2Int(Screen.width, Screen.height);
                if (screen != _lastScreen)
                {
                    _lastScreen = screen;
                    RepositionForScreen();
                }
            }

            private static bool InTacticalScene()
            {
                try
                {
                    return SeaPower.Globals._mainGameViewModel != null
                        && SeaPower.Globals._mainGameViewModel.gameObject.activeSelf
                        && SeapowerUI.MainGameView.Instance != null;
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>
            /// Hides by toggling the children rather than this GameObject, so Update keeps
            /// running (disabling the bar itself would stop this gate from ever re-enabling it).
            /// </summary>
            private void Apply(bool visible)
            {
                Image background = GetComponent<Image>();
                if (background != null) background.enabled = visible;

                Outline outline = GetComponent<Outline>();
                if (outline != null) outline.enabled = visible;

                for (int i = 0; i < transform.childCount; i++)
                {
                    GameObject child = transform.GetChild(i).gameObject;
                    if (child.activeSelf != visible) child.SetActive(visible);
                }
            }
        }

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        /// <summary>Tints a toolbar button while its window is open.</summary>
        private class ToggleState : MonoBehaviour
        {
            private Image _background;
            private Func<bool> _isOn;
            private bool _lastState;

            public void Initialize(Image background, Func<bool> isOn)
            {
                _background = background;
                _isOn = isOn;
                Apply(isOn != null && isOn());
            }

            private void Update()
            {
                if (_isOn == null) return;
                bool now = _isOn();
                if (now != _lastState) Apply(now);
            }

            private void Apply(bool on)
            {
                _lastState = on;
                if (_background != null)
                {
                    _background.color = on ? OnColor : new Color(1f, 1f, 1f, 0.10f);
                }
            }
        }
    }
}

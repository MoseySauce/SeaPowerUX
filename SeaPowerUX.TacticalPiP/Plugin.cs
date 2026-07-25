using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeaPower;
using SeaPowerUX.Core.Foundation;
using SeapowerUI.ViewModels;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SeaPowerUX.TacticalPiP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.moseysauce.seapowerux.tacticalpip";
        public const string PluginName = "SeaPowerUX.TacticalPiP";
        /// <summary>Core build this plugin was compiled against; see CoreCompatibility.</summary>
        private const string RequiredCoreVersion = "0.5.0";

        public const string PluginVersion = "0.5.0";

        private const int PipSortingOrder = 32760;
        private const int MinPipWidth = 160;
        private const int MinPipHeight = 90;
        private const int TitleBarHeight = 24;
        private const int MaxRenderWidth = 4096;

        // Coded default rect in DIP (logical) units, captured in-game to overlay the native tactical
        // map. Multiplied by NoesisDipScale at apply time so it tracks the game's own UI scaling
        // across resolutions and UI-scale settings. Used only while the config value is unset
        // (position = Auto, size = 0); once the player moves or resizes, real pixel values are saved.
        private const int DefaultDipX = 1;
        private const int DefaultDipY = 642;
        private const int DefaultDipWidth = 400;
        private const int DefaultDipHeight = 355;

        // The game clamps pitch to this in CameraBase (camera.ini "MaxPitchAngle", default 80).
        private const float MaxPitchAngle = 80f;

        /// <summary>What a click-drag inside the PiP body does.</summary>
        public enum CameraDragMode
        {
            /// <summary>Drive the game's real camera rig (main view + PiP move together).</summary>
            GameCamera,
            /// <summary>Body drag does nothing.</summary>
            Off
        }

        // Config
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<int> _initialWidth;
        private ConfigEntry<int> _initialHeight;
        private ConfigEntry<WindowLock> _windowLock;
        private ConfigEntry<int> _initialX;
        private ConfigEntry<int> _initialY;
        private ConfigEntry<float> _renderScale;
        private ConfigEntry<float> _cameraRefreshInterval;
        private ConfigEntry<CameraDragMode> _cameraDragMode;
        private ConfigEntry<float> _dragSensitivity;
        private ConfigEntry<bool> _invertY;

        // Static log handle so Harmony patch classes (which have no plugin instance) can log.
        internal static ManualLogSource Log;

        private Harmony _harmony;

        // Source camera we mirror, and its gimbal rig (resolved from parents).
        private Camera _sourceCamera;
        private Transform _xRotator; // pitch (local X)
        private Transform _yRotator; // yaw (world Y)
        private float _timeSinceLastCameraRefresh;

        // PiP scene objects.
        private Canvas _pipCanvas;
        private UguiWindow _window;
        private WindowGeometry _geometry;
        private RectTransform _pipPanelRect;
        private RectTransform _contentRect;
        private RectTransform _viewFrame;
        // Fraction of the source width currently visible; the render texture is sized so the
        // VISIBLE slice keeps roughly 1:1 pixel density rather than the whole (cropped) frame.
        private float _cropU = 1f;
        private RawImage _pipImage;
        private RenderTexture _renderTexture;
        private PipFrameCapture _capture; // lives on the source camera; blits its frame into _renderTexture
        private bool _pipVisible; // off by default; shown while the fullscreen map (Tab) covers the 3D view

        // Collapse (rollup) state.
        private bool _collapsed;

        // Tracks the game's fullscreen-map (Tab) state so PiP visibility follows it.
        private bool _wasMapFullscreen;

        // Set when startup validation finds the game's API changed; the mod then does nothing.
        private bool _incompatible;

        // While a context menu we opened from the PiP is showing: the PiP is hidden (Noesis
        // draws context menus via a camera-attached pass that always composites BEFORE Unity's
        // ScreenSpaceOverlay canvases, so no sortingOrder value on our canvas can put it above
        // the menu — hiding the PiP is the only way to make the menu visible/reachable), and
        // Noesis mouse suppression is held off so the menu itself is clickable.
        private bool _contextMenuOpenForPip;
        private bool _contextMenuClosedHandlerAttached;

        // The game uses ONE shared ContextMenu instance, so our AbsolutePoint override has to be
        // undone on close — otherwise every normal right-click in the 3D view afterwards inherits
        // our placement mode and stale offsets instead of appearing at the cursor.
        private bool _menuPlacementOverridden;
        private Noesis.PlacementMode _savedMenuPlacement;
        private float _savedMenuHorizontalOffset;
        private float _savedMenuVerticalOffset;

        // GameCamera drag: accumulated angles, seeded on drag begin.
        private float _driveYaw;
        private float _drivePitch;

        private void Awake()
        {
            Log = Logger;

            // Before anything else touches Core: a mismatched or missing Core otherwise fails
            // later as an opaque type-load error with nothing naming the cause.
            if (!CoreVersionOk()) return;

            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.Backslash,
                "Key that shows/hides the picture-in-picture window.");
            _initialWidth = Config.Bind("General", "InitialWidth", 0,
                "PiP window width in pixels. Updated when you resize the window. 0 = UIScale-aware default.");
            _initialHeight = Config.Bind("General", "InitialHeight", 0,
                "PiP content height in pixels (excludes the title bar). Updated when you resize the window. 0 = UIScale-aware default.");
            _initialX = Config.Bind("General", "InitialX", WindowDefaults.Auto,
                "PiP window X position in pixels from the left edge. Updated when you move the window. -1 = UIScale-aware default.");
            _initialY = Config.Bind("General", "InitialY", WindowDefaults.Auto,
                "PiP window Y position in pixels from the top edge. Updated when you move the window. -1 = UIScale-aware default.");
            _windowLock = Config.Bind("General", "WindowLock", WindowLock.None,
                "Holds the PiP window's position and/or size. Cycled by the lock button in the title bar.");
            _renderScale = Config.Bind("General", "RenderScale", 1.0f,
                "Multiplier applied to the PiP view size to get the capture RenderTexture resolution. Lower this to save GPU cost.");
            _cameraRefreshInterval = Config.Bind("General", "CameraRefreshIntervalSeconds", 2.0f,
                "How often (seconds) to re-check for the main camera in case it was replaced or the scene changed.");

            _cameraDragMode = Config.Bind("Camera", "DragMode", CameraDragMode.GameCamera,
                "What click-dragging inside the PiP body does. GameCamera = orbit the game's real camera (main view and PiP move together). Off = disabled.");
            _dragSensitivity = Config.Bind("Camera", "DragSensitivity", 0.2f,
                "Degrees of camera rotation per pixel dragged.");
            _invertY = Config.Bind("Camera", "InvertY", false,
                "Invert the vertical drag direction.");

            // Verify the specific game methods/members we hook still exist and match. This is a
            // capability probe, not a version compare — it only trips on an ACTUAL breaking change
            // to something we depend on, and it disables the mod loudly instead of crashing later.
            if (!ValidateGameApi())
            {
                _incompatible = true;
                return;
            }

            _harmony = new Harmony(PluginGuid);

            // CameraBase.handleRotation prefix+postfix: prefix skips the game's own rotation while
            // dragging over the PiP (so window/title-bar drags don't rotate the camera); postfix
            // re-applies our GameCamera-driver rotation so body drags still work.
            // MouseControlState.OnUpdate postfix: re-enable camera mouse interaction over the PiP so
            // scroll-zoom keeps working even when the PiP sits over the map.
            PatchEach(
                typeof(CameraBaseHandleRotationPatch),
                typeof(MouseControlStateOnUpdatePatch));

            // Takes part in the toolbar's Set Default / Reset pair. (Geometry reset is wired by the
            // window's WindowGeometry in BuildUI.)
            UxSettings.RegisterConfig(Config);
            UxSettings.ApplyShippedDefaultsOnce();

            // Window position memory now lives in its own removable plugin — PiP neither starts
            // nor needs it.
            HudVisibility.Enable();
            // PiP draws on its own canvas rather than the pack's shared one, so it has to react
            // to the hide-HUD key itself instead of being covered by HudVisibility's blanket
            // switch. See ApplyCanvasEnabled().
            HudVisibility.Changed += OnHudVisibilityChanged;

            SceneManager.sceneLoaded += OnSceneLoaded;
            UguiToolbar.AddToggle("PiP", () => _pipVisible, () => SetPipVisible(!_pipVisible));

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        /// <summary>
        /// PiP's side of a geometry change: re-fit the mirrored view and rebuild its render target
        /// for the current window size. Runs after WindowGeometry applies geometry (build and reset)
        /// via its onApplied hook.
        /// </summary>
        private void RelayoutView()
        {
            LayoutView();
            AllocateRenderTexture(ContentWidth(), ContentHeight());
        }

        /// <summary>
        /// After a resize drag ends: keep the window on-screen and rebuild the render target at the
        /// final size. WindowGeometry persists the new size; this handles only the PiP-specific work
        /// that a plain window doesn't have. (During the drag, OnPipResized re-fits the crop live but
        /// leaves the texture resolution alone — reallocating every frame would be needless churn.)
        /// </summary>
        private void OnPipResizeEnded()
        {
            _window?.ClampIntoScreen();
            RelayoutView();
        }

        private void OnHudVisibilityChanged(bool hudVisible)
        {
            ApplyCanvasEnabled();
        }

        /// <summary>
        /// Isolated in its own method and called from a try/catch so a Core too old to contain the
        /// members it reads fails here, where we can explain it, rather than at plugin load.
        /// </summary>
        private bool CoreVersionOk()
        {
            try
            {
                return CoreCompatibility.Check(PluginName, RequiredCoreVersion, Logger);
            }
            catch (Exception e)
            {
                Logger.LogError(
                    $"{PluginName}: SeaPowerUX.Core is missing or incompatible ({e.GetType().Name}). " +
                    "Install every SeaPowerUX file from the same release. This plugin is disabled.");
                return false;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            HudVisibility.Changed -= OnHudVisibilityChanged;
            _geometry?.Dispose();
            _harmony?.UnpatchSelf();

            if (_contextMenuClosedHandlerAttached)
            {
                try
                {
                    Noesis.ContextMenu menu = SeapowerUI.MainGameView.Instance?.ContextMenuCanvas?.ContextMenu;
                    if (menu != null)
                    {
                        menu.Closed -= OnPipContextMenuClosed;
                    }
                }
                catch { }
            }

            // Remove the capture component so the main camera renders normally again.
            if (_capture != null)
            {
                Destroy(_capture);
                _capture = null;
            }
            ReleaseRenderTexture();

            // Unregistering restores mouse input on any view the shared suppressor switched off,
            // so the game's UI is never left unable to receive the mouse.
            if (_pipPanelRect != null)
            {
                NoesisInputSuppressor.Unregister(_pipPanelRect);
            }
        }

        /// <summary>
        /// Probes every game method/member this plugin hooks or calls, by reflection, to confirm
        /// the integration surface still matches. Returns false (and logs each missing point) if a
        /// game update changed anything we depend on — a targeted "breaking change" check rather
        /// than a version-string compare, so routine updates that don't touch our hooks pass.
        /// </summary>
        /// <summary>
        /// Confirms every game member this plugin touches still exists, using Core's shared
        /// validator. PiP previously carried its own inline copy of this logic — written before
        /// Core existed — which meant two implementations to keep in step for no benefit.
        /// </summary>
        private bool ValidateGameApi()
        {
            if (!CoreApi.Validate(Logger, PluginName, PluginVersion)) return false;

            return new GameApiValidator()
                // Camera rig: drag-to-orbit and scroll-zoom over the PiP.
                .Method(typeof(CameraBase), "handleRotation", "CameraBase.handleRotation (window-drag rotation control)")
                .Method(typeof(MouseControlState), "OnUpdate", "MouseControlState.OnUpdate (scroll-zoom over map)")
                .Method(typeof(RenderPosition), "setRotationXY", "RenderPosition.setRotationXY (camera drive)", new[] { typeof(float), typeof(float) })
                .Method(typeof(CameraManager), "setMouseInteractionFlag", "CameraManager.setMouseInteractionFlag (zoom re-enable)", new[] { typeof(bool) })

                // Tab/fullscreen-map detection drives automatic PiP visibility.
                .Field(typeof(Globals), "_mainGameViewModel", "Globals._mainGameViewModel (Tab/map visibility)")
                .Property(typeof(MainGameViewModel), "Map", "MainGameViewModel.Map (Tab/map visibility)")
                .Property(typeof(SceneMapViewModel), "IsFullscreen", "SceneMapViewModel.IsFullscreen (Tab detection)")
                .Property(typeof(NoesisView), "EnableMouse", "NoesisView.EnableMouse (map input suppression)")

                // Right-clicking a unit shown in the PiP opens the game's own context menu.
                .Property(typeof(MainGameViewModel), "OpenContextMenuCommand", "MainGameViewModel.OpenContextMenuCommand (PiP right-click menu)")
                .Method(typeof(ObjectBase), "isUnit", "ObjectBase.isUnit (PiP right-click raycast filter)")
                .Method(typeof(ObjectBase), "isWeapon", "ObjectBase.isWeapon (PiP right-click raycast filter)")
                .Field(typeof(ObjectBase), "_type", "ObjectBase._type (PiP right-click Chaff exclusion)")
                .Property(typeof(ObjectBase), "AcceptsOrdersFromPlayer", "ObjectBase.AcceptsOrdersFromPlayer (PiP right-click ownership filter)")
                .Field(typeof(SeapowerUI.MainGameView), "Instance", "MainGameView.Instance (PiP context menu placement)")
                .Field(typeof(SeapowerUI.MainGameView), "ContextMenuCanvas", "MainGameView.ContextMenuCanvas (PiP context menu placement)")
                .Validate(Logger, PluginName, PluginVersion);
        }

        private void PatchEach(params Type[] patchClasses)
        {
            foreach (Type t in patchClasses)
            {
                try
                {
                    _harmony.CreateClassProcessor(t).Patch();
                    Logger.LogInfo($"Applied Harmony patch: {t.Name}.");
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Harmony patch {t.Name} failed: {e.Message}");
                }
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RefreshSourceCamera();
        }

        private void Update()
        {
            if (_incompatible)
            {
                return;
            }

            if (Input.GetKeyDown(_toggleKey.Value))
            {
                SetPipVisible(!_pipVisible);
            }

            _timeSinceLastCameraRefresh += Time.unscaledDeltaTime;
            if (_sourceCamera == null || _timeSinceLastCameraRefresh >= _cameraRefreshInterval.Value)
            {
                _timeSinceLastCameraRefresh = 0f;
                RefreshSourceCamera();
            }

            if (_pipCanvas == null && _sourceCamera != null)
            {
                BuildPipUI();
            }

            // Input suppression is driven by the shared NoesisInputSuppressor (registered in
            // BuildPipUI), so there's nothing to poll here.
            UpdateMapDrivenVisibility();
        }

        /// <summary>
        /// Show the PiP while the game's fullscreen map (Tab) is covering the 3D view, and hide it
        /// when returning to the 3D view. Edge-triggered so the manual toggle key still works
        /// between map transitions.
        /// </summary>
        private void UpdateMapDrivenVisibility()
        {
            bool fullscreen;
            try
            {
                fullscreen = Globals._mainGameViewModel != null
                    && Globals._mainGameViewModel.Map != null
                    && Globals._mainGameViewModel.Map.IsFullscreen;
            }
            catch
            {
                fullscreen = false;
            }

            if (fullscreen != _wasMapFullscreen)
            {
                _wasMapFullscreen = fullscreen;
                SetPipVisible(fullscreen);
            }
        }

        private static float ScreenAspect()
        {
            return (float)Screen.width / Mathf.Max(1, Screen.height);
        }

        /// <summary>
        /// Noesis mouse-input suppression now lives in SeaPowerUX.Core's NoesisInputSuppressor,
        /// which arbitrates over ALL registered overlay windows (PiP, Flight Decks, …) instead of
        /// each plugin toggling the global NoesisView.EnableMouse flag independently — two
        /// independent trackers clobber each other, e.g. moving the pointer between two overlays
        /// leaves one restoring input while the other still needs it suppressed. The mechanism is
        /// unchanged (plain EnableMouse property set, never a Harmony patch of the map's Noesis
        /// behavior, which crashes the Mono runtime). Registration happens in BuildPipUI.
        /// </summary>
        private void RegisterPipInputSuppression()
        {
            NoesisInputSuppressor.Register(
                _pipPanelRect,
                // Suppress only while the PiP is actually on screen, and not while a context menu
                // we opened is up (the menu needs real Noesis mouse input to be clickable).
                () => _pipVisible && !_contextMenuOpenForPip && _pipCanvas != null && _pipCanvas.enabled);
        }

        /// <summary>
        /// Finds (or re-finds) the camera to mirror, moves the frame-capture component onto it,
        /// and resolves its gimbal rig (Camera Y Rotator -> Camera X Rotator -> Main Camera).
        /// </summary>
        private void RefreshSourceCamera()
        {
            Camera candidate = Camera.main;

            if (candidate == null)
            {
                // Camera.main relies on the "MainCamera" tag; fall back to any camera.
                Camera[] all = FindObjectsOfType<Camera>();
                if (all.Length > 0)
                {
                    candidate = all[0];
                }
            }

            if (candidate != null && candidate != _sourceCamera)
            {
                _sourceCamera = candidate;
                Logger.LogInfo($"PiP source camera set to '{_sourceCamera.name}'.");
                if (_pipCanvas != null)
                {
                    AttachCapture();
                }
            }

            ResolveRotators();
        }

        /// <summary>
        /// Walks up from the source camera to find the pitch (X) and yaw (Y) rotator
        /// transforms of the game's camera rig. Only set when both look like the rig,
        /// so GameCamera-mode drags never rotate an unrelated transform.
        /// </summary>
        private void ResolveRotators()
        {
            if (_sourceCamera == null)
            {
                _xRotator = null;
                _yRotator = null;
                return;
            }

            Transform x = _sourceCamera.transform.parent;
            Transform y = x != null ? x.parent : null;
            bool changed = x != _xRotator || y != _yRotator;
            _xRotator = x;
            _yRotator = y;

            if (changed)
            {
                if (x == null || y == null)
                {
                    Logger.LogWarning($"PiP: incomplete camera rig for source '{_sourceCamera.name}' " +
                        $"(parent='{(x != null ? x.name : "null")}', grandparent='{(y != null ? y.name : "null")}'). " +
                        "GameCamera-mode drag will do nothing.");
                }
                else
                {
                    Logger.LogInfo($"PiP camera rig resolved: yaw='{y.name}', pitch='{x.name}' " +
                        $"(source='{_sourceCamera.name}').");
                }
            }
        }

        private void BuildPipUI()
        {
            GameObject canvasGo = new GameObject("SeaPowerUX.TacticalPiP_Canvas");
            DontDestroyOnLoad(canvasGo);

            _pipCanvas = canvasGo.AddComponent<Canvas>();
            _pipCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _pipCanvas.sortingOrder = PipSortingOrder;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            canvasGo.AddComponent<GraphicRaycaster>();

            // Chrome comes from Core now: panel, title bar, drag, collapse/close buttons and the
            // edge/corner resize zones with proper resize cursors. PiP keeps only what is actually
            // PiP-specific — the mirrored view, camera drag and context menu.
            int contentW = _initialWidth.Value;
            int contentH = _initialHeight.Value > 0
                ? _initialHeight.Value
                : Mathf.Max(1, Mathf.RoundToInt(contentW / ScreenAspect()));

            _window = UguiWindowFactory.Create(_pipCanvas, new UguiWindowOptions
            {
                Title = "Tactical PiP",
                InitialX = _initialX.Value,
                InitialY = _initialY.Value,
                InitialWidth = contentW,
                InitialHeight = contentH,
                TitleBarHeight = TitleBarHeight,
                MinContentSize = new Vector2(MinPipWidth, MinPipHeight),
                // The mirrored frame must reach the window's frame — any inset would letterbox it.
                ContentPadding = 0,
                // Free-form resize. The mirrored view keeps the source aspect by being fitted
                // INSIDE the content area rather than by forcing the window's shape — otherwise an
                // ultra-wide source makes every window shape a wide letterbox, and narrowing the
                // window shrinks the view instead of just cropping the empty space around it.
                AspectLocked = false,
            });

            _pipPanelRect = _window.PanelRect;
            _contentRect = _window.ContentRect;
            PipInputState.PanelRect = _pipPanelRect;

            _window.Resized += OnPipResized;              // live crop during a resize drag
            _window.ResizeEnded += OnPipResizeEnded;       // rebuild the render target at the final size
            _window.CloseClicked += () => SetPipVisible(false);
            _window.CollapsedChanged += OnCollapsedChanged;

            // Sits left of the collapse button: snaps the window back to the source's aspect so
            // the whole frame is visible with nothing cropped.
            _window.AddTitleButton("▣", FitToAspect);
            _window.AddLockButton();

            BuildContent(_contentRect);

            RegisterPipInputSuppression();

            SetupCapture();

            // Core owns the geometry lifecycle — resolve the config (or the UIScale-aware default when
            // unset) to a pixel rect, apply it now at build and on Reset, persist moves/resizes, and
            // hold the lock. Install() applies immediately, which is what places the window at build
            // instead of leaving it at the raw sentinels. The DIP default overlays the tactical map.
            _geometry = new WindowGeometry(_window, _initialX, _initialY, _initialWidth, _initialHeight,
                WindowGeometry.UiScale(DefaultDipX, DefaultDipY, DefaultDipWidth, DefaultDipHeight),
                _windowLock, onApplied: RelayoutView);
            _geometry.Install();

            SetPipVisible(_pipVisible);
        }



        private void BuildContent(Transform contentRect)
        {
            // The view sits in its own centred frame rather than filling the content area, so the
            // window can be any shape while the image keeps the source aspect ratio.
            GameObject frameGo = new GameObject("ViewFrame");
            _viewFrame = frameGo.AddComponent<RectTransform>();
            _viewFrame.SetParent(contentRect, false);
            _viewFrame.anchorMin = Vector2.zero;
            _viewFrame.anchorMax = Vector2.one;
            _viewFrame.offsetMin = Vector2.zero;
            _viewFrame.offsetMax = Vector2.zero;

            // The frame IS the image — no separate border object. The window's own outer edge
            // carries the border (UguiWindowOptions.ShowBorder); framing the picture instead drew
            // a box around it floating inside a much larger window.
            _pipImage = frameGo.AddComponent<RawImage>();
            _pipImage.raycastTarget = true;

            // On the view, not the whole content area: dragging or right-clicking the empty
            // letterbox margin shouldn't act as though it happened in the mirrored scene.
            PipCameraDragHandler camDrag = frameGo.AddComponent<PipCameraDragHandler>();
            camDrag.OnDragBegin = OnCameraDragBegin;
            camDrag.OnDragDelta = OnCameraDrag;
            camDrag.OnDragEnd = OnCameraDragEnd;

            PipContextMenuHandler ctxMenu = frameGo.AddComponent<PipContextMenuHandler>();
            ctxMenu.OnRightClick = OnPipRightClick;

            LayoutView();
        }

        /// <summary>
        /// Resizes the window to the nearest size that shows the source frame uncropped.
        ///
        /// "Nearest" is meant literally: it tries holding the width and adjusting the height, and
        /// holding the height and adjusting the width, then takes whichever moves the window edge
        /// less. Always adjusting one fixed dimension would make the button feel arbitrary — snap
        /// a wide-but-short window and it would balloon in height when trimming the width was the
        /// smaller change.
        /// </summary>
        private void FitToAspect()
        {
            if (_pipPanelRect == null) return;

            // Snapping to aspect is nearly always the start of adjusting the window, not the end
            // of it — leaving it locked afterwards just means a second click to do anything. This
            // also keeps the button honest: it resizes, so it shouldn't work around a size lock
            // silently.
            if (_window != null) _window.Lock = WindowLock.None;

            float aspect = Mathf.Max(0.0001f, ScreenAspect());
            float width = _pipPanelRect.sizeDelta.x;
            float height = _pipPanelRect.sizeDelta.y - TitleBarHeight;

            float heightForWidth = width / aspect;   // keep width, fix height
            float widthForHeight = height * aspect;  // keep height, fix width

            if (Mathf.Abs(heightForWidth - height) <= Mathf.Abs(widthForHeight - width))
            {
                height = heightForWidth;
            }
            else
            {
                width = widthForHeight;
            }

            // Don't grow off the display: cap against the screen and re-derive the other axis so
            // the fit stays exact rather than being clipped back into a non-aspect shape.
            float maxWidth = Screen.width;
            float maxHeight = Mathf.Max(1f, Screen.height - TitleBarHeight);

            if (width > maxWidth)
            {
                width = maxWidth;
                height = width / aspect;
            }
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * aspect;
            }

            width = Mathf.Max(MinPipWidth, width);
            height = Mathf.Max(MinPipHeight, height);

            _pipPanelRect.sizeDelta = new Vector2(width, height + TitleBarHeight);

            // The window can be near an edge, so a bigger size still needs repositioning to stay
            // fully visible.
            _window.ClampIntoScreen();

            RelayoutView();
            _geometry.Persist();
        }

        /// <summary>
        /// CROPS the mirrored frame to the window's shape instead of scaling it to fit.
        ///
        /// The view fills the whole content area and the source is cropped in UV space, centred,
        /// so narrowing the window shows a narrower slice of the scene at unchanged height —
        /// exactly what's wanted when the source is an ultra-wide display. Scaling to fit did the
        /// opposite: a narrow window shrank the whole picture and left large empty margins.
        ///
        /// Nothing is ever stretched: the crop only ever removes source, so the aspect ratio of
        /// what's on screen always matches the aspect ratio of what's shown.
        /// </summary>
        private void LayoutView()
        {
            if (_pipImage == null || _contentRect == null) return;

            float displayWidth = Mathf.Max(1f, _contentRect.rect.width);
            float displayHeight = Mathf.Max(1f, _contentRect.rect.height);

            float displayAspect = displayWidth / displayHeight;
            float sourceAspect = Mathf.Max(0.0001f, ScreenAspect());

            // Fraction of the source kept on each axis. One is always 1 — we only ever crop the
            // axis that has surplus.
            float u = 1f;
            float v = 1f;

            if (displayAspect < sourceAspect) u = displayAspect / sourceAspect;  // narrower: trim the sides
            else v = sourceAspect / displayAspect;                               // taller: trim top and bottom

            // Centred crop.
            _pipImage.uvRect = new UnityEngine.Rect((1f - u) * 0.5f, (1f - v) * 0.5f, u, v);
            _cropU = u;
        }

        // ----- Frame capture (mirror the main camera) -----

        private void SetupCapture()
        {
            AllocateRenderTexture(ContentWidth(), ContentHeight());
            AttachCapture();
        }

        /// <summary>
        /// Puts the PiPFrameCapture component on the current source camera (moving it if the
        /// source changed). Its OnRenderImage blits the camera's finished frame into our texture.
        /// </summary>
        private void AttachCapture()
        {
            if (_sourceCamera == null)
            {
                return;
            }

            if (_capture != null && _capture.gameObject != _sourceCamera.gameObject)
            {
                Destroy(_capture);
                _capture = null;
            }

            if (_capture == null)
            {
                _capture = _sourceCamera.gameObject.AddComponent<PipFrameCapture>();
            }

            _capture.Target = _renderTexture;
            _capture.enabled = _pipVisible && !_collapsed;
        }

        /// <summary>
        /// Size of the VISIBLE view, not the window. The render texture is allocated from this, so
        /// letterbox margin costs no GPU memory.
        /// </summary>
        /// <summary>
        /// Render-texture width: the full source frame scaled so the visible crop lands at about
        /// one texel per pixel. Clamped, since a very narrow window would otherwise demand an
        /// enormous texture to keep that density.
        /// </summary>
        private int ContentWidth()
        {
            if (_contentRect == null) return 1;

            float visibleWidth = Mathf.Max(1f, _contentRect.rect.width);
            float fullWidth = visibleWidth / Mathf.Max(0.05f, _cropU);
            return Mathf.Clamp(Mathf.RoundToInt(fullWidth), 1, MaxRenderWidth);
        }

        /// <summary>Height follows the SOURCE aspect — the texture always holds a whole frame.</summary>
        private int ContentHeight()
        {
            return Mathf.Max(1, Mathf.RoundToInt(ContentWidth() / Mathf.Max(0.0001f, ScreenAspect())));
        }

        private void AllocateRenderTexture(int width, int height)
        {
            ReleaseRenderTexture();

            int rtWidth = Mathf.Max(1, Mathf.RoundToInt(width * _renderScale.Value));
            int rtHeight = Mathf.Max(1, Mathf.RoundToInt(height * _renderScale.Value));

            // Blit target only (no depth buffer needed).
            _renderTexture = new RenderTexture(rtWidth, rtHeight, 0, RenderTextureFormat.Default)
            {
                name = "SeaPowerUX.TacticalPiP_RT"
            };
            _renderTexture.Create();

            if (_capture != null)
            {
                _capture.Target = _renderTexture;
            }

            if (_pipImage != null)
            {
                _pipImage.texture = _renderTexture;
            }
        }

        private void ReleaseRenderTexture()
        {
            if (_renderTexture == null)
            {
                return;
            }

            if (_capture != null)
            {
                _capture.Target = null;
            }

            _renderTexture.Release();
            UnityEngine.Object.Destroy(_renderTexture);
            _renderTexture = null;
        }

        private void OnPipResized(Vector2 newPanelSize)
        {
            LayoutView();
        }

        /// <summary>
        /// Core owns the collapse geometry; PiP only has to stop the capture work while collapsed
        /// (and resume it, at the restored size, on expand).
        /// </summary>
        private void OnCollapsedChanged(bool collapsed)
        {
            _collapsed = collapsed;

            if (_capture != null) _capture.enabled = _pipVisible && !collapsed;
            if (!collapsed) AllocateRenderTexture(ContentWidth(), ContentHeight());
        }

        /// <summary>
        /// Single place that decides whether the PiP canvas draws. PiP predates the pack's shared
        /// canvas and still owns its own (it needs a distinct sorting order relative to Noesis),
        /// which is why hiding the HUD didn't hide it — HudVisibility only reaches the shared
        /// canvas. Rather than move PiP onto that canvas, every condition is ANDed here so no
        /// call site can set `enabled` while forgetting one of the others.
        /// </summary>
        private void ApplyCanvasEnabled()
        {
            if (_pipCanvas == null) return;

            // Note: NOT gated on _collapsed — a collapsed PiP still shows its title bar, which is
            // the only way to expand it again. (The old context-menu-close path did AND in
            // !_collapsed, which would have blanked a collapsed PiP completely.)
            _pipCanvas.enabled = _pipVisible
                && !_contextMenuOpenForPip
                && HudVisibility.IsHudVisible;
        }

        private void SetPipVisible(bool visible)
        {
            _pipVisible = visible;
            PipInputState.CanvasVisible = visible;
            ApplyCanvasEnabled();

            if (_capture != null)
            {
                // Capture (and its per-frame blit) is off while hidden or collapsed.
                _capture.enabled = visible && !_collapsed;
            }
        }

        // ----- Camera drag -----

        private void OnCameraDragBegin()
        {
            if (_cameraDragMode.Value == CameraDragMode.GameCamera && _yRotator != null && _xRotator != null)
            {
                _driveYaw = _yRotator.eulerAngles.y;
                _drivePitch = NormalizeAngle(_xRotator.localEulerAngles.x);
            }
        }

        private void OnCameraDrag(Vector2 delta)
        {
            if (_cameraDragMode.Value != CameraDragMode.GameCamera)
            {
                return;
            }

            float sens = _dragSensitivity.Value;
            float dy = _invertY.Value ? -delta.y : delta.y;
            DriveGameCamera(delta.x * sens, dy * sens);
        }

        /// <summary>
        /// Rotates the game's real camera rig and syncs the game's stored rotation
        /// (RenderPosition) so its own camera loop re-applies the same value instead of
        /// snapping back.
        /// </summary>
        private void DriveGameCamera(float yawDelta, float pitchDelta)
        {
            if (_yRotator == null || _xRotator == null)
            {
                return;
            }

            _driveYaw += yawDelta;
            _drivePitch = Mathf.Clamp(_drivePitch - pitchDelta, -MaxPitchAngle, MaxPitchAngle);

            // Hand our target to the driver: it applies immediately and the handleRotation
            // postfix re-applies it after the game runs, so our value wins the frame.
            PipCameraDriver.XRotator = _xRotator;
            PipCameraDriver.YRotator = _yRotator;
            PipCameraDriver.Yaw = _driveYaw;
            PipCameraDriver.Pitch = _drivePitch;
            PipCameraDriver.Active = true;
            PipCameraDriver.Apply();
        }

        private void OnCameraDragEnd()
        {
            // Stop forcing our rotation; the game resumes from the current rig state
            // (which we kept synced into RenderPosition, so it won't snap back).
            PipCameraDriver.Active = false;
        }

        // ----- Right-click context menu -----

        private readonly RaycastHit[] _contextMenuHits = new RaycastHit[10];

        /// <summary>
        /// Maps a right-click's screen position (over the PiP's RawImage) into the corresponding
        /// point in the real viewport. Since the PiP is a pixel-for-pixel mirror of the source
        /// camera's finished frame at the same aspect ratio, this is a plain UV rescale — no
        /// second camera or raycast layer involved.
        /// </summary>
        private void OnPipRightClick(PointerEventData eventData)
        {
            if (_sourceCamera == null || _contentRect == null)
            {
                return;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _contentRect, eventData.position, null, out Vector2 local))
            {
                return;
            }

            Rect rect = _contentRect.rect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            float u = (local.x - rect.xMin) / rect.width;
            float v = (local.y - rect.yMin) / rect.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                return;
            }

            // Two different points: the raycast needs the click translated into the *main
            // viewport's* screen space (since the PiP mirrors that camera's full frame at the
            // same aspect), but the menu should visually appear at the real cursor position,
            // which is wherever the PiP window itself actually is on screen.
            Vector3 raycastScreenPoint = new Vector3(u * Screen.width, v * Screen.height, 0f);
            TryOpenContextMenuAt(raycastScreenPoint, eventData.position);
        }

        /// <summary>
        /// Mirrors SeaPower.InputHandler.handleMouseInputs()'s right-click raycast/filter, but
        /// against a screen point translated from the PiP instead of the real Input.mousePosition,
        /// and calls the game's own MainGameViewModel.OpenContextMenuCommand directly — a plain
        /// public ICommand on a plain MonoBehaviour, so no Harmony patch is needed for this
        /// feature at all. Unlike the vanilla path (which only opens the menu for the
        /// already-selected unit), this opens it for whatever unit/weapon is under the click.
        /// </summary>
        private void TryOpenContextMenuAt(Vector3 raycastScreenPoint, Vector2 menuScreenPoint)
        {
            int count = Physics.RaycastNonAlloc(_sourceCamera.ScreenPointToRay(raycastScreenPoint), _contextMenuHits, 1000f);
            for (int i = 0; i < count; i++)
            {
                Transform hitTransform = _contextMenuHits[i].transform;
                ObjectBase ob = hitTransform.GetComponent<ObjectBase>() ?? hitTransform.root.GetComponent<ObjectBase>();
                if (ob == null)
                {
                    continue;
                }
                if (!ob.isUnit() && !ob.isWeapon())
                {
                    continue;
                }
                if (ob._type == ObjectBase.ObjectType.Chaff)
                {
                    continue;
                }
                if (ob.isUnit() && !ob.AcceptsOrdersFromPlayer)
                {
                    continue;
                }

                try
                {
                    ShowPipContextMenuAt(menuScreenPoint);
                    Globals._mainGameViewModel?.OpenContextMenuCommand?.Execute(ob);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"PiP context menu failed: {e.Message}");
                }
                return;
            }
        }

        /// <summary>
        /// Positions the game's context menu at a known screen point and makes it actually
        /// usable from the PiP: hides the PiP canvas (Noesis draws the menu via a camera-attached
        /// pass that composites BEFORE Unity's ScreenSpaceOverlay canvases, so no sortingOrder on
        /// our canvas can put it above the menu — hiding is the only way to make it visible).
        /// Setting _contextMenuOpenForPip also makes this window's NoesisInputSuppressor
        /// predicate report inactive, so mouse input is handed straight back to Noesis and the
        /// menu is clickable. Restored by OnPipContextMenuClosed when the menu's Closed event fires.
        /// </summary>
        private void ShowPipContextMenuAt(Vector2 unityScreenPoint)
        {
            Noesis.ContextMenu menu = SeapowerUI.MainGameView.Instance?.ContextMenuCanvas?.ContextMenu;
            if (menu == null)
            {
                return;
            }

            if (!_contextMenuClosedHandlerAttached)
            {
                menu.Closed += OnPipContextMenuClosed;
                _contextMenuClosedHandlerAttached = true;
            }

            _contextMenuOpenForPip = true;
            ApplyCanvasEnabled();

            // _contextMenuOpenForPip (set above) makes our suppressor predicate return false, so
            // the shared NoesisInputSuppressor hands mouse input back to Noesis on its next tick
            // and the menu becomes clickable.

            // Noesis screen coordinates are top-left-origin DIPs (confirmed via Visual.PointToScreen
            // usage elsewhere), while Unity's screen point is bottom-left-origin, hence the Y flip.
            // AbsolutePoint is used instead of the Mouse/MousePoint placement modes because our
            // right-click never routes through Noesis's own input pipeline (Noesis mouse input is
            // suppressed over the PiP until just now), so Noesis has no real click event to derive
            // a mouse-relative position from — those modes defaulted to screen-center here.
            if (!_menuPlacementOverridden)
            {
                _savedMenuPlacement = menu.Placement;
                _savedMenuHorizontalOffset = menu.HorizontalOffset;
                _savedMenuVerticalOffset = menu.VerticalOffset;
                _menuPlacementOverridden = true;
            }

            menu.Placement = Noesis.PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = unityScreenPoint.x;
            menu.VerticalOffset = Screen.height - unityScreenPoint.y;
        }

        /// <summary>
        /// Puts the shared ContextMenu's placement back exactly as we found it, so ordinary
        /// right-clicks in the 3D view keep opening at the cursor.
        /// </summary>
        private void RestoreContextMenuPlacement()
        {
            if (!_menuPlacementOverridden) return;
            _menuPlacementOverridden = false;

            try
            {
                Noesis.ContextMenu menu = SeapowerUI.MainGameView.Instance?.ContextMenuCanvas?.ContextMenu;
                if (menu == null) return;
                menu.Placement = _savedMenuPlacement;
                menu.HorizontalOffset = _savedMenuHorizontalOffset;
                menu.VerticalOffset = _savedMenuVerticalOffset;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to restore context menu placement: {e.Message}");
            }
        }

        /// <summary>
        /// Fires for ANY context menu close, including ones opened normally outside the PiP
        /// (this is the game's single shared ContextMenu instance) — harmless no-op unless we're
        /// the one who hid the PiP for it. Restores PiP visibility from current state rather than
        /// forcing it back on, in case the player toggled/collapsed the PiP while the menu was up.
        /// </summary>
        private void OnPipContextMenuClosed(object sender, Noesis.RoutedEventArgs e)
        {
            // Undo the placement override even if this close was for a menu we didn't open —
            // leaving AbsolutePoint set is what made ordinary right-clicks stop following the cursor.
            RestoreContextMenuPlacement();

            if (!_contextMenuOpenForPip)
            {
                return;
            }

            _contextMenuOpenForPip = false;
            ApplyCanvasEnabled();
        }

        private static float NormalizeAngle(float euler)
        {
            euler %= 360f;
            if (euler > 180f)
            {
                euler -= 360f;
            }
            return euler;
        }

        private static Font GetUiFont()
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

    /// <summary>Reports right-clicks over the PiP body so the plugin can open the game's context menu.</summary>
    public class PipContextMenuHandler : MonoBehaviour, IPointerClickHandler
    {
        public Action<PointerEventData> OnRightClick;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                OnRightClick?.Invoke(eventData);
            }
        }
    }

    /// <summary>Reports pointer drags over the PiP body so the plugin can orbit the camera.</summary>
    public class PipCameraDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public Action OnDragBegin;
        public Action<Vector2> OnDragDelta;
        public Action OnDragEnd;

        public void OnBeginDrag(PointerEventData eventData)
        {
            OnDragBegin?.Invoke();
        }

        public void OnDrag(PointerEventData eventData)
        {
            OnDragDelta?.Invoke(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            OnDragEnd?.Invoke();
        }
    }

    /// <summary>
    /// Captures the camera's finished frame into a RenderTexture via OnRenderImage (Built-in RP).
    /// Lives on the source (main) camera. Always passes the frame through to the screen — omitting
    /// that would black out the main view — and additionally blits into the PiP texture.
    /// Disable the component to turn capture off with zero per-frame cost.
    /// </summary>
    public class PipFrameCapture : MonoBehaviour
    {
        public RenderTexture Target;

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            Graphics.Blit(src, dest); // keep the main screen intact
            if (Target != null)
            {
                Graphics.Blit(src, Target); // mirror into the PiP (scales to the texture size)
            }
        }
    }

    /// <summary>
    /// Holds the PiP's desired camera-rig rotation and applies it. Used both directly
    /// (on drag) and from the CameraBase.handleRotation postfix so the game can't revert
    /// our rotation mid-drag. Static because the Harmony postfix has no plugin instance.
    /// </summary>
    internal static class PipCameraDriver
    {
        public static bool Active;
        public static float Yaw;
        public static float Pitch;
        public static Transform XRotator;
        public static Transform YRotator;

        public static void Apply()
        {
            if (YRotator != null)
            {
                YRotator.rotation = Quaternion.Euler(0f, Yaw, 0f);
            }
            if (XRotator != null)
            {
                XRotator.localRotation = Quaternion.Euler(Pitch, 0f, 0f);
            }
            if (Singleton<RenderPosition>.InstanceExists())
            {
                Singleton<RenderPosition>.Instance.setRotationXY(Pitch, Yaw);
            }
        }
    }

    /// <summary>
    /// Prefix skips the game's rotation while dragging over the PiP (so window/title-bar drags
    /// don't rotate the camera); postfix re-applies our GameCamera-driver rotation so body drags
    /// still work. NOTE: patching MouseControlState.getState() to report UI instead does NOT work —
    /// it's a trivial getter the Mono runtime inlines, so callers never route through the patch.
    /// </summary>
    [HarmonyPatch(typeof(CameraBase), "handleRotation")]
    internal static class CameraBaseHandleRotationPatch
    {
        private static bool Prefix()
        {
            return !(PipInputState.PointerOverPip() && Input.GetMouseButton(0));
        }

        private static void Postfix()
        {
            if (PipCameraDriver.Active)
            {
                PipCameraDriver.Apply();
            }
        }
    }

    /// <summary>
    /// Keeps the game's camera mouse interaction enabled while the cursor is over the PiP.
    /// The game disables it (setMouseInteractionFlag(false)) whenever it thinks the mouse is over
    /// UI — which happens when the PiP sits over the map — and that also kills scroll-zoom. This
    /// postfix runs after OnUpdate (a substantial method, so it's patchable, unlike the inlined
    /// getState/setter) and re-enables the flag over the PiP so scroll-zoom stays consistent.
    /// </summary>
    [HarmonyPatch(typeof(MouseControlState), "OnUpdate")]
    internal static class MouseControlStateOnUpdatePatch
    {
        private static void Postfix()
        {
            if (PipInputState.PointerOverPip() && Singleton<CameraManager>.InstanceExists())
            {
                Singleton<CameraManager>.Instance.setMouseInteractionFlag(true);
            }
        }
    }

    /// <summary>
    /// True when the mouse pointer is inside the PiP window's screen rect. Uses a direct
    /// rect test rather than EventSystem.IsPointerOverGameObject(), which is unreliable
    /// with the game's new Input System UI module.
    /// </summary>
    internal static class PipInputState
    {
        public static RectTransform PanelRect;
        public static bool CanvasVisible;

        public static bool PointerOverPip()
        {
            if (!CanvasVisible || PanelRect == null)
            {
                return false;
            }

            // ScreenSpaceOverlay canvas -> pass null camera.
            return RectTransformUtility.RectangleContainsScreenPoint(
                PanelRect, Input.mousePosition, null);
        }
    }
}

/*
NOTES
=====

1) Tab-to-fullscreen-map does NOT deactivate the 3D camera (confirmed in-game) — it just covers
   the 3D view with the map's UI canvas, so mirroring the main camera keeps working during Tab.

2) Rendering: the PiP now MIRRORS the main camera by capturing its finished frame via
   OnRenderImage (PipFrameCapture) and blitting into a RenderTexture — Built-in RP, confirmed no
   URP/HDRP. This replaced the earlier second "shadow camera" that re-rendered the whole scene:
   that doubled render cost and made the Ceto ocean asset build an incomplete per-camera setup
   (missing its Mask camera), producing the projected-grid water artifacts. Mirroring is cheaper
   and artifact-free, but the PiP can only show the main camera's view. Independent PiP angles
   ("PipOnly" mode) and PiP-only FOV zoom were removed with the shadow camera; revisit them (and a
   possible multi-viewport setup) later — they'd need their own render again, with the Ceto
   multi-camera problem to solve.
*/

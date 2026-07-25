using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeaPower;
using SeaPowerUX.Core.Foundation;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SeaPowerUX.FlightDeck
{
    /// <summary>
    /// Unified Flight Deck window: one scrollable list of every ship/air base in the scene that
    /// has a flight deck, with at-a-glance capacity, so the player doesn't have to hunt for each
    /// carrier individually. Clicking a row selects that unit and opens the game's OWN native
    /// Flight Deck window for it.
    ///
    /// Why the detail pane is the game's window rather than ours: the game already defines a
    /// `DataTemplate DataType="{x:Type viewModels:FlightDeckViewModel}"` that builds a complete
    /// native `SeapowerUI.Window` (type/squadron/loadout dropdowns, capacity bars, the Flight
    /// Operations table, Ready/Launch/Recover All). Reproducing that in UGUI was both worse
    /// looking and worse functioning, so this now drives the real thing.
    ///
    /// How opening it works (all plain property/method calls — no Harmony patching):
    /// we construct our own `FlightDeckViewModel(ship)` and hand it to
    /// `WindowHostCommands.OpenWindowCommand.Execute(vm, MainGameView.Instance)`. `WindowHost`
    /// appends it to its public `Windows` ObservableCollection, which a MainGameView
    /// `ItemsControl` renders through the unkeyed
    /// `DataTemplate DataType="{x:Type viewModels:FlightDeckViewModel}"` — the full native
    /// window. This is the same call the game itself uses for `MessageBoxViewModel`.
    ///
    /// Rejected alternative — setting `ObjectFlightDeck.FlightDeckVisible = true` on the
    /// selected unit's view model (what the game's own context-menu item does). That only works
    /// for the *currently selected* unit, because `FlightDeckVisible` drives a
    /// `Popup IsOpen="{Binding FlightDeckVisible}"` that only exists in the visual tree while
    /// that unit's info panel is rendered. Driving it therefore required force-selecting the
    /// ship, which also yanks the camera (MainGameViewModel subscribes to selection and calls
    /// `RenderPosition.switchToObject`) — an obnoxious side effect for a "check all my decks"
    /// window. OpenWindowCommand needs no selection change at all.
    ///
    /// Also note `ObjectBase.ViewModel` is `=> CreateViewModel()` — it builds a NEW view model
    /// on every access and never caches, so it must not be used as a stable handle.
    ///
    /// ONE CHILD WINDOW AT A TIME: clicking a different ship switches the open window over to it;
    /// clicking the ship that's already open closes it.
    ///
    /// DOCKED, NOT FLOATING: the native window is pinned to this window's right edge every frame
    /// (Core's WindowDock), so the list and the deck panel move together as one control.
    /// Re-asserting the position each frame also makes the native title bar non-draggable, which is
    /// what we want now that it isn't independently positioned. An earlier attempt to merely
    /// *anchor* a free-floating child was removed — Noesis placed it before we could, so every open
    /// showed a jump from the default spot to the anchor.
    ///
    /// The composite is a dock stack (Core's DockStack): this window is the parent container, and
    /// the native panel sits in a HOLLOW child slot beside it — hollow because this pack's overlay
    /// canvas draws above Noesis, so anything painted there would hide the panel. A thin frame
    /// around the union of the two makes them read as one control, and the slot collapses to
    /// nothing when no deck is open. The stack picks its expansion direction from the screen room
    /// actually available, so a deck opened near an edge expands inward instead of off-screen.
    ///
    /// LIFETIME (important — `FlightDeckViewModel` leaks if dropped): its constructor subscribes
    /// to `FlightDeck._vehiclesOnBoard.CollectionChanged`, and `Dispose()` is what unsubscribes.
    /// `WindowHost.CloseWindow` only auto-disposes view models implementing `IDisposeOnClose` —
    /// `FlightDeckViewModel` implements plain `IDisposable`, so the game will NOT dispose ours.
    /// We therefore own the view model we create: disposed when we close/switch, when its window
    /// leaves `WindowHost.Windows` (watched via CollectionChanged), on scene change, and on
    /// plugin unload.
    /// </summary>
    /// <remarks>
    /// Soft dependency on WindowMemory: Air Boss works without it, but the pack is designed to be
    /// used together and the load-order hint keeps the two consistent when both are installed.
    /// Soft rather than hard precisely so WindowMemory can be removed in an emergency — see that
    /// plugin's remarks — without disabling this one.
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.moseysauce.seapowerux.windowmemory", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.moseysauce.seapowerux.flightdeck";
        public const string PluginName = "SeaPowerUX.FlightDeck";
        /// <summary>Core build this plugin was compiled against; see CoreCompatibility.</summary>
        private const string RequiredCoreVersion = "0.5.0";

        public const string PluginVersion = "0.5.0";

        // Palette lifted from the game's own theme resources (globalgamemanagers.assets), same
        // source as UguiContextMenu's, so the list reads as part of the game's UI.
        private static readonly Color RowHoverColor = HexColor("#393d40");
        private static readonly Color RowSelectedColor = HexColor("#2f5d8a");
        private static readonly Color TextColor = HexColor("#d7d7d7");
        private static readonly Color DimTextColor = HexColor("#949aa1");
        private static readonly Color SeparatorColor = HexColor("#3f4147");

        private const float HeaderHeight = 20f;
        private const float RowHeight = 24f;
        private const float ScrollbarWidth = 12f;
        private const float TitleBarHeightPx = 24f;

        // Coded default rect in DIP (logical) units, captured in-game (upper-right). Scaled by the
        // game's UI scale at apply time so it tracks the native UI across resolutions.
        private const int DefaultDipX = 1449;
        private const int DefaultDipY = 83;
        private const int DefaultDipWidth = 470;
        private const int DefaultDipHeight = 260;

        /// <summary>
        /// The one definition of the grid's columns. Header and rows are both laid out from this,
        /// so a column's position can't differ between them.
        /// </summary>
        private static readonly GridColumn[] Columns =
        {
            new GridColumn { Header = "Unit",     Flexible = true, MinWidth = 120f, Align = TextAnchor.MiddleLeft },
            new GridColumn { Header = "Status",   Width = 68f,  Align = TextAnchor.MiddleLeft },
            new GridColumn { Header = "Ops",      Width = 78f,  Align = TextAnchor.MiddleLeft },
            new GridColumn { Header = "Aircraft", Width = 54f,  Align = TextAnchor.MiddleRight },
            new GridColumn { Header = "Deck",     Width = 46f,  Align = TextAnchor.MiddleRight },
        };
        private const int FontSize = 12;

        // Config
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<int> _initialX;
        private ConfigEntry<int> _initialY;
        private ConfigEntry<int> _initialWidth;
        private ConfigEntry<int> _initialHeight;
        private ConfigEntry<WindowLock> _windowLock;
        private ConfigEntry<bool> _debugLogging;

        internal static ManualLogSource Log;
        private bool _incompatible;
        private Harmony _harmony;

        private UguiWindow _window;
        private WindowGeometry _geometry;
        private bool _visible;
        private RectTransform _headerRow;
        private RectTransform _listContent;
        private Text _statusText;

        // Per-row selection overlays, keyed by the unit the row represents. Rebuilt whenever the
        // list is, and cleared with it — these hold destroyed GameObjects otherwise.
        private readonly Dictionary<ObjectBase, Image> _rowSelection = new Dictionary<ObjectBase, Image>();

        // Live status binding. Each listed ship's FlightDeckTasks is subscribed once; because it's
        // a TrulyObservableCollection it re-raises CollectionChanged when any task's properties
        // change, so one handler per ship covers adds, removes AND state transitions with no
        // per-frame work. Handlers are kept so they can be detached from exactly the same
        // collection instance later — see UnbindStatus().
        private readonly Dictionary<ObjectBase, RowStatusBinding> _statusBindings =
            new Dictionary<ObjectBase, RowStatusBinding>();

        private sealed class RowStatusBinding
        {
            public SeaPower.FlightDeck Deck;
            public NotifyCollectionChangedEventHandler Handler;
            public Text StatusText;
            public Text GlyphText;
            public Text AircraftText;
            public Text DeckText;
        }

        // Exactly one child flight deck window at a time: clicking another ship switches to it,
        // clicking the open ship again closes it. We own this view model's disposal — see the
        // LIFETIME note on the class doc.
        private ObjectBase _openShip;
        private FlightDeckViewModel _openVm;

        // The WindowHost.Windows collection we're currently subscribed to, kept so we can
        // unsubscribe from exactly the same instance (it changes when the scene reloads).
        private bool _watchingHostCommands;

        // The ship list is bound, not polled: ObjectsManager._listOfAllPlayerObjects is an
        // ObservableCollection, so units appearing or being destroyed reach us as events. A dirty
        // flag rather than an immediate rebuild because a scene load adds every unit one at a
        // time, and rebuilding per add would be quadratic for no benefit.
        private ObservableCollection<ObjectBase> _watchedPlayerObjects;
        private bool _shipListDirty;

        // Docking: the native flight deck window is positioned over a reserved slot inside this
        // window every frame, so the two read as a single control. See Core's WindowDock.
        private WindowDock _dock;
        private DockStack _stack;
        private NativeWindowDockedElement _dockedDeck;

        private void Awake()
        {
            Log = Logger;

            // Before anything else touches Core: a mismatched or missing Core otherwise fails
            // later as an opaque type-load error with nothing naming the cause.
            if (!CoreVersionOk()) return;

            _enabled = Config.Bind("General", "Enabled", true,
                "Master kill switch for this panel plugin.");
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.Home,
                "Key that shows/hides the Flight Deck list window.");
            _initialX = Config.Bind("Window", "InitialX", WindowDefaults.Auto,
                "Window X position in pixels from the left edge. Updated when you move the window. -1 = position relative to the screen on first show.");
            _initialY = Config.Bind("Window", "InitialY", WindowDefaults.Auto,
                "Window Y position in pixels from the top edge. Updated when you move the window. -1 = position relative to the screen on first show.");
            _initialWidth = Config.Bind("Window", "InitialWidth", 0,
                "Window width in pixels. Updated when you resize the window. 0 = default.");
            _initialHeight = Config.Bind("Window", "InitialHeight", 0,
                "Window content height in pixels. Updated when you resize the window. 0 = default.");
            _windowLock = Config.Bind("Window", "Lock", WindowLock.None,
                "Holds this window's position and/or size. Cycled by the lock button in the title bar.");
            _debugLogging = Config.Bind("General", "Debug", false,
                "Verbose logging.");

            if (!_enabled.Value)
            {
                Logger.LogInfo($"{PluginName}: disabled via config.");
                _incompatible = true;
                return;
            }

            if (!ValidateGameApi())
            {
                _incompatible = true;
                return;
            }

            // Takes part in the toolbar's Set Default / Reset pair.
            UxSettings.RegisterConfig(Config);
            UxSettings.ApplyShippedDefaultsOnce();

            HudVisibility.Enable();

            // Docking hides a child window before its first render, which needs the same
            // pre-render seam window memory uses. Installed here too, and idempotent, so docking
            // keeps working when WindowMemory isn't installed.
            _harmony = new Harmony(PluginGuid);
            WindowLifecycle.Install(_harmony, Logger);

            SceneManager.sceneLoaded += OnSceneLoaded;
            UguiToolbar.AddToggle("Air Boss", () => _visible, () => SetVisible(!_visible));

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. Toggle with {_toggleKey.Value}.");
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
            if (_window?.PanelRect != null)
            {
                NoesisInputSuppressor.Unregister(_window.PanelRect);
            }
            _geometry?.Dispose();
            UnbindAllStatus();
            UnwatchWindowCollection();
            UnwatchPlayerObjects();
            CloseAndDisposeAll();
            _harmony?.UnpatchSelf();
        }

        private bool ValidateGameApi()
        {
            if (!CoreApi.Validate(Logger, PluginName, PluginVersion)) return false;

            var validator = new GameApiValidator()
                // Live operations state and the bound ship list — both added after the first
                // version of this validator was written.
                .Property(typeof(SeaPower.FlightDeck), "FlightDeckTasks", "FlightDeck.FlightDeckTasks (live flight operations)")
                .Field(typeof(SeaPower.FlightDeck), "_holdingQueue", "FlightDeck._holdingQueue (inbound aircraft count)")
                .Property(typeof(SeaPower.PendingLaunchTask), "LaunchCount", "PendingLaunchTask.LaunchCount (aircraft per pending launch)")
                .Field(typeof(SeaPower.FlightDeckTask), "_stateMachine", "FlightDeckTask._stateMachine (ready vs preparing)")
                .Property(typeof(SeaPower.StateMachine), "CurrentState", "StateMachine.CurrentState (ready vs preparing)")
                .Property(typeof(ObjectsManager), "_listOfAllPlayerObjects", "ObjectsManager._listOfAllPlayerObjects (bound ship list)")
                // Ship enumeration / display.
                .Field(typeof(ObjectBase), "_obp", "ObjectBase._obp (flight deck lookup)")
                .Field(typeof(ObjectBaseParameters), "_flightDeck", "ObjectBaseParameters._flightDeck (flight deck lookup)")
                .Property(typeof(ObjectBase), "AcceptsOrdersFromPlayer", "ObjectBase.AcceptsOrdersFromPlayer (ship list filter)")
                .Method(typeof(ObjectBase), "getUIDAndName", "ObjectBase.getUIDAndName (ship list display name)")
                .Property(typeof(SeaPower.FlightDeck), "AircraftCurrentlyOnBoard", "FlightDeck.AircraftCurrentlyOnBoard (list capacity column)")
                .Property(typeof(SeaPower.FlightDeck), "MaxAircraftOnBoard", "FlightDeck.MaxAircraftOnBoard (list capacity column)")
                .Property(typeof(SeaPower.FlightDeck), "AssignedDeckParkSlots", "FlightDeck.AssignedDeckParkSlots (list deck column)")
                .Property(typeof(SeaPower.FlightDeck), "DeckParkSlots", "FlightDeck.DeckParkSlots (list deck column)")
                // Opening the native flight deck window via WindowHost.
                .Type(typeof(FlightDeckViewModel), "SeapowerUI.ViewModels.FlightDeckViewModel (native window content)")
                .Method(typeof(FlightDeckViewModel), "Dispose", "FlightDeckViewModel.Dispose (view model cleanup — leaks without it)")
                .Property(typeof(WindowHostCommands), "OpenWindowCommand", "WindowHostCommands.OpenWindowCommand (open native window)")
                .Field(typeof(MainGameView), "Instance", "MainGameView.Instance (command target)")
                .Property(typeof(WindowHostManager), "CurrentHost", "WindowHostManager.CurrentHost (window tracking)")
                .Property(typeof(WindowHost), "Windows", "WindowHost.Windows (window tracking / close detection)")
                // Right-click context menu on a row (still selects the unit deliberately, since
                // several of the game's own context-menu commands act on the selected unit).
                .Method(typeof(ObjectsManager), "setActiveObject", "ObjectsManager.setActiveObject (context menu selection)", new[] { typeof(ObjectBase) })
                .Property(typeof(RenderPosition), "SelectedObject", "RenderPosition.SelectedObject (context menu selection)")
                .Property(typeof(ObjectBase), "ViewModel", "ObjectBase.ViewModel (context menu item source)")
                .Method(typeof(SeapowerUI.IContextMenuObject), "GetContextMenuItems", "IContextMenuObject.GetContextMenuItems (context menu items)")
                // Blocking the game's own 3D picking while the pointer is over our windows.
                .Method(typeof(MouseControlState), "setMouseIsOverUIWindow", "MouseControlState.setMouseIsOverUIWindow (input blocking)", new[] { typeof(bool) })
                // Child window placement.
                .Property(typeof(SeapowerUI.Window), "X", "SeapowerUI.Window.X (child window placement)")
                .Property(typeof(SeapowerUI.Window), "Y", "SeapowerUI.Window.Y (child window placement)");

            return validator.Validate(Logger, PluginName, PluginVersion);
        }

        private void Update()
        {
            if (_incompatible) return;

            if (Input.GetKeyDown(_toggleKey.Value))
            {
                SetVisible(!_visible);
            }

            UpdateDockStack();
            WatchPlayerObjects();

            // Coalesced to at most one rebuild per frame, and only while the window is showing.
            if (_shipListDirty && _visible)
            {
                _shipListDirty = false;
                RefreshShipList();
            }
        }


        private void SetVisible(bool visible)
        {
            _visible = visible;

            if (_window == null)
            {
                if (!visible) return;
                BuildWindow();
            }

            _window.SetVisible(visible);

            if (visible && _shipListDirty)
            {
                _shipListDirty = false;
                RefreshShipList();
            }

            if (visible)
            {
                RefreshShipList();
            }
        }

        // ----- Window construction -----

        private void BuildWindow()
        {
            var options = new UguiWindowOptions
            {
                Title = "Flight Deck Manager",
                InitialX = _initialX.Value,
                InitialY = _initialY.Value,
                InitialWidth = _initialWidth.Value,
                InitialHeight = _initialHeight.Value,
                MinContentSize = new Vector2(400f, 120f),
                AspectLocked = false,
                ShowResizeHandle = true,
                ShowCollapseButton = true,
                ShowCloseButton = true,
            };

            _window = UguiWindowFactory.Create(options);
            _window.CloseClicked += () => SetVisible(false);
            _window.AddLockButton();

            // Stop clicks/drags inside this window from also panning the tactical map underneath.
            NoesisInputSuppressor.Register(_window.PanelRect, () => _visible);

            BuildColumnHeader(_window.ContentRect);
            BuildScrollArea(_window.ContentRect);
            BuildStatusBar(_window.ContentRect);
            BuildDock();

            // Core owns the geometry lifecycle: resolve the config (or the coded default when unset)
            // to a pixel rect, apply it now at build and on Reset, persist moves/resizes, and keep it
            // on-screen. Default is upper-right, UIScale-aware.
            _geometry = new WindowGeometry(_window, _initialX, _initialY, _initialWidth, _initialHeight,
                WindowGeometry.UiScale(DefaultDipX, DefaultDipY, DefaultDipWidth, DefaultDipHeight),
                _windowLock);
            _geometry.Install();
        }

        /// <summary>
        /// Builds the composite: this window is the parent container of a dock stack, and the
        /// native flight deck panel occupies the stack's hollow child slot.
        ///
        /// The slot paints nothing on purpose. This pack's overlay canvas draws ABOVE Noesis, so
        /// anything we rendered there would hide the native panel; leaving it empty lets the panel
        /// show through, and DockStack's thin frame around the union makes the two read as one
        /// control. The slot collapses when no deck is open, shrinking the composite back to just
        /// this window.
        /// </summary>
        private void BuildDock()
        {
            _stack = DockStack.Attach(_window.PanelRect, _window.Canvas);

            // On the stack's always-active root, NOT the window panel: hiding a UguiWindow
            // deactivates its panel GameObject, which would stop this component ticking and strand
            // the docked native window on screen with nothing left running to hide it.
            _dock = _stack.Root.AddComponent<WindowDock>();
            _dock.Host = _window.PanelRect;
            _dock.Target = _stack.Slot;
            _dock.IsHostVisible = () => _visible;
        }

        /// <summary>
        /// Feeds the native panel's measured size to the stack so the slot reserves exactly that
        /// much room. Per frame because the panel sizes itself from its own content and constraints,
        /// and changes as its dropdowns and tables do.
        /// </summary>
        private void UpdateDockStack()
        {
            if (_stack == null) return;

            bool docked = _dockedDeck != null && _dockedDeck.IsAlive;
            _stack.SetChildSizePx(docked ? _dockedDeck.DesiredSizePx : Vector2.zero);
        }

        /// <summary>Starts docking the native window that renders <paramref name="vm"/>.</summary>
        private void AttachDock(FlightDeckViewModel vm)
        {
            DetachDock();
            if (_dock == null) return;

            // Resolved through a live check of _openVm rather than a captured reference, so the
            // element reports itself dead the moment we switch ships or close.
            // Fill: the element is placed over the stack's slot, which is already positioned and
            // sized for it — the stack owns direction, this owns the native window.
            _dockedDeck = new NativeWindowDockedElement(() => ReferenceEquals(_openVm, vm) ? vm : null, DockSide.Fill)
            {
                // The composite already has one title bar and one close button — this window's own
                // are redundant, and its close button would drop it out of the dock silently.
                SuppressChrome = true,
            };
            _dock.Add(_dockedDeck);
        }

        private void DetachDock()
        {
            if (_dockedDeck == null) return;
            _dockedDeck.Close();
            _dock?.Remove(_dockedDeck);
            _dockedDeck = null;
        }

        /// <summary>Fixed column header pinned to the top of the content area.</summary>
        private void BuildColumnHeader(RectTransform parent)
        {
            _headerRow = CreateStrip(parent, "ColumnHeader", HeaderHeight, anchorTop: true, offset: 0f);

            // Inset by the scrollbar so header columns line up with rows, which sit left of it.
            _headerRow.offsetMax = new Vector2(-ScrollbarWidth, _headerRow.offsetMax.y);

            var headerCells = new RectTransform[Columns.Length];
            for (int i = 0; i < Columns.Length; i++)
            {
                headerCells[i] = CreateCell(_headerRow, Columns[i].Header, DimTextColor, Columns[i].Align)
                    .rectTransform;
            }
            _headerRow.gameObject.AddComponent<GridRow>().Initialize(Columns, headerCells);

            // 1px separator under the header.
            GameObject sepGo = new GameObject("Separator");
            RectTransform sep = sepGo.AddComponent<RectTransform>();
            sep.SetParent(parent, false);
            sep.anchorMin = new Vector2(0f, 1f);
            sep.anchorMax = new Vector2(1f, 1f);
            sep.pivot = new Vector2(0.5f, 1f);
            sep.sizeDelta = new Vector2(0f, 1f);
            sep.anchoredPosition = new Vector2(0f, -HeaderHeight);
            sepGo.AddComponent<Image>().color = SeparatorColor;
        }

        /// <summary>
        /// Standard Unity ScrollRect/Viewport/Content stack with a visible vertical scrollbar,
        /// filling the space between the column header and the status bar.
        /// </summary>
        private void BuildScrollArea(RectTransform parent)
        {
            GameObject scrollGo = new GameObject("ScrollArea");
            RectTransform scrollRectTransform = scrollGo.AddComponent<RectTransform>();
            scrollRectTransform.SetParent(parent, false);
            scrollRectTransform.anchorMin = Vector2.zero;
            scrollRectTransform.anchorMax = Vector2.one;
            scrollRectTransform.offsetMin = new Vector2(0f, RowHeight); // leave room for status bar
            scrollRectTransform.offsetMax = new Vector2(0f, -(HeaderHeight + 1f));

            GameObject viewportGo = new GameObject("Viewport");
            RectTransform viewport = viewportGo.AddComponent<RectTransform>();
            viewport.SetParent(scrollRectTransform, false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-ScrollbarWidth, 0f);
            viewportGo.AddComponent<RectMask2D>();

            _listContent = new GameObject("Content").AddComponent<RectTransform>();
            _listContent.SetParent(viewport, false);
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            // Top-LEFT pivot and an explicit zero width offset. A new RectTransform defaults to
            // sizeDelta (100,100), so with stretched horizontal anchors the content was 100px
            // wider than the viewport — and a centred pivot pushed its left edge outside, which is
            // what shifted every row left of the header and fed the leading character to the
            // viewport mask. The header sits directly on the content rect, so it never moved,
            // which is why the two disagreed.
            _listContent.pivot = new Vector2(0f, 1f);
            _listContent.sizeDelta = new Vector2(0f, 0f);
            _listContent.anchoredPosition = Vector2.zero;

            VerticalLayoutGroup vlg = _listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 0f;
            // childControlHeight MUST be true or the layout ignores each row's LayoutElement and
            // falls back to the RectTransform's default 100px height (which is what made the
            // first version render giant, overlapping rows).
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            ContentSizeFitter fitter = _listContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Scrollbar scrollbar = CreateVerticalScrollbar(scrollRectTransform);

            ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.content = _listContent;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20f;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        private Scrollbar CreateVerticalScrollbar(Transform parent)
        {
            GameObject barGo = new GameObject("Scrollbar");
            RectTransform bar = barGo.AddComponent<RectTransform>();
            bar.SetParent(parent, false);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 1f);
            bar.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            bar.anchoredPosition = Vector2.zero;
            barGo.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);

            GameObject areaGo = new GameObject("Sliding Area");
            RectTransform area = areaGo.AddComponent<RectTransform>();
            area.SetParent(bar, false);
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(2f, 2f);
            area.offsetMax = new Vector2(-2f, -2f);

            GameObject handleGo = new GameObject("Handle");
            RectTransform handle = handleGo.AddComponent<RectTransform>();
            handle.SetParent(area, false);
            handle.sizeDelta = Vector2.zero;
            Image handleImage = handleGo.AddComponent<Image>();
            handleImage.color = new Color(1f, 1f, 1f, 0.25f);

            Scrollbar scrollbar = barGo.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            ColorBlock colors = scrollbar.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0.25f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.40f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.55f);
            scrollbar.colors = colors;
            return scrollbar;
        }

        /// <summary>Bottom strip: row count / hint text, plus a Refresh button.</summary>
        private void BuildStatusBar(RectTransform parent)
        {
            RectTransform strip = CreateStrip(parent, "StatusBar", RowHeight, anchorTop: false, offset: 0f);

            HorizontalLayoutGroup hlg = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
            ConfigureRowLayout(hlg);

            _statusText = CreateCell(strip, string.Empty, DimTextColor, TextAnchor.MiddleLeft);
            _statusText.supportRichText = true;
            // The status bar is a simple flow strip, not part of the column grid, so it keeps a
            // layout group — the legend just needs to take the space the Refresh button doesn't.
            LayoutElement legendLayout = _statusText.gameObject.AddComponent<LayoutElement>();
            legendLayout.flexibleWidth = 1f;
        }


        /// <summary>
        /// Binds the list to the game's own observable collection of player units, so ships
        /// entering or leaving the scene update it without a manual refresh. Re-checked each tick
        /// because the collection instance can change across scene loads.
        /// </summary>
        private void WatchPlayerObjects()
        {
            ObservableCollection<ObjectBase> live = null;
            try
            {
                if (SeaPower.Singleton<ObjectsManager>.InstanceExists())
                {
                    live = SeaPower.Singleton<ObjectsManager>.Instance._listOfAllPlayerObjects;
                }
            }
            catch
            {
                live = null;
            }

            if (ReferenceEquals(live, _watchedPlayerObjects)) return;

            if (_watchedPlayerObjects != null)
            {
                _watchedPlayerObjects.CollectionChanged -= OnPlayerObjectsChanged;
            }

            _watchedPlayerObjects = live;

            if (_watchedPlayerObjects != null)
            {
                _watchedPlayerObjects.CollectionChanged += OnPlayerObjectsChanged;
                _shipListDirty = true;
            }
        }

        private void UnwatchPlayerObjects()
        {
            if (_watchedPlayerObjects == null) return;
            _watchedPlayerObjects.CollectionChanged -= OnPlayerObjectsChanged;
            _watchedPlayerObjects = null;
        }

        private void OnPlayerObjectsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _shipListDirty = true;
        }

        // ----- List population -----

        private void RefreshShipList()
        {
            if (_listContent == null) return;

            for (int i = _listContent.childCount - 1; i >= 0; i--)
            {
                Destroy(_listContent.GetChild(i).gameObject);
            }

            _rowSelection.Clear();
            UnbindAllStatus();

            List<ObjectBase> ships;
            try
            {
                ships = UnityEngine.Object.FindObjectsOfType<ObjectBase>()
                    .Where(ob => ob != null && ob._obp != null && ob._obp._flightDeck != null && ob.AcceptsOrdersFromPlayer)
                    .OrderBy(ob => ob.getUIDAndName())
                    .ToList();
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: ship enumeration failed: {e.Message}");
                ships = new List<ObjectBase>();
            }

            foreach (ObjectBase ship in ships)
            {
                BuildShipRow(ship);
            }

            if (_statusText != null)
            {
                _statusText.text = ships.Count == 0
                    ? "No flight decks found"
                    : FlightDeckGlyphs.Legend();
            }
        }

        private void BuildShipRow(ObjectBase ship)
        {
            SeaPower.FlightDeck fd = ship._obp._flightDeck;

            GameObject rowGo = new GameObject("Row");
            rowGo.transform.SetParent(_listContent, false);
            rowGo.AddComponent<RectTransform>();

            LayoutElement layout = rowGo.AddComponent<LayoutElement>();
            layout.minHeight = RowHeight;
            layout.preferredHeight = RowHeight;

            Image bg = rowGo.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0f);

            Button button = rowGo.AddComponent<Button>();
            button.targetGraphic = bg;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0f);
            colors.highlightedColor = RowHoverColor;
            colors.pressedColor = RowHoverColor;
            colors.selectedColor = new Color(1f, 1f, 1f, 0f);
            button.colors = colors;

            // Selection is its own layer, created before the cells so it paints under the text but
            // over the Button's hover tint. Deliberately NOT done through the Button's ColorBlock:
            // Selectable.colors doesn't re-run the current state transition when assigned, so a
            // colour set that way stays invisible until the pointer next enters or leaves the row.
            // ignoreLayout keeps the HorizontalLayoutGroup from treating it as another column.
            GameObject selectionGo = new GameObject("Selection");
            RectTransform selectionRect = selectionGo.AddComponent<RectTransform>();
            selectionRect.SetParent(rowGo.transform, false);
            selectionRect.anchorMin = Vector2.zero;
            selectionRect.anchorMax = Vector2.one;
            selectionRect.offsetMin = Vector2.zero;
            selectionRect.offsetMax = Vector2.zero;

            Image selection = selectionGo.AddComponent<Image>();
            selection.raycastTarget = false;
            _rowSelection[ship] = selection;
            ApplyRowSelection(ship, selection);

            Text unitText = CreateCell(rowGo.transform, ship.getUIDAndName(), TextColor, Columns[0].Align);
            Text statusText = CreateCell(rowGo.transform, string.Empty, DimTextColor, Columns[1].Align);
            Text glyphText = CreateCell(rowGo.transform, string.Empty, TextColor, Columns[2].Align);
            Text aircraftText = CreateCell(rowGo.transform, string.Empty, DimTextColor, Columns[3].Align);
            Text deckText = CreateCell(rowGo.transform, string.Empty, DimTextColor, Columns[4].Align);
            glyphText.supportRichText = true;

            rowGo.AddComponent<GridRow>().Initialize(Columns, new[]
            {
                unitText.rectTransform, statusText.rectTransform, glyphText.rectTransform,
                aircraftText.rectTransform, deckText.rectTransform,
            });

            BindStatus(ship, fd, statusText, glyphText, aircraftText, deckText);

            RowClickHandler click = rowGo.AddComponent<RowClickHandler>();
            click.OnLeftClick = () => OpenNativeFlightDeck(ship);
            click.OnRightClick = screenPoint => ShowContextMenuFor(ship, screenPoint);
        }

        /// <summary>
        /// Subscribes a row to its ship's flight deck task list and paints the first values.
        ///
        /// One handler per ship, no polling: FlightDeckTasks is a TrulyObservableCollection, which
        /// re-raises CollectionChanged whenever any task's own properties change as well as on
        /// add/remove. So a task moving from "await ground crew" to "ready to launch" reaches us
        /// through the same event as a task appearing. In a scene with many carriers this costs
        /// nothing while nothing is happening, which a per-frame sweep could not manage — and the
        /// expensive part of a per-frame version wouldn't be the enumeration anyway, it would be
        /// re-setting Text every frame and forcing a canvas rebuild.
        /// </summary>
        private void BindStatus(ObjectBase ship, SeaPower.FlightDeck deck,
            Text statusText, Text glyphText, Text aircraftText, Text deckText)
        {
            if (ship == null || deck == null) return;

            UnbindStatus(ship);

            var binding = new RowStatusBinding
            {
                Deck = deck,
                StatusText = statusText,
                GlyphText = glyphText,
                AircraftText = aircraftText,
                DeckText = deckText,
            };

            binding.Handler = (sender, e) => RefreshStatus(ship);
            _statusBindings[ship] = binding;

            try
            {
                deck.FlightDeckTasks.CollectionChanged += binding.Handler;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: could not subscribe to flight deck tasks for '{Describe(ship)}': {e.Message}");
            }

            RefreshStatus(ship);
        }

        /// <summary>
        /// Detaches from exactly the collection instance we attached to. Keeping the FlightDeck
        /// reference in the binding matters: resolving it again through the ship could hand back a
        /// different object (or throw) once the unit is being torn down, leaving our handler
        /// attached to a collection nothing will ever unsubscribe from.
        /// </summary>
        private void UnbindStatus(ObjectBase ship)
        {
            if (ship == null || !_statusBindings.TryGetValue(ship, out RowStatusBinding binding)) return;
            _statusBindings.Remove(ship);

            if (binding.Deck == null || binding.Handler == null) return;
            try { binding.Deck.FlightDeckTasks.CollectionChanged -= binding.Handler; }
            catch { /* collection already gone with the unit */ }
        }

        private void UnbindAllStatus()
        {
            foreach (KeyValuePair<ObjectBase, RowStatusBinding> pair in _statusBindings)
            {
                RowStatusBinding binding = pair.Value;
                if (binding.Deck == null || binding.Handler == null) continue;
                try { binding.Deck.FlightDeckTasks.CollectionChanged -= binding.Handler; }
                catch { }
            }
            _statusBindings.Clear();
        }

        /// <summary>Repaints one row's status cells from its deck's current tasks.</summary>
        private void RefreshStatus(ObjectBase ship)
        {
            if (ship == null || !_statusBindings.TryGetValue(ship, out RowStatusBinding binding)) return;

            SeaPower.FlightDeck deck = binding.Deck;
            if (deck == null) return;

            FlightDeckStatus status = FlightDeckStatus.Summarize(deck);

            SetTextIfChanged(binding.StatusText, status.PrimaryLabel);
            SetTextIfChanged(binding.GlyphText, FlightDeckGlyphs.Render(status));

            try
            {
                SetTextIfChanged(binding.AircraftText, $"{deck.AircraftCurrentlyOnBoard}/{deck.MaxAircraftOnBoard}");
                SetTextIfChanged(binding.DeckText, $"{deck.AssignedDeckParkSlots}/{deck.DeckParkSlots}");
            }
            catch
            {
                // Capacity getters walk the task list; a unit mid-teardown can throw.
            }
        }

        /// <summary>
        /// Assigning Text.text dirties the layout and forces a canvas rebuild even when the value
        /// is identical, so compare first — these repaint on every task property change.
        /// </summary>
        private static void SetTextIfChanged(Text text, string value)
        {
            if (text != null && text.text != value) text.text = value;
        }

        /// <summary>Shows whether this row's unit is the one whose flight deck is open.</summary>
        private void ApplyRowSelection(ObjectBase ship, Image selection)
        {
            if (selection == null) return;
            selection.color = ReferenceEquals(ship, _openShip) ? RowSelectedColor : new Color(1f, 1f, 1f, 0f);
        }

        /// <summary>Re-applies the selected tint across every row after the open deck changes.</summary>
        private void RefreshRowSelection()
        {
            foreach (KeyValuePair<ObjectBase, Image> pair in _rowSelection)
            {
                if (pair.Value != null) ApplyRowSelection(pair.Key, pair.Value);
            }
        }

        // ----- Opening the game's native flight deck window -----

        /// <summary>
        /// One child window at a time: clicking the ship that's already open closes it, clicking
        /// a different ship switches the child window over to that ship.
        /// </summary>
        private void OpenNativeFlightDeck(ObjectBase ship)
        {
            if (ship == null || ship._obp?._flightDeck == null) return;

            if (ReferenceEquals(_openShip, ship))
            {
                CloseChildWindow();
                return;
            }

            // Switching ships: tear the current one down first so we never hold two view models.
            CloseChildWindow();

            FlightDeckViewModel vm;
            try
            {
                vm = new FlightDeckViewModel(ship);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: failed to build FlightDeckViewModel for '{Describe(ship)}': {e.Message}");
                return;
            }

            // Only start tracking once the window actually opened; otherwise dispose immediately
            // rather than leaving an orphaned view model subscribed to the ship's flight deck.
            if (!TryExecuteOpen(vm, ship))
            {
                SafeDispose(vm, ship);
                return;
            }

            _openShip = ship;
            _openVm = vm;
            WatchWindowCollection();
            AttachDock(vm);
            RefreshRowSelection();


            if (_debugLogging.Value)
            {
                Logger.LogInfo($"{PluginName}: opened native flight deck for '{Describe(ship)}'.");
            }
        }

        private bool TryExecuteOpen(FlightDeckViewModel vm, ObjectBase ship)
        {
            try
            {
                if (MainGameView.Instance == null)
                {
                    Logger.LogWarning($"{PluginName}: MainGameView not available; cannot open flight deck window.");
                    return false;
                }

                WindowHostCommands.OpenWindowCommand.Execute(vm, MainGameView.Instance);

                // Confirm via the SAME routing the open used. CanExecute on CloseWindowCommand is
                // the game's own "is this window open" test (WindowHost.CanCloseWindow), and
                // routing it from MainGameView reaches whichever host actually took the window.
                //
                // This used to check WindowHostManager.CurrentHost.Windows, which is wrong:
                // CurrentHost is simply the last WindowHost constructed, and other views (the
                // Tactical Display for one) declare their own. With one of those open, the window
                // went to MainGameView's host while we inspected a different one — so opens were
                // reported as rejected and, worse, closes silently removed nothing.
                if (!WindowHostCommands.CloseWindowCommand.CanExecute(vm, MainGameView.Instance))
                {
                    Logger.LogWarning($"{PluginName}: WindowHost did not accept the flight deck window for '{Describe(ship)}'.");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: failed to open flight deck for '{Describe(ship)}': {e.Message}");
                return false;
            }
        }

        // ----- Child window placement -----




        // ----- View model lifetime -----

        /// <summary>
        /// Subscribes to the live WindowHost.Windows collection so we can dispose our view model
        /// when its window is closed (by the window's own X button, or anything else that removes
        /// it). Re-subscribes if the collection instance changed, e.g. after a scene reload.
        /// </summary>
        /// <summary>
        /// Notices the child window being closed by anything other than us.
        ///
        /// Uses the same signal the game's own ToggleWindowBroker uses to track whether its window
        /// is open: WindowHost raises CloseWindowCommand.UpdateCanExecute() on every open and
        /// close, and CanExecute answers "is this view model currently hosted". That is
        /// host-agnostic, which matters because there is more than one WindowHost in a mission and
        /// we cannot assume which one took our window.
        /// </summary>
        private void WatchWindowCollection()
        {
            if (_watchingHostCommands) return;
            _watchingHostCommands = true;
            WindowHostCommands.CloseWindowCommand.CanExecuteChanged += OnHostWindowsChanged;
        }

        private void UnwatchWindowCollection()
        {
            if (!_watchingHostCommands) return;
            _watchingHostCommands = false;
            WindowHostCommands.CloseWindowCommand.CanExecuteChanged -= OnHostWindowsChanged;
        }

        private void OnHostWindowsChanged(object sender, EventArgs e)
        {
            if (_openVm == null || MainGameView.Instance == null) return;

            bool stillOpen;
            try { stillOpen = WindowHostCommands.CloseWindowCommand.CanExecute(_openVm, MainGameView.Instance); }
            catch { return; }

            if (!stillOpen) ForgetChildWindow();
        }

        /// <summary>Closes the child window (if still open) and disposes its view model.</summary>
        private void CloseChildWindow()
        {
            if (_openVm == null) return;

            FlightDeckViewModel vm = _openVm;
            ObjectBase ship = _openShip;

            // Clear our state BEFORE closing: the close re-enters OnHostWindowsChanged, which
            // must not try to dispose this a second time.
            _openVm = null;
            _openShip = null;
            DetachDock();
            RefreshRowSelection();

            // Routed close, symmetric with the routed open — reaches the host that owns it.
            try
            {
                if (MainGameView.Instance != null)
                {
                    WindowHostCommands.CloseWindowCommand.Execute(vm, MainGameView.Instance);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: failed to close flight deck window: {e.Message}");
            }

            SafeDispose(vm, ship);
        }

        /// <summary>Disposes our view model after its window was removed by something else.</summary>
        private void ForgetChildWindow()
        {
            FlightDeckViewModel vm = _openVm;
            ObjectBase ship = _openShip;
            _openVm = null;
            _openShip = null;
            DetachDock();
            RefreshRowSelection();
            SafeDispose(vm, ship);
        }

        /// <summary>
        /// Disposing a FlightDeckViewModel is what unsubscribes it from
        /// FlightDeck._vehiclesOnBoard.CollectionChanged — skipping it leaks the view model for as
        /// long as the ship lives. Never allowed to throw.
        /// </summary>
        private void SafeDispose(FlightDeckViewModel vm, ObjectBase ship)
        {
            if (vm == null) return;
            try
            {
                vm.Dispose();
                if (_debugLogging.Value)
                {
                    Logger.LogInfo($"{PluginName}: disposed flight deck view model for '{Describe(ship)}'.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: FlightDeckViewModel.Dispose failed for '{Describe(ship)}': {e.Message}");
            }
        }

        /// <summary>Closes the child window and stops watching the host.</summary>
        private void CloseAndDisposeAll()
        {
            UnwatchWindowCollection();
            CloseChildWindow();
        }

        /// <summary>
        /// A scene change tears down MainGameView (and with it the WindowHost and its Windows
        /// collection), so the view model we opened is orphaned — drop it here rather than
        /// holding a reference to a ship from the previous scene.
        /// </summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            UnwatchWindowCollection();
            ForgetChildWindow();
            SetVisible(false);
        }

        private void ShowContextMenuFor(ObjectBase ship, Vector2 screenPoint)
        {
            if (_window == null || !GameContextMenu.IsValidTarget(ship)) return;

            // Select first: several of the game's own context-menu commands act on
            // MainGameViewModel.SelectedViewModel rather than on a captured target.
            try
            {
                if (Singleton<ObjectsManager>.InstanceExists())
                {
                    Singleton<ObjectsManager>.Instance.setActiveObject(ship);
                }
                if (Singleton<RenderPosition>.InstanceExists())
                {
                    Singleton<RenderPosition>.Instance.SelectedObject = ship;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: selection before context menu failed: {e.Message}");
            }

            try
            {
                var source = ship.ViewModel as SeapowerUI.IContextMenuObject;
                var items = source?.GetContextMenuItems();
                if (items == null || items.Count == 0) return;

                UguiContextMenu.Show(_window.Canvas, items, screenPoint, onClosed: null, Logger);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{PluginName}: context menu failed: {e.Message}");
            }
        }

        private static string Describe(ObjectBase ship)
        {
            try { return ship.getUIDAndName(); }
            catch { return "<unknown>"; }
        }

        // ----- Layout helpers -----

        /// <summary>
        /// Shared row-layout settings. childControl* MUST be true so each cell's LayoutElement
        /// (preferred/flexible width, row height) is actually honored — with it false, Unity
        /// uses the RectTransform's default 100x100, which is what produced the overlapping,
        /// oversized rows in the first version.
        /// </summary>
        private static void ConfigureRowLayout(HorizontalLayoutGroup hlg)
        {
            hlg.padding = new RectOffset(4, 4, 0, 0);
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
        }

        private RectTransform CreateStrip(RectTransform parent, string name, float height, bool anchorTop, float offset)
        {
            GameObject go = new GameObject(name);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            float y = anchorTop ? 1f : 0f;
            rect.anchorMin = new Vector2(0f, y);
            rect.anchorMax = new Vector2(1f, y);
            rect.pivot = new Vector2(0.5f, y);
            rect.sizeDelta = new Vector2(0f, height);
            rect.anchoredPosition = new Vector2(0f, anchorTop ? -offset : offset);
            return rect;
        }

        /// <summary>
        /// A single grid cell. Deliberately carries no LayoutElement and no sizing of its own —
        /// position and width come from Grid.Layout, which is the only thing that decides column
        /// geometry for both the header and every row.
        /// </summary>
        private Text CreateCell(Transform parent, string content, Color color, TextAnchor alignment)
        {
            GameObject go = new GameObject("Cell");
            go.transform.SetParent(parent, false);

            Text text = go.AddComponent<Text>();
            text.text = content;
            text.font = UguiWindow.GetUiFont();
            text.fontSize = FontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            // Clip an overlong unit name at its column edge rather than letting it run across the
            // neighbouring columns.
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }


        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

    }

    /// <summary>Dispatches left/right clicks on a list row.</summary>
    internal class RowClickHandler : MonoBehaviour, IPointerClickHandler
    {
        public Action OnLeftClick;
        public Action<Vector2> OnRightClick;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                OnLeftClick?.Invoke();
            }
            else if (eventData.button == PointerEventData.InputButton.Right)
            {
                OnRightClick?.Invoke(eventData.position);
            }
        }
    }
}

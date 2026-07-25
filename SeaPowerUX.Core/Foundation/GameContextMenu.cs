using System;
using BepInEx.Logging;
using SeaPower;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Opens the game's own right-click context menu for a given unit/weapon at an explicit
    /// screen point. Generalized from SeaPowerUX.TacticalPiP's right-click handling (the raycast that
    /// finds a target from a click is inherently plugin-specific — e.g. PiP translates a click
    /// into a ray against the mirrored camera view — but everything from "I have an ObjectBase
    /// and a screen point" onward is identical for any plugin, so it lives here once).
    ///
    /// Calls SeapowerUI.ViewModels.MainGameViewModel.OpenContextMenuCommand directly — a plain
    /// ICommand on a plain MonoBehaviour — so no Harmony patch is involved at all.
    ///
    /// KNOWN LIMITATION (accepted for now): Noesis draws the context menu via a camera-attached
    /// render pass that always composites BEFORE Unity's ScreenSpaceOverlay canvases, so no
    /// sortingOrder can put a UGUI window's canvas above it. The only available fix today is
    /// hiding the host window entirely while the menu is open, via the onOpened/onClosed
    /// callbacks below — not ideal for a window meant to stay usable while the menu is up, but
    /// it's what SeaPowerUX.TacticalPiP already does, and every consumer of this helper inherits the same
    /// tradeoff until a better placement mechanism is found.
    /// </summary>
    public static class GameContextMenu
    {
        /// <summary>
        /// True for the same targets SeaPower.InputHandler.handleMouseInputs() would open a
        /// menu for: a unit or weapon, excluding Chaff, and (for units only) restricted to ones
        /// that accept player orders.
        /// </summary>
        public static bool IsValidTarget(ObjectBase ob)
        {
            if (ob == null) return false;
            if (!ob.isUnit() && !ob.isWeapon()) return false;
            if (ob._type == ObjectBase.ObjectType.Chaff) return false;
            if (ob.isUnit() && !ob.AcceptsOrdersFromPlayer) return false;
            return true;
        }

        /// <summary>
        /// Opens the game's context menu for <paramref name="target"/> at
        /// <paramref name="unityScreenPoint"/> (Unity screen space, bottom-left origin).
        /// <paramref name="onOpened"/> runs first — the caller's chance to hide its own window
        /// per the KNOWN LIMITATION above — then the menu is positioned and opened.
        /// <paramref name="onClosed"/> (optional) fires once, the next time this specific menu
        /// closes, however that happens (item click, click-away, Escape). Returns false without
        /// side effects if the target isn't valid or a required game member is missing.
        /// </summary>
        public static bool TryOpen(
            ObjectBase target,
            Vector2 unityScreenPoint,
            Action onOpened,
            Action onClosed,
            ManualLogSource logger)
        {
            if (!IsValidTarget(target))
            {
                return false;
            }

            try
            {
                Noesis.ContextMenu menu = SeapowerUI.MainGameView.Instance?.ContextMenuCanvas?.ContextMenu;
                if (menu == null)
                {
                    return false;
                }

                if (onClosed != null)
                {
                    AttachOneShotClosedHandler(menu, onClosed, logger);
                }

                onOpened?.Invoke();

                // Noesis screen coordinates are top-left-origin DIPs (Visual.PointToScreen
                // convention), Unity's screen point is bottom-left-origin, hence the Y flip.
                // AbsolutePoint placement is used because a click routed through a UGUI window
                // never reaches Noesis's own input pipeline, so its mouse-relative placement
                // modes (Mouse/MousePoint) have no real click event to derive a position from.
                menu.Placement = Noesis.PlacementMode.AbsolutePoint;
                menu.HorizontalOffset = unityScreenPoint.x;
                menu.VerticalOffset = Screen.height - unityScreenPoint.y;

                Globals._mainGameViewModel?.OpenContextMenuCommand?.Execute(target);
                return true;
            }
            catch (Exception e)
            {
                logger?.LogWarning($"GameContextMenu.TryOpen failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// The menu is a single instance shared by the whole game (also used for normal,
        /// non-PiP/non-plugin right-clicks), so its Closed event can fire for reasons unrelated
        /// to this call — a one-shot subscription that detaches itself on first fire keeps each
        /// caller's callback scoped to the specific menu invocation it asked about.
        /// </summary>
        private static void AttachOneShotClosedHandler(Noesis.ContextMenu menu, Action onClosed, ManualLogSource logger)
        {
            Noesis.RoutedEventHandler handler = null;
            handler = (sender, args) =>
            {
                menu.Closed -= handler;
                try { onClosed(); }
                catch (Exception e) { logger?.LogWarning($"GameContextMenu onClosed callback failed: {e.Message}"); }
            };
            menu.Closed += handler;
        }
    }
}

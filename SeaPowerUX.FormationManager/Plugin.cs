using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SeaPowerUX.Core.Foundation;
using SeapowerUI.ViewModels;

namespace SeaPowerUX.FormationManager
{
    /// <summary>
    /// Placeholder for Formation Manager features. Currently does nothing.
    ///
    /// It was the pack's first member, when its job was making the Formation Manager panel
    /// remember its position. That behaviour now applies to every one of the game's windows via
    /// SeaPowerUX.WindowMemory, so nothing panel-specific is left. The shell is kept because
    /// Formation Manager work is planned; it is deliberately inert until then.
    ///
    /// DELIBERATELY MINIMAL. It starts no shared services, installs no Harmony patches and touches
    /// no game state — a plugin that does nothing should not be able to break anything. In
    /// particular it does NOT call HudVisibility.Enable() (it owns no UI to hide) or
    /// CoreApi.Validate() (it uses none of Core's window surface, so failing that check would
    /// disable it for reasons that don't apply to it).
    ///
    /// History worth keeping: it used to carry its own drag-capture poll loop and an override of
    /// <c>UI_Parameters.FM_WindowPositionX/Y</c>, the values this panel's XAML reads through
    /// <c>{sp:UIResource}</c>, meant to place the window before construction. Neither worked. The
    /// poll loop duplicated Core's and silently stopped persisting, and the UI_Parameters override
    /// never removed the flicker because <c>UIResource.ProvideValue</c> is a MarkupExtension
    /// evaluated when the XAML is parsed, not per window open.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.moseysauce.seapowerux.formationmanager";
        public const string PluginName = "SeaPowerUX.FormationManager";
        public const string PluginVersion = "0.5.0";

        /// <summary>Core build this plugin was compiled against; see CoreCompatibility.</summary>
        private const string RequiredCoreVersion = "0.5.0";

        private ConfigEntry<bool> _enabled;

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            // Before anything else touches Core: a mismatched or missing Core otherwise fails
            // later as an opaque type-load error with nothing naming the cause.
            if (!CoreVersionOk()) return;

            _enabled = Config.Bind("General", "Enabled", true,
                "Master kill switch for this panel plugin.");

            if (!_enabled.Value)
            {
                Logger.LogInfo($"{PluginName}: disabled via config.");
                return;
            }

            // Registered so its setting takes part in the toolbar's Set Default / Reset pair.
            UxSettings.RegisterConfig(Config);

            // Confirms the panel this plugin is named for still exists, so the shell reports a
            // game-side change rather than silently sitting on a stale assumption.
            if (!ValidateGameApi()) return;

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (no features yet — placeholder).");
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

        private bool ValidateGameApi()
        {
            return new GameApiValidator()
                .Type(typeof(FormationManagerViewModel), "SeapowerUI.ViewModels.FormationManagerViewModel (target ViewModel)")
                .Validate(Logger, PluginName, PluginVersion);
        }
    }
}

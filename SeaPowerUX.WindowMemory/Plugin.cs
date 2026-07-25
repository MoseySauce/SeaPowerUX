using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeaPowerUX.Core.Foundation;

namespace SeaPowerUX.WindowMemory
{
    /// <summary>
    /// Remembers where you put the game's OWN windows, and puts them back.
    ///
    /// Sea Power has no window position persistence at all: a window's element is destroyed on
    /// close and rebuilt from its DataTemplate, so it reopens wherever the template says. This
    /// supplies the missing behaviour for every window uniformly.
    ///
    /// WHY IT'S A SEPARATE PLUGIN. It is the only part of the pack that is purely a quality-of-life
    /// change to the base game's UI and is needed by nothing else — PiP and Air Boss don't consume
    /// anything from it. It is also the most exposed thing the pack does: it Harmony-patches a game
    /// UI class and writes X/Y into windows it didn't create. If a game update reworks those
    /// windows, deleting this one DLL restores stock behaviour and leaves the rest of the pack
    /// working, instead of taking everything down with it.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.moseysauce.seapowerux.windowmemory";
        public const string PluginName = "SeaPowerUX.WindowMemory";
        public const string PluginVersion = "0.5.0";

        private const string RequiredCoreVersion = "0.5.0";

        private ConfigEntry<bool> _enabled;
        private Harmony _harmony;

        private void Awake()
        {
            if (!CoreVersionOk()) return;

            _enabled = Config.Bind("General", "Enabled", true,
                "Master kill switch. Turn off to leave the game's own window positions alone.");

            if (!_enabled.Value)
            {
                Logger.LogInfo($"{PluginName}: disabled via config.");
                return;
            }

            if (!ValidateGameApi()) return;

            ConfigEntry<bool> debug = Config.Bind("General", "Debug", false,
                "Log each window's restore/anchor-correct/save decision at Info level, for diagnosing a window that won't stay where you put it.");
            WindowPositionMemory.Verbose = debug.Value;

            _harmony = new Harmony(PluginGuid);

            UxSettings.RegisterConfig(Config);
            UxSettings.ApplyShippedDefaultsOnce();

            WindowPositionMemory.Enable(Logger, _harmony);

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            WindowPositionMemory.Disable();
            _harmony?.UnpatchSelf();
        }

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
            return CoreApi.Validate(Logger, PluginName, PluginVersion);
        }
    }
}

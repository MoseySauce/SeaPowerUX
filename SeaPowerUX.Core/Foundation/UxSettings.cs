using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// A config file owned by Core rather than by any one plugin.
    ///
    /// Core deliberately has no <c>[BepInPlugin]</c> (it's a shared dependency library, not a
    /// plugin), so it has no <c>Config</c> handed to it by BepInEx. But Core now owns state that
    /// genuinely belongs to the pack as a whole rather than to any single member: the shared
    /// toolbar's position, and remembered positions for the game's own windows. Storing those in
    /// whichever plugin happened to initialise first would make them disappear when that plugin
    /// is disabled, so Core keeps its own file.
    ///
    /// Uses BepInEx's own <see cref="ConfigFile"/> (saveOnInit: true), so entries auto-persist on
    /// <c>.Value</c> writes exactly like a plugin's own config, and the file is human-editable in
    /// the same place as every other one.
    /// </summary>
    public static class UxSettings
    {
        private const string FileName = "com.moseysauce.seapowerux.core.cfg";

        private static ConfigFile _file;

        public static ConfigFile File
        {
            get
            {
                if (_file == null)
                {
                    _file = new ConfigFile(Path.Combine(Paths.ConfigPath, FileName), saveOnInit: true);
                }
                return _file;
            }
        }

        public static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            return File.Bind(section, key, defaultValue, description);
        }

        private static readonly List<ConfigFile> RegisteredConfigs = new List<ConfigFile>();

        /// <summary>
        /// Raised after every registered setting has been put back to its shipped default, so
        /// plugins can re-apply the restored values to live UI (a window won't move on its own
        /// just because the number behind it changed).
        /// </summary>
        public static event Action DefaultsRestored;

        /// <summary>
        /// Lets a plugin's own config take part in "reset to defaults". Call from Awake with the
        /// plugin's <c>Config</c>.
        /// </summary>
        public static void RegisterConfig(ConfigFile config)
        {
            if (config != null && !RegisteredConfigs.Contains(config)) RegisteredConfigs.Add(config);
        }

        /// <summary>
        /// Restores every registered setting — Core's own and each plugin's — from the saved
        /// default layout, then tells listeners to re-apply.
        /// </summary>
        public static void ResetAllToDefaults()
        {
            RegisterConfig(File);
            UxDefaults.Restore(RegisteredConfigs);

            try { DefaultsRestored?.Invoke(); }
            catch { }
        }

        /// <summary>Captures the current layout as the default the reset button restores to.</summary>
        public static bool CaptureCurrentAsDefaults()
        {
            RegisterConfig(File);
            return UxDefaults.Capture(RegisteredConfigs);
        }

        /// <summary>
        /// Applies the shipped default layout once on a fresh install. Guarded by a flag in Core's
        /// own config rather than by inspecting each setting: a player who happens to have left a
        /// value at its coded default shouldn't have the rest of their layout overwritten.
        /// </summary>
        public static void ApplyShippedDefaultsOnce()
        {
            ConfigEntry<bool> applied = Bind(
                "Defaults", "Applied", false,
                "Set once the shipped default layout has been applied. Set to false to take the shipped layout again on next launch.");

            if (applied.Value || !UxDefaults.Exists) return;

            ResetAllToDefaults();
            applied.Value = true;
        }

        /// <summary>
        /// Makes an arbitrary string safe to use as a BepInEx config key. BepInEx rejects
        /// <c>= \n \t \\ " ' [ ]</c> in section/key names, and our keys are derived from live
        /// type names and window headers, so they can't be trusted to be clean.
        /// </summary>
        public static string SanitizeKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unnamed";

            char[] chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '=' || c == '\n' || c == '\t' || c == '\\' || c == '"' || c == '\'' ||
                    c == '[' || c == ']' || c == '\r')
                {
                    chars[i] = '_';
                }
            }
            return new string(chars).Trim();
        }
    }
}

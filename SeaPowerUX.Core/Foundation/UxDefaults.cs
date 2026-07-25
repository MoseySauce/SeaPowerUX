using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// A saved snapshot of every SeaPowerUX setting, used as the pack's default layout.
    ///
    /// Two jobs. It ships with the mod so a fresh install starts with a sensible arrangement
    /// instead of every window at its coded fallback, and it backs the toolbar's split button:
    /// "Set Default" captures the current layout into it, "Reset" puts that layout back.
    ///
    /// Written as plain text keyed by config file, so it can be edited or diffed by hand and
    /// committed to the repo:
    /// <code>
    /// [com.moseysauce.seapowerux.tacticalpip]
    /// General.InitialWidth = 396
    /// </code>
    ///
    /// Values are stored with <c>GetSerializedValue()</c> and applied with
    /// <c>SetSerializedValue()</c> — the same round-trip BepInEx uses for the config files
    /// themselves — so every setting type is handled without this class knowing any of them.
    /// </summary>
    public static class UxDefaults
    {
        private const string FileName = "com.moseysauce.seapowerux.defaults.cfg";

        /// <summary>
        /// Core's own bookkeeping, deliberately excluded from both capture and restore.
        ///
        /// It records that the shipped layout has been applied once. Restoring it would set it
        /// back to false — so the next launch would re-apply the shipped defaults over whatever
        /// the player changed after their reset. Capturing it is equally wrong: a defaults file is
        /// a layout, not a record of whether that layout has been used yet.
        /// </summary>
        private const string BookkeepingSection = "Defaults";
        private const string BookkeepingKey = "Applied";

        private static bool IsBookkeeping(ConfigEntryBase entry)
        {
            return entry.Definition.Section == BookkeepingSection
                && entry.Definition.Key == BookkeepingKey;
        }

        public static string Path => System.IO.Path.Combine(Paths.ConfigPath, FileName);

        public static bool Exists => System.IO.File.Exists(Path);

        /// <summary>Writes the current value of every registered setting as the new default.</summary>
        public static bool Capture(IEnumerable<ConfigFile> configs)
        {
            try
            {
                var text = new StringBuilder();
                text.AppendLine("## SeaPowerUX default layout.");
                text.AppendLine("## Written by the toolbar's \"Set Default\" button; restored by \"Reset\".");
                text.AppendLine();

                foreach (ConfigFile config in configs)
                {
                    if (config == null) continue;

                    text.Append('[').Append(NameOf(config)).AppendLine("]");

                    foreach (ConfigEntryBase entry in config.GetConfigEntries())
                    {
                        if (entry == null || IsBookkeeping(entry)) continue;
                        text.Append(entry.Definition.Section).Append('.').Append(entry.Definition.Key)
                            .Append(" = ").AppendLine(entry.GetSerializedValue());
                    }

                    text.AppendLine();
                }

                System.IO.File.WriteAllText(Path, text.ToString());
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Applies the saved defaults to the given configs. Any setting the file doesn't mention
        /// falls back to its coded default, so this always produces a complete reset rather than a
        /// partial one.
        /// </summary>
        public static void Restore(IEnumerable<ConfigFile> configs)
        {
            Dictionary<string, Dictionary<string, string>> saved = Read();

            foreach (ConfigFile config in configs)
            {
                if (config == null) continue;

                saved.TryGetValue(NameOf(config), out Dictionary<string, string> values);

                foreach (ConfigEntryBase entry in config.GetConfigEntries())
                {
                    if (entry == null || IsBookkeeping(entry)) continue;

                    string key = entry.Definition.Section + "." + entry.Definition.Key;

                    try
                    {
                        if (values != null && values.TryGetValue(key, out string serialized))
                        {
                            entry.SetSerializedValue(serialized);
                        }
                        else
                        {
                            entry.BoxedValue = entry.DefaultValue;
                        }
                    }
                    catch
                    {
                        // A default saved by an older build may no longer parse — fall back rather
                        // than abandoning the rest of the reset.
                        try { entry.BoxedValue = entry.DefaultValue; }
                        catch { }
                    }
                }

                try { config.Save(); }
                catch { }
            }
        }

        private static Dictionary<string, Dictionary<string, string>> Read()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!Exists) return result;

            try
            {
                Dictionary<string, string> current = null;

                foreach (string rawLine in System.IO.File.ReadAllLines(Path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        string section = line.Substring(1, line.Length - 2).Trim();
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        result[section] = current;
                        continue;
                    }

                    if (current == null) continue;

                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    current[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
            }
            catch
            {
                // Unreadable defaults file — treat as absent.
            }

            return result;
        }

        private static string NameOf(ConfigFile config)
        {
            return System.IO.Path.GetFileNameWithoutExtension(config.ConfigFilePath);
        }
    }
}

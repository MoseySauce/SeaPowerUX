using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Shared, stateless helpers extracted from SeaPowerUX.TacticalPiP's Plugin.cs so every SeaPowerUX.*
    /// panel plugin gets the same "one failing patch doesn't take down the others" and
    /// "disable loudly instead of crashing later" behavior without re-deriving it. Each plugin
    /// still owns its own Harmony instance (so UnpatchSelf() on its own OnDestroy only affects
    /// its own patches) — these helpers just take that instance as a parameter.
    /// </summary>
    public static class PluginSupport
    {
        /// <summary>
        /// Applies each patch class in its own try/catch via CreateClassProcessor, exactly like
        /// SeaPowerUX.TacticalPiP's PatchEach, so one missing/changed target doesn't disable the others.
        /// </summary>
        public static void PatchEach(Harmony harmony, ManualLogSource logger, params Type[] patchClasses)
        {
            foreach (Type t in patchClasses)
            {
                try
                {
                    harmony.CreateClassProcessor(t).Patch();
                    logger.LogInfo($"Applied Harmony patch: {t.Name}.");
                }
                catch (Exception e)
                {
                    logger.LogWarning($"Harmony patch {t.Name} failed: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The game surface CORE depends on, checked once on a plugin's behalf.
    ///
    /// Kept here rather than copied into each plugin because these are Core's dependencies, not
    /// any one plugin's: window placement, HUD visibility, input suppression and the DIP scale are
    /// all provided by Core, so Core is what should notice when the game changes underneath them.
    /// </summary>
    public static class CoreApi
    {
        public static bool Validate(ManualLogSource logger, string pluginName, string pluginVersion)
        {
            return new GameApiValidator()
                .Type(typeof(SeapowerUI.Window), "SeapowerUI.Window (native window chrome)")
                .Property(typeof(SeapowerUI.Window), "X", "SeapowerUI.Window.X (position)")
                .Property(typeof(SeapowerUI.Window), "Y", "SeapowerUI.Window.Y (position)")
                .Method(typeof(SeapowerUI.Window), "OnInitialized", "SeapowerUI.Window.OnInitialized (pre-render placement hook)")
                .Property(typeof(SeapowerUI.Window), "ButtonsTemplate", "SeapowerUI.Window.ButtonsTemplate (docked-window chrome suppression)")
                .Property(typeof(SeapowerUI.Window), "HeaderThumb", "SeapowerUI.Window.HeaderThumb (docked-window drag suppression)")
                .Property(typeof(Noesis.FrameworkElement), "DataContext", "Noesis.FrameworkElement.DataContext (window identification)")
                .Property(typeof(SeapowerUI.WindowHost), "Windows", "SeapowerUI.WindowHost.Windows (hosted window set)")
                .Property(typeof(SeapowerUI.WindowHostManager), "CurrentHost", "SeapowerUI.WindowHostManager.CurrentHost")
                // Properties, not fields — WindowHostCommands declares them as
                // `public static CustomRoutedCommand X { get; }`. Probing them as fields reported
                // a false incompatibility and disabled the whole pack.
                .Property(typeof(SeapowerUI.WindowHostCommands), "CloseWindowCommand", "WindowHostCommands.CloseWindowCommand (routed open/close)")
                .Property(typeof(SeapowerUI.WindowHostCommands), "OpenWindowCommand", "WindowHostCommands.OpenWindowCommand (routed open)")
                .Method(typeof(SeapowerUI.ViewModels.MainGameViewModel), "GetHudVisible", "MainGameViewModel.GetHudVisible (HUD-aware hiding)")
                .Method(typeof(SeaPower.MouseControlState), "setMouseIsOverUIWindow", "MouseControlState.setMouseIsOverUIWindow (input suppression)")
                .Property(typeof(SeaPower.OptionsManager), "UIScale", "OptionsManager.UIScale (pixel/DIP conversion)")
                .Validate(logger, pluginName + " (Core)", pluginVersion);
        }
    }

    /// <summary>
    /// Reflection-based capability probe, mirroring SeaPowerUX.TacticalPiP's ValidateGameApi: confirms every
    /// game method/field/property a plugin depends on still exists before any patches are applied
    /// or any game members are touched. This is a targeted "did anything we depend on change"
    /// check, not a version-string compare, so routine game updates that don't touch our
    /// integration points still pass. Build one per plugin in Awake(), call Validate() once.
    /// </summary>
    public sealed class GameApiValidator
    {
        private enum MemberKind { Method, Property, Field }

        private readonly List<string> _missing = new List<string>();
        private readonly List<string> _misprobed = new List<string>();
        private int _checked;

        public GameApiValidator Method(Type type, string name, string description, Type[] args = null)
        {
            return Probe(type, name, description, MemberKind.Method, args);
        }

        public GameApiValidator Field(Type type, string name, string description)
        {
            return Probe(type, name, description, MemberKind.Field, null);
        }

        public GameApiValidator Property(Type type, string name, string description)
        {
            return Probe(type, name, description, MemberKind.Property, null);
        }

        public GameApiValidator Type(Type type, string description)
        {
            _checked++;
            if (type == null) _missing.Add(description);
            return this;
        }

        /// <summary>
        /// Looks for the member as the kind the caller expected, then — if that misses — as the
        /// other kinds before declaring it gone.
        ///
        /// This exists because the validator's own mistakes are indistinguishable from a real game
        /// change: probing `WindowHostCommands.CloseWindowCommand` as a field when it is declared
        /// `public static CustomRoutedCommand CloseWindowCommand { get; }` reported an
        /// incompatibility and disabled the entire pack, for a member that was present the whole
        /// time. A member that exists under a different kind is reported as OUR bug, loudly, and
        /// does not fail validation — the mod keeps running and the wrong probe gets fixed.
        /// </summary>
        private GameApiValidator Probe(Type type, string name, string description, MemberKind expected, Type[] args)
        {
            _checked++;

            if (type == null)
            {
                _missing.Add(description);
                return this;
            }

            if (Exists(type, name, expected, args)) return this;

            foreach (MemberKind kind in new[] { MemberKind.Method, MemberKind.Property, MemberKind.Field })
            {
                if (kind == expected || !Exists(type, name, kind, args)) continue;

                _misprobed.Add($"{description} — declared as a {kind.ToString().ToLowerInvariant()}, probed as a {expected.ToString().ToLowerInvariant()}");
                return this;
            }

            _missing.Add(description);
            return this;
        }

        private static bool Exists(Type type, string name, MemberKind kind, Type[] args)
        {
            try
            {
                switch (kind)
                {
                    case MemberKind.Method:
                        return (args != null ? AccessTools.Method(type, name, args) : AccessTools.Method(type, name)) != null;
                    case MemberKind.Property:
                        return AccessTools.Property(type, name) != null;
                    default:
                        return AccessTools.Field(type, name) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Logs and returns false with a loud banner if anything is genuinely missing; logs a
        /// short pass message and returns true otherwise. Callers should treat a false result by
        /// setting an "incompatible" flag and skipping patching entirely, rather than partially
        /// operating against a changed API.
        ///
        /// The probe count is derived rather than passed in — it used to be a hand-maintained
        /// argument and had already drifted out of step with the probes.
        /// </summary>
        public bool Validate(ManualLogSource logger, string pluginName, string pluginVersion)
        {
            foreach (string m in _misprobed)
            {
                logger.LogWarning($"{pluginName}: mod bug (not a game change) — {m}. Continuing.");
            }

            if (_missing.Count > 0)
            {
                var lines = new List<string>
                {
                    $"{pluginName} v{pluginVersion} is INCOMPATIBLE with this game build.",
                    "A game update changed or removed things this mod hooks. It is DISABLED.",
                    "Missing integration points:",
                };
                lines.AddRange(_missing.ConvertAll(m => "    - " + m));

                logger.LogError("======================================================================");
                foreach (string line in lines) logger.LogError(line);
                logger.LogError("======================================================================");

                StartupAlert.Report(pluginName, lines);
                return false;
            }

            logger.LogInfo($"{pluginName}: game API validation passed ({_checked} integration points OK).");
            return true;
        }
    }
}

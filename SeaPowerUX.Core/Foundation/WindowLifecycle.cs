using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// A pre-render notification for the game's own windows: raised from inside
    /// <c>SeapowerUI.Window.OnInitialized()</c>, before the window is first drawn.
    ///
    /// This is the seam that lets features which need that moment live in different plugins.
    /// Window position memory needs it (to place a window before it renders) and docking needs it
    /// (to hide a docked child before it flashes at 0,0), but neither should depend on the other
    /// being installed — so Core owns the hook and both subscribe.
    ///
    /// <c>OnInitialized</c> is a public plain managed method called from the window's own Loaded
    /// handler — the same event the game's AbsoluteInitialPosition uses to place the tactical map
    /// without a visible jump. <c>Window</c> derives from <c>HeaderedContentControl</c>, not from
    /// <c>Behavior&lt;T&gt;</c>, so patching it is clear of the rule that patching Behavior
    /// subclasses crashes Mono natively.
    /// </summary>
    public static class WindowLifecycle
    {
        private static bool _installed;
        private static bool _failed;
        private static Harmony _pending;
        private static ManualLogSource _pendingLogger;
        private static Runner _runner;

        /// <summary>
        /// Raised for each window as it initialises. Handlers run inside the game's own window
        /// setup, so they must not throw — each is individually guarded here regardless.
        /// </summary>
        public static event Action<SeapowerUI.Window> WindowInitialized;

        /// <summary>True once the hook is live; features can degrade gracefully if it isn't.</summary>
        public static bool IsInstalled => _installed;

        /// <summary>
        /// Idempotent, and safe to call from a plugin's Awake(). Any plugin may supply its Harmony
        /// instance; the first one that succeeds installs the patch and the rest are no-ops, so
        /// disabling one plugin can't silently remove a capability others rely on.
        ///
        /// The patch is NOT applied here. Resolving <c>SeapowerUI.Window</c> runs its static
        /// initialiser, which registers Noesis DependencyProperties — native calls that hard-crash
        /// the process if made before Noesis is up, leaving an empty BepInEx log. Callers
        /// legitimately want to ask for this during Awake, so the deferral lives here rather than
        /// being every caller's problem to remember.
        /// </summary>
        public static void Install(Harmony harmony, ManualLogSource logger)
        {
            if (_installed || _failed || harmony == null) return;

            _pending = harmony;
            _pendingLogger = logger;

            if (_runner != null) return;

            GameObject go = new GameObject("SeaPowerUX_WindowLifecycle");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
        }

        private static bool TryPatchNow(Harmony harmony, ManualLogSource logger)
        {
            if (_installed) return true;
            if (_failed || harmony == null) return false;

            try
            {
                var target = AccessTools.Method(typeof(SeapowerUI.Window), "OnInitialized");
                if (target == null)
                {
                    _failed = true;
                    logger?.LogWarning("WindowLifecycle: SeapowerUI.Window.OnInitialized() not found — pre-render window features are unavailable.");
                    return false;
                }

                var postfix = AccessTools.Method(typeof(WindowLifecycle), nameof(OnInitializedPostfix));
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                _installed = true;
                logger?.LogInfo("WindowLifecycle: patched SeapowerUI.Window.OnInitialized.");
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                logger?.LogWarning($"WindowLifecycle: could not patch SeapowerUI.Window.OnInitialized ({e.Message}).");
                return false;
            }
        }

        /// <summary>
        /// Waits for a live NoesisView before touching any Noesis-derived type. Checked via a
        /// Unity component lookup rather than by reading a Noesis static, because reading those
        /// statics is the hazard being avoided.
        /// </summary>
        private class Runner : MonoBehaviour
        {
            private void Update()
            {
                if (_installed || _failed)
                {
                    Destroy(gameObject);
                    return;
                }

                bool noesisReady;
                try { noesisReady = UnityEngine.Object.FindObjectOfType<NoesisView>() != null; }
                catch { noesisReady = false; }
                if (!noesisReady) return;

                TryPatchNow(_pending, _pendingLogger);

                _pending = null;
                _pendingLogger = null;
                Destroy(gameObject);
            }
        }

        private static void OnInitializedPostfix(SeapowerUI.Window __instance)
        {
            if (__instance == null) return;

            Action<SeapowerUI.Window> handlers = WindowInitialized;
            if (handlers == null) return;

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action<SeapowerUI.Window>)handler)(__instance); }
                catch { /* one subscriber must not break the game's window setup, or the others */ }
            }
        }
    }
}

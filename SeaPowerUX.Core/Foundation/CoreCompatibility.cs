using System;
using BepInEx.Logging;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Checks that the loaded Core matches the one a plugin was built against, so a partial
    /// update fails with an explanation instead of a runtime type error deep in some later call.
    /// </summary>
    public static class CoreCompatibility
    {
        /// <summary>
        /// Call FIRST in a plugin's Awake, from inside a try/catch. Returns false — and logs why —
        /// when the loaded Core is a different build.
        ///
        /// The caller's try/catch matters: if the installed Core is old enough to lack the members
        /// this reads, the failure surfaces when THIS method is JIT-compiled, which happens at the
        /// call site. Catching there turns "the plugin exploded on load" into a clear message.
        /// </summary>
        public static bool Check(string pluginName, string expectedCoreVersion, ManualLogSource logger)
        {
            string loaded = CoreInfo.Version;
            if (string.Equals(loaded, expectedCoreVersion, StringComparison.Ordinal)) return true;

            // Compatible within a major.minor line. Deliberately NOT an exact-match requirement:
            // an exact check would defeat an assembly binding redirect pointing several plugins at
            // one newer Core, which is a legitimate way to run a mixed install. Patch-level drift
            // is reported and allowed; a major or minor difference means the shared API moved and
            // is refused.
            if (SameFeatureLine(loaded, expectedCoreVersion))
            {
                logger?.LogWarning(
                    $"{pluginName} was built against SeaPowerUX.Core {expectedCoreVersion} but loaded {loaded}. " +
                    "Same feature line, so continuing — update the pack together if anything misbehaves.");
                return true;
            }

            string message =
                $"{pluginName} was built against SeaPowerUX.Core {expectedCoreVersion} but loaded {loaded}. " +
                "Those are different feature lines. Install every SeaPowerUX file from the same release.";

            logger?.LogError(message + " This plugin is disabled.");
            StartupAlert.Report(pluginName, new[] { message });
            return false;
        }

        private static bool SameFeatureLine(string a, string b)
        {
            try
            {
                var left = new Version(Normalize(a));
                var right = new Version(Normalize(b));
                return left.Major == right.Major && left.Minor == right.Minor;
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string version)
        {
            // Version demands at least major.minor.
            return version != null && version.Contains(".") ? version : version + ".0";
        }
    }
}

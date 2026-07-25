namespace SeaPowerUX.Core
{
    /// <summary>
    /// Identifies the Core build a plugin is actually running against.
    ///
    /// Deliberately a <c>static readonly</c> field and NOT a <c>const</c>: the compiler inlines
    /// const values into every calling assembly, so a plugin would carry the version it was BUILT
    /// against and could never observe the version it LOADED against — which is precisely the
    /// mismatch this exists to catch.
    ///
    /// Core ships in the same package as the plugins, so skew normally means a partial update:
    /// someone replaced one DLL and not the others. Without this check that surfaces as a
    /// TypeLoadException or MissingMethodException with nothing pointing at the cause.
    /// </summary>
    public static class CoreInfo
    {
        public static readonly string Version = "0.5.0";
    }
}

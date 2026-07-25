namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// The config sentinel that marks a window position as "not set by the player yet", so the
    /// plugin uses its coded default instead. <see cref="WindowGeometry"/> resolves the default; the
    /// plugin overwrites this with real pixels the first time the window is moved, and subsequent
    /// launches use the saved spot.
    /// </summary>
    public static class WindowDefaults
    {
        /// <summary>No saved position — use the coded default. Matches the pack's "-1 means unset"
        /// convention, and is never a legitimate top-left pixel.</summary>
        public const int Auto = -1;
    }
}

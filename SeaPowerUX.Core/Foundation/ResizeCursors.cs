using UnityEngine;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>Which resize cursor a hit zone shows.</summary>
    public enum ResizeCursor
    {
        None,
        Horizontal,  // ↔ left/right edges
        Vertical,    // ↕ bottom edge
        DiagonalNWSE, // ↘↖ top-left / bottom-right corner
        DiagonalNESW, // ↗↙ top-right / bottom-left corner
    }

    /// <summary>
    /// Double-headed resize cursors, generated at runtime.
    ///
    /// Unity exposes no system resize cursors — <c>Cursor.SetCursor</c> only takes a texture — so
    /// these are drawn procedurally rather than shipped as assets, which also keeps the plugin a
    /// single DLL with no resource loading.
    ///
    /// Each is one shape rotated: a horizontal double arrow sampled through a rotation, with a
    /// black outline so it stays readable against both the light HUD and the dark sea.
    /// </summary>
    public static class ResizeCursors
    {
        private const int Size = 32;
        private static readonly Vector2 Hotspot = new Vector2(Size / 2f, Size / 2f);

        private static Texture2D _horizontal;
        private static Texture2D _vertical;
        private static Texture2D _diagonalNWSE;
        private static Texture2D _diagonalNESW;
        private static ResizeCursor _current = ResizeCursor.None;

        public static void Set(ResizeCursor cursor)
        {
            if (_current == cursor) return;
            _current = cursor;

            if (cursor == ResizeCursor.None)
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                return;
            }

            Texture2D texture = Get(cursor);
            if (texture != null) Cursor.SetCursor(texture, Hotspot, CursorMode.Auto);
        }

        /// <summary>Restores the game's cursor. Safe to call when nothing was overridden.</summary>
        public static void Clear() => Set(ResizeCursor.None);

        private static Texture2D Get(ResizeCursor cursor)
        {
            switch (cursor)
            {
                case ResizeCursor.Horizontal:
                    return _horizontal ?? (_horizontal = Build(0f));
                case ResizeCursor.Vertical:
                    return _vertical ?? (_vertical = Build(90f));
                case ResizeCursor.DiagonalNWSE:
                    return _diagonalNWSE ?? (_diagonalNWSE = Build(-45f));
                case ResizeCursor.DiagonalNESW:
                    return _diagonalNESW ?? (_diagonalNESW = Build(45f));
                default:
                    return null;
            }
        }

        private static Texture2D Build(float degrees)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                // Never collected while the cursor might still reference it.
                hideFlags = HideFlags.HideAndDontSave,
            };

            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            float centre = (Size - 1) / 2f;

            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - centre;
                    float dy = y - centre;

                    // Rotate the sample point into the arrow's own space instead of rotating the
                    // drawing — one shape function then serves all four cursors.
                    float u = dx * cos + dy * sin;
                    float v = -dx * sin + dy * cos;

                    Color32 colour;
                    if (InArrow(u, v, 0f)) colour = new Color32(255, 255, 255, 255);
                    else if (InArrow(u, v, 1.5f)) colour = new Color32(0, 0, 0, 255); // outline
                    else colour = new Color32(0, 0, 0, 0);

                    pixels[y * Size + x] = colour;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// A horizontal double-headed arrow: a shaft with a triangular head at each end.
        /// <paramref name="expand"/> fattens the whole shape, which is how the outline is drawn.
        /// </summary>
        private static bool InArrow(float u, float v, float expand)
        {
            const float ShaftHalfHeight = 1.5f;
            const float ShaftHalfLength = 9f;
            const float HeadStart = 8f;
            const float HeadEnd = 14f;

            if (Mathf.Abs(v) <= ShaftHalfHeight + expand && Mathf.Abs(u) <= ShaftHalfLength + expand)
            {
                return true;
            }

            float absU = Mathf.Abs(u);
            if (absU >= HeadStart - expand && absU <= HeadEnd + expand)
            {
                // Triangle tapering to a point at HeadEnd.
                float halfHeight = (HeadEnd - absU) * 0.8f + expand;
                if (Mathf.Abs(v) <= halfHeight) return true;
            }

            return false;
        }
    }
}

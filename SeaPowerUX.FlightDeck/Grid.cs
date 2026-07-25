using UnityEngine;
using UnityEngine.UI;

namespace SeaPowerUX.FlightDeck
{
    /// <summary>One column of the flight deck list.</summary>
    internal struct GridColumn
    {
        public string Header;

        /// <summary>Fixed width in pixels. Ignored when <see cref="Flexible"/> is set.</summary>
        public float Width;

        /// <summary>Exactly one column should be flexible; it absorbs the leftover width.</summary>
        public bool Flexible;

        /// <summary>Minimum width the flexible column may shrink to.</summary>
        public float MinWidth;

        public TextAnchor Align;
    }

    /// <summary>
    /// Explicit column layout for the list, replacing nested HorizontalLayoutGroups.
    ///
    /// The layout groups were a persistent source of misalignment: a flexible child with no
    /// minimum silently collapsed to zero once the fixed columns claimed the row, and its Text
    /// then drew outside its own rect where the viewport mask clipped the leading characters —
    /// which reads exactly like a padding bug and isn't one. Header and rows were also two
    /// independent layout groups that could disagree about where a column starts.
    ///
    /// This positions cells directly from one shared column table, so the header and every row are
    /// laid out by identical arithmetic and cannot drift. Cells are anchored to the row's left edge
    /// with explicit offsets rather than being sized by a layout group, so nothing can be squeezed
    /// out of existence.
    /// </summary>
    internal static class Grid
    {
        public const float CellPadding = 6f;
        public const float ColumnGap = 8f;

        /// <summary>
        /// Positions a row's cells. <paramref name="cells"/> must be parallel to
        /// <paramref name="columns"/>.
        /// </summary>
        public static void Layout(RectTransform row, GridColumn[] columns, RectTransform[] cells, float rowWidth)
        {
            if (columns == null || cells == null) return;

            float available = rowWidth - (CellPadding * 2f) - (ColumnGap * (columns.Length - 1));

            float fixedTotal = 0f;
            for (int i = 0; i < columns.Length; i++)
            {
                if (!columns[i].Flexible) fixedTotal += columns[i].Width;
            }

            float flexible = Mathf.Max(0f, available - fixedTotal);

            float x = CellPadding;
            for (int i = 0; i < columns.Length && i < cells.Length; i++)
            {
                float width = columns[i].Flexible
                    ? Mathf.Max(columns[i].MinWidth, flexible)
                    : columns[i].Width;

                RectTransform cell = cells[i];
                if (cell != null)
                {
                    cell.anchorMin = new Vector2(0f, 0f);
                    cell.anchorMax = new Vector2(0f, 1f);
                    cell.pivot = new Vector2(0f, 0.5f);
                    cell.anchoredPosition = new Vector2(x, 0f);
                    cell.sizeDelta = new Vector2(width, 0f);
                }

                x += width + ColumnGap;
            }
        }
    }

    /// <summary>
    /// Re-runs the column layout whenever the row's width changes, so resizing the window reflows
    /// the grid. Unity raises OnRectTransformDimensionsChange for exactly this.
    /// </summary>
    internal class GridRow : MonoBehaviour
    {
        private GridColumn[] _columns;
        private RectTransform[] _cells;
        private RectTransform _rect;

        public void Initialize(GridColumn[] columns, RectTransform[] cells)
        {
            _columns = columns;
            _cells = cells;
            _rect = GetComponent<RectTransform>();
            Apply();
        }

        private void OnRectTransformDimensionsChange() => Apply();

        private void Apply()
        {
            if (_rect == null || _columns == null || _cells == null) return;
            Grid.Layout(_rect, _columns, _cells, _rect.rect.width);
        }
    }
}

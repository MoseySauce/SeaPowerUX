using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Shows an on-screen panel when a plugin disables itself, so a failure isn't only visible to
    /// someone who thinks to open <c>BepInEx/LogOutput.log</c>.
    ///
    /// Built from plain UGUI with its own canvas rather than through <see cref="UguiWindow"/> and
    /// the shared canvas ON PURPOSE. This is the error path: it has to work when other parts of
    /// the pack have already decided the game changed underneath them, so it deliberately depends
    /// on nothing but Unity. It reads no game state at all.
    ///
    /// Reports are collected and shown together — a change to a shared game member usually trips
    /// several plugins at once, and four stacked panels would be worse than one list.
    /// </summary>
    public static class StartupAlert
    {
        private const int SortingOrder = 32766; // above the pack's own canvas
        private const float ShowAfterSeconds = 2f;

        private static readonly List<string> Lines = new List<string>();
        private static Runner _runner;
        private static bool _shown;

        /// <summary>Queues a failure for display. Safe to call from a plugin's Awake().</summary>
        public static void Report(string pluginName, IEnumerable<string> details)
        {
            if (Lines.Count > 0) Lines.Add(string.Empty);
            Lines.Add(pluginName);

            if (details != null)
            {
                foreach (string line in details) Lines.Add(line);
            }

            EnsureRunner();
        }

        private static void EnsureRunner()
        {
            if (_runner != null || _shown) return;

            GameObject go = new GameObject("SeaPowerUX_StartupAlert");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
        }

        private static void Build()
        {
            _shown = true;

            GameObject canvasGo = new GameObject("SeaPowerUX_AlertCanvas");
            Object.DontDestroyOnLoad(canvasGo);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            GameObject panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(canvasGo.transform, false);
            RectTransform panel = panelGo.AddComponent<RectTransform>();
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(620f, 300f);

            Image bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.11f, 0.13f, 0.98f);

            Outline border = panelGo.AddComponent<Outline>();
            border.effectColor = new Color(0.72f, 0.25f, 0.25f, 1f);
            border.effectDistance = new Vector2(2f, -2f);

            CreateText(panel, "Title", "SeaPowerUX — a plugin has been disabled", 15,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -40f), new Vector2(-16f, -12f),
                TextAnchor.MiddleLeft, new Color(0.95f, 0.55f, 0.55f, 1f));

            var body = new StringBuilder();
            foreach (string line in Lines) body.AppendLine(line);
            body.AppendLine();
            body.Append("Full details are in BepInEx/LogOutput.log.");

            CreateText(panel, "Body", body.ToString(), 13,
                new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(16f, 44f), new Vector2(-16f, -44f),
                TextAnchor.UpperLeft, new Color(0.85f, 0.87f, 0.90f, 1f));

            CreateCloseButton(panel);
        }

        private static Text CreateText(RectTransform parent, string name, string content, int size,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
            TextAnchor alignment, Color color)
        {
            GameObject go = new GameObject(name);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            Text text = go.AddComponent<Text>();
            text.text = content;
            text.font = UguiWindow.GetUiFont();
            text.fontSize = size;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static void CreateCloseButton(RectTransform parent)
        {
            GameObject go = new GameObject("Dismiss");
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = new Vector2(96f, 26f);
            rect.anchoredPosition = new Vector2(-14f, 12f);

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.12f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(() =>
            {
                if (parent != null) Object.Destroy(parent.gameObject);
            });

            CreateText(rect, "Label", "Dismiss", 13,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                TextAnchor.MiddleCenter, new Color(0.85f, 0.87f, 0.90f, 1f));
        }

        /// <summary>
        /// Waits a moment before building. Plugins report during Awake, so showing immediately
        /// would produce a panel per plugin instead of one combined list, and would put UI on
        /// screen before the game has finished its own startup.
        /// </summary>
        private class Runner : MonoBehaviour
        {
            private float _elapsed;

            private void Update()
            {
                if (_shown)
                {
                    Destroy(gameObject);
                    return;
                }

                _elapsed += Time.unscaledDeltaTime;
                if (_elapsed < ShowAfterSeconds) return;

                Build();
                Destroy(gameObject);
            }
        }
    }
}

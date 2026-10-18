using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Performance
{
    /// <summary>Small FPS / frame-time readout in the top-left corner.</summary>
    internal sealed class Overlay : IDisposable
    {
        private readonly GameObject _go;
        private readonly PanelSettings _panel;
        private readonly UIDocument _doc;
        private Label _fps, _detail, _hint;
        private VisualElement _box;
        private float _nextText, _hintUntil;

        public Overlay()
        {
            _go = new GameObject("Výkon overlay") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_go);
            var source = Resources.Load<PanelSettings>("Announcements/AnnouncementPanelSettings");
            if (source == null)
                foreach (var p in Resources.FindObjectsOfTypeAll<PanelSettings>())
                    if (p != null && p.themeStyleSheet != null) { source = p; break; }
            if (source == null) throw new InvalidOperationException("no UI panel settings found in the game");
            _panel = UnityEngine.Object.Instantiate(source);
            _panel.sortingOrder = 95;
            _panel.targetTexture = null;
            _doc = _go.AddComponent<UIDocument>();
            _doc.panelSettings = _panel;
            Build();
        }

        private void Build()
        {
            var root = _doc.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.position = Position.Absolute;
            root.style.left = 0; root.style.top = 0; root.style.right = 0; root.style.bottom = 0;

            _box = new VisualElement { pickingMode = PickingMode.Ignore };
            _box.style.position = Position.Absolute;
            _box.style.left = 10;
            _box.style.top = 10;
            _box.style.paddingLeft = 8; _box.style.paddingRight = 8; _box.style.paddingTop = 4; _box.style.paddingBottom = 5;
            _box.style.backgroundColor = new Color(0.03f, 0.04f, 0.08f, 0.62f);
            _box.style.borderTopLeftRadius = 6; _box.style.borderTopRightRadius = 6; _box.style.borderBottomLeftRadius = 6; _box.style.borderBottomRightRadius = 6;
            root.Add(_box);
            _fps = Label(18, true);
            _box.Add(_fps);
            _detail = Label(11, false);
            _detail.style.color = new Color(1, 1, 1, 0.7f);
            _box.Add(_detail);

            _hint = Label(16, true);
            _hint.style.position = Position.Absolute;
            _hint.style.left = 0; _hint.style.right = 0; _hint.style.top = 60;
            _hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            _hint.style.color = new Color(1f, 0.9f, 0.4f);
            _hint.style.display = DisplayStyle.None;
            root.Add(_hint);
        }

        private static Label Label(int size, bool bold)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.fontSize = size;
            l.style.color = Color.white;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginLeft = 0; l.style.marginRight = 0; l.style.marginTop = 0; l.style.marginBottom = 0;
            l.style.paddingLeft = 0; l.style.paddingRight = 0; l.style.paddingTop = 0; l.style.paddingBottom = 0;
            return l;
        }

        public void Hint(string text)
        {
            _hint.text = text;
            _hint.style.display = DisplayStyle.Flex;
            _hintUntil = Time.unscaledTime + 2.5f;
        }

        public void Tick(bool showFps, string presetName)
        {
            if (_hint.style.display == DisplayStyle.Flex && Time.unscaledTime > _hintUntil) _hint.style.display = DisplayStyle.None;
            _box.style.display = showFps ? DisplayStyle.Flex : DisplayStyle.None;
            if (!showFps) return;
            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;
            double ms = FrameStats.AverageMs(30);
            int lastSecond = ms > 0 ? (int)Math.Min(FrameStats.Window, 1000.0 / ms) : 1;
            _fps.text = $"{(ms > 0 ? 1000.0 / ms : 0):F0} FPS   {ms:F1} ms";
            string batches = FrameStats.Batches >= 0 ? $" · {FrameStats.Batches} {Lang.T("dávek", "batches")}" : "";
            _detail.text = $"{Lang.T("nejhorší", "worst")} {FrameStats.WorstMs(lastSecond):F1} ms{batches} · {presetName}\n" +
                           $"{Lang.T("Windows a myš", "Windows & mouse")} {GapTimer.LastMs:F1} ms";
        }

        public void Dispose()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace SwyfModManager
{
    /// <summary>
    /// Small notifications in the bottom-right corner ("Načteno: …"), drawn with the IMGUI calls that
    /// survived Unity's code stripping (GUIStyle.Draw, GUI.Label with GUIContent, GUI.DrawTexture).
    /// </summary>
    internal static class Toasts
    {
        internal enum Kind { Ok, Info, Error }

        private const float Lifetime = 4f;
        private const float Fade = 0.35f;

        private static readonly List<(string Text, Kind Kind, float Start)> Items = new List<(string, Kind, float)>();
        private static readonly GUIContent Content = new GUIContent();
        private static GUIStyle _box, _text;
        private static Texture2D _dotOk, _dotInfo, _dotError, _boxTex;

        internal static void Show(string text, Kind kind)
        {
            Items.Add((text, kind, Time.realtimeSinceStartup));
            if (Items.Count > 5) Items.RemoveAt(0);
            if (kind == Kind.Error) ModManagerPlugin.Log.LogWarning(text);
            else ModManagerPlugin.Log.LogInfo(text);
        }

        internal static void Draw()
        {
            if (Items.Count == 0) return;
            var now = Time.realtimeSinceStartup;
            Items.RemoveAll(i => now - i.Start > Lifetime + (i.Kind == Kind.Error ? 4f : 0f));
            if (Items.Count == 0 || Event.current.type != EventType.Repaint) return;
            EnsureStyles();

            var scale = Mathf.Clamp(Screen.height / 1080f, 1f, 2f);
            var old = GUI.matrix;
            var oldColor = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var sw = Screen.width / scale;
            var y = Screen.height / scale - 24f;

            for (var i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];
                var age = now - item.Start;
                var life = Lifetime + (item.Kind == Kind.Error ? 4f : 0f);
                var alpha = Mathf.Clamp01(Mathf.Min(age / Fade, (life - age) / Fade));
                var slide = (1f - Mathf.Clamp01(age / Fade)) * 16f;

                Content.text = item.Text;
                var width = Mathf.Min(440f, _text.CalcHeight(Content, 1000f) > 20f ? 440f : 36f + 9f * item.Text.Length + 20f);
                var height = Mathf.Max(40f, _text.CalcHeight(Content, width - 52f) + 20f);
                y -= height;
                var r = new Rect(sw - width - 24f + slide, y, width, height);
                y -= 8f;

                GUI.color = new Color(1f, 1f, 1f, alpha);
                _box.Draw(r, false, false, false, false);
                var dot = item.Kind == Kind.Ok ? _dotOk : item.Kind == Kind.Error ? _dotError : _dotInfo;
                GUI.DrawTexture(new Rect(r.x + 16f, r.center.y - 5f, 10f, 10f), dot);
                GUI.Label(new Rect(r.x + 36f, r.y, r.width - 48f, r.height), Content, _text);
            }

            GUI.color = oldColor;
            GUI.matrix = old;
        }

        private static void EnsureStyles()
        {
            if (_boxTex != null) return;
            _boxTex = Rounded(10, new Color(0.067f, 0.075f, 0.094f, 0.96f), new Color(1f, 1f, 1f, 0.08f));
            _box = new GUIStyle { border = new RectOffset(10, 10, 10, 10) };
            _box.normal.background = _boxTex;
            _text = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            _text.normal.textColor = new Color(0.91f, 0.92f, 0.95f);
            _dotOk = Rounded(4, new Color(0.43f, 0.91f, 0.63f), null);
            _dotInfo = Rounded(4, new Color(1f, 0.76f, 0.24f), null);
            _dotError = Rounded(4, new Color(1f, 0.42f, 0.42f), null);
        }

        private static Texture2D Rounded(int radius, Color fill, Color? stroke)
        {
            var size = radius * 2 + 2;
            var half = size / 2f;
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                var qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                var ox = Mathf.Max(qx, 0f);
                var oy = Mathf.Max(qy, 0f);
                var d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                var c = stroke.HasValue ? Color.Lerp(stroke.Value, fill, Mathf.Clamp01(-0.5f - d)) : fill;
                c.a *= Mathf.Clamp01(0.5f - d);
                px[y * size + x] = c;
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}

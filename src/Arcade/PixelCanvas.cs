using System;
using UnityEngine;

namespace Arcade
{
    /// <summary>A small pixel buffer the games draw into, uploaded to a point-filtered texture once per frame.</summary>
    internal sealed class PixelCanvas : IDisposable
    {
        public readonly int Width, Height;
        private readonly Color32[] _px;
        private Texture2D _texture;

        public PixelCanvas(int width, int height)
        {
            Width = width;
            Height = height;
            _px = new Color32[width * height];
        }

        /// <summary>Created on first use, so the games can also be drawn outside Unity (tools/ArcadeTest).</summary>
        public Texture2D Texture => _texture ?? (_texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        });

        /// <summary>Raw pixels, bottom row first (Unity's texture order).</summary>
        public Color32[] Pixels => _px;

        public void Clear(Color32 c)
        {
            for (int i = 0; i < _px.Length; i++) _px[i] = c;
        }

        /// <summary>Top-left origin, like the screen.</summary>
        public void Px(int x, int y, Color32 c)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            int i = (Height - 1 - y) * Width + x;
            if (c.a == 255) { _px[i] = c; return; }
            if (c.a == 0) return;
            var d = _px[i];
            int a = c.a, ia = 255 - a;
            _px[i] = new Color32((byte)((c.r * a + d.r * ia) / 255), (byte)((c.g * a + d.g * ia) / 255), (byte)((c.b * a + d.b * ia) / 255), 255);
        }

        public void Rect(int x, int y, int w, int h, Color32 c)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
            for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++) Px(xx, yy, c);
        }

        public void Frame(int x, int y, int w, int h, Color32 c)
        {
            Rect(x, y, w, 1, c);
            Rect(x, y + h - 1, w, 1, c);
            Rect(x, y, 1, h, c);
            Rect(x + w - 1, y, 1, h, c);
        }

        public void Circle(float cx, float cy, float r, Color32 c)
        {
            int x0 = (int)Math.Floor(cx - r), x1 = (int)Math.Ceiling(cx + r);
            int y0 = (int)Math.Floor(cy - r), y1 = (int)Math.Ceiling(cy + r);
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy <= r2) Px(x, y, c);
                }
        }

        /// <summary>A filled block with a lighter top-left edge and darker bottom-right edge (Tetris, bricks).</summary>
        public void Block(int x, int y, int w, int h, Color32 c)
        {
            Rect(x, y, w, h, c);
            Rect(x, y, w, 1, Shade(c, 1.35f));
            Rect(x, y, 1, h, Shade(c, 1.2f));
            Rect(x, y + h - 1, w, 1, Shade(c, 0.6f));
            Rect(x + w - 1, y, 1, h, Shade(c, 0.7f));
        }

        public static Color32 Shade(Color32 c, float f) =>
            new Color32((byte)Math.Min(255, c.r * f), (byte)Math.Min(255, c.g * f), (byte)Math.Min(255, c.b * f), c.a);

        public static Color32 Hex(uint rgb, byte a = 255) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);

        public void Upload()
        {
            Texture.SetPixels32(_px);
            Texture.Apply(false);
        }

        public void Dispose()
        {
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
        }
    }
}

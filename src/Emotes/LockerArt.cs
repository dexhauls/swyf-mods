using System;
using System.Collections.Generic;
using UnityEngine;

namespace Emotes
{
    internal enum Rarity { Common, Uncommon, Rare, Epic, Legendary, Icon }

    /// <summary>Generated textures for the locker: rarity backgrounds and a little dancer pictogram per emote.</summary>
    internal static class LockerArt
    {
        public static string Name(Rarity r)
        {
            switch (r)
            {
                case Rarity.Uncommon: return Lang.T("NEOBVYKLÉ", "UNCOMMON");
                case Rarity.Rare: return Lang.T("VZÁCNÉ", "RARE");
                case Rarity.Epic: return Lang.T("EPICKÉ", "EPIC");
                case Rarity.Legendary: return Lang.T("LEGENDÁRNÍ", "LEGENDARY");
                case Rarity.Icon: return "ICON SERIES";
                default: return Lang.T("BĚŽNÉ", "COMMON");
            }
        }

        public static Color Accent(Rarity r)
        {
            switch (r)
            {
                case Rarity.Uncommon: return Hex(0x7ee03a);
                case Rarity.Rare: return Hex(0x3fc8ff);
                case Rarity.Epic: return Hex(0xd26bff);
                case Rarity.Legendary: return Hex(0xffa64a);
                case Rarity.Icon: return Hex(0x4fe3e6);
                default: return Hex(0xb8bcc8);
            }
        }

        private static (Color top, Color bottom) Gradient(Rarity r)
        {
            switch (r)
            {
                case Rarity.Uncommon: return (Hex(0x6dbd27), Hex(0x1f5410));
                case Rarity.Rare: return (Hex(0x34b9f5), Hex(0x123e8a));
                case Rarity.Epic: return (Hex(0xbe5cf5), Hex(0x4a1885));
                case Rarity.Legendary: return (Hex(0xf2a24b), Hex(0x8a3412));
                case Rarity.Icon: return (Hex(0x3fd2d8), Hex(0x0b4f63));
                default: return (Hex(0x9a9ea8), Hex(0x3e424c));
            }
        }

        public static Rarity RarityOf(EmoteEntry e)
        {
            switch (e.Kind)
            {
                case EmoteKind.Dance:
                    var d = DanceLibrary.Find(e.Clip);
                    return DanceMusic.Has(d) ? Rarity.Icon : Rarity.Legendary;
                case EmoteKind.Catalog: return EmoteCatalog.IsDancePageClip(e.Clip) ? Rarity.Epic : Rarity.Common;
                case EmoteKind.Extra: return Rarity.Rare;
                default: return Rarity.Uncommon;
            }
        }

        private static readonly Dictionary<Rarity, Texture2D> Backgrounds = new Dictionary<Rarity, Texture2D>();
        private static readonly Dictionary<string, Texture2D> Icons = new Dictionary<string, Texture2D>();

        /// <summary>Fortnite-style tile background: vertical gradient with a soft glow in the upper middle.</summary>
        public static Texture2D Background(Rarity r)
        {
            if (Backgrounds.TryGetValue(r, out var tex) && tex != null) return tex;
            const int W = 64, H = 80;
            var (top, bottom) = Gradient(r);
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y / (float)(H - 1);                       // 0 = bottom row in texture space
                    var c = Color.Lerp(bottom, top, Mathf.Pow(v, 0.9f));
                    float dx = (x - W * 0.5f) / W, dy = (y - H * 0.62f) / H;
                    float glow = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx * 2.2f + dy * dy * 3f)) * 0.35f;
                    // Faint diagonal stripes like the item shop backgrounds.
                    float stripe = ((x + y) / 6 % 2 == 0) ? 0.03f : 0f;
                    c = Color.Lerp(c, Color.white, glow + stripe);
                    px[y * W + x] = c;
                }
            tex = MakeTexture(W, H, px, FilterMode.Bilinear);
            Backgrounds[r] = tex;
            return tex;
        }

        /// <summary>A dancer pictogram whose pose comes from the emote's name, so every tile looks different.</summary>
        public static Texture2D Icon(EmoteEntry e)
        {
            string id = EmoteCatalog.Id(e);
            if (Icons.TryGetValue(id, out var tex) && tex != null) return tex;
            const int S = 96;
            var px = new Color32[S * S];
            var rnd = new System.Random(StableHash(id));
            bool dance = e.Kind == EmoteKind.Dance || (e.Kind == EmoteKind.Catalog && EmoteCatalog.IsDancePageClip(e.Clip));
            float spread = dance ? 1f : 0.55f;

            // Pose (angles in degrees, 0 = straight down, positive = toward the figure's right/up).
            float lean = R(rnd, -14, 14) * spread;
            float armL = R(rnd, 20, 170) * spread + (dance ? 0 : 10), armR = -R(rnd, 20, 170) * spread;
            float elbowL = R(rnd, -90, 90) * spread, elbowR = R(rnd, -90, 90) * spread;
            float legL = R(rnd, 5, 40) * spread, legR = -R(rnd, 5, 40) * spread;
            float kneeL = R(rnd, -50, 10) * spread, kneeR = R(rnd, -10, 50) * spread;

            var hip = new Vector2(S * 0.5f, S * 0.40f);
            var neck = hip + Dir(180 + lean) * S * 0.24f;
            var head = neck + Dir(180 + lean) * S * 0.10f;

            var shadow = new Color32(0, 0, 0, 70);
            var white = new Color32(255, 255, 255, 255);
            for (int pass = 0; pass < 2; pass++)
            {
                var col = pass == 0 ? shadow : white;
                var off = pass == 0 ? new Vector2(2, -2) : Vector2.zero;
                float w = pass == 0 ? 5.5f : 4.5f;
                Limb(px, S, neck + off, 180 + lean + armL, elbowL, S * 0.15f, w, col);
                Limb(px, S, neck + off, 180 + lean + armR, elbowR, S * 0.15f, w, col);
                Limb(px, S, hip + off, legL, kneeL, S * 0.19f, w + 0.5f, col);
                Limb(px, S, hip + off, legR, kneeR, S * 0.19f, w + 0.5f, col);
                Line(px, S, hip + off, neck + off, w + 1.5f, col);
                Disc(px, S, head + off + Dir(180 + lean) * 3, S * 0.085f, col);
            }
            tex = MakeTexture(S, S, px, FilterMode.Bilinear);
            Icons[id] = tex;
            return tex;
        }

        private static void Limb(Color32[] px, int s, Vector2 from, float angle, float bend, float len, float w, Color32 c)
        {
            var mid = from + Dir(angle) * len;
            var end = mid + Dir(angle + bend) * len;
            Line(px, s, from, mid, w, c);
            Line(px, s, mid, end, w, c);
        }

        // Angle 0 = down; texture y grows upward.
        private static Vector2 Dir(float deg)
        {
            float a = deg * 3.14159265f / 180f;
            return new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
        }

        private static void Line(Color32[] px, int s, Vector2 a, Vector2 b, float w, Color32 c)
        {
            float r = w * 0.5f;
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) - r), x1 = Mathf.CeilToInt(Mathf.Max(a.x, b.x) + r);
            int y0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) - r), y1 = Mathf.CeilToInt(Mathf.Max(a.y, b.y) + r);
            var ab = b - a;
            float len2 = Mathf.Max(ab.sqrMagnitude, 0.0001f);
            for (int y = Math.Max(0, y0); y <= Math.Min(s - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(s - 1, x1); x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                    float d = (p - (a + ab * t)).magnitude;
                    Blend(px, s, x, y, c, Mathf.Clamp01(r + 0.5f - d));
                }
        }

        private static void Disc(Color32[] px, int s, Vector2 c0, float r, Color32 c)
        {
            for (int y = Mathf.FloorToInt(c0.y - r - 1); y <= Mathf.CeilToInt(c0.y + r + 1); y++)
                for (int x = Mathf.FloorToInt(c0.x - r - 1); x <= Mathf.CeilToInt(c0.x + r + 1); x++)
                {
                    if (x < 0 || y < 0 || x >= s || y >= s) continue;
                    float d = (new Vector2(x + 0.5f, y + 0.5f) - c0).magnitude;
                    Blend(px, s, x, y, c, Mathf.Clamp01(r + 0.5f - d));
                }
        }

        private static void Blend(Color32[] px, int s, int x, int y, Color32 c, float a)
        {
            if (a <= 0f) return;
            int i = y * s + x;
            var d = px[i];
            float sa = a * c.a / 255f, da = d.a / 255f;
            float oa = sa + da * (1 - sa);
            if (oa <= 0f) return;
            byte Mix(byte sc, byte dc) => (byte)((sc * sa + dc * da * (1 - sa)) / oa);
            px[i] = new Color32(Mix(c.r, d.r), Mix(c.g, d.g), Mix(c.b, d.b), (byte)(oa * 255));
        }

        private static Texture2D MakeTexture(int w, int h, Color32[] px, FilterMode filter)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        private static float R(System.Random r, float lo, float hi) => lo + (float)r.NextDouble() * (hi - lo);

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return h;
            }
        }

        private static Color Hex(uint rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    }
}

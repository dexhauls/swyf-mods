using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DevMenuUnlock
{
    /// <summary>
    /// Icons pre-rendered from Segoe Fluent Icons into the embedded Icons.bin (see tools/GenerateIcons.ps1).
    /// The game can't load OS fonts at runtime, so each glyph is a white alpha texture tinted with GUI.color.
    /// </summary>
    internal static class IconAtlas
    {
        private static Dictionary<char, Texture2D> _icons;

        internal static bool Loaded => _icons != null && _icons.Count > 0;

        internal static void Load()
        {
            if (_icons != null) return;
            _icons = new Dictionary<char, Texture2D>();
            using (var stream = typeof(IconAtlas).Assembly.GetManifestResourceStream("DevMenuUnlock.Icons.bin"))
            {
                if (stream == null)
                {
                    Plugin.Log.LogWarning("Icons.bin is missing from the mod, the menu will have no icons.");
                    return;
                }
                var reader = new BinaryReader(stream);
                var count = reader.ReadInt32();
                var size = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var code = (char)reader.ReadUInt16();
                    var alpha = reader.ReadBytes(size * size);
                    var pixels = new Color32[size * size];
                    for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                        pixels[(size - 1 - y) * size + x] = new Color32(255, 255, 255, alpha[y * size + x]); // flip: Unity is bottom-up
                    var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    tex.SetPixels32(pixels);
                    tex.Apply();
                    _icons[code] = tex;
                }
            }
        }

        /// <summary>Draws the icon centred in <paramref name="area"/> at <paramref name="size"/> px, tinted.</summary>
        internal static void Draw(Rect area, string icon, Color color, float size = 18f)
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(icon) || !Loaded) return;
            if (!_icons.TryGetValue(icon[0], out var tex)) return;
            var r = new Rect(area.center.x - size / 2f, area.center.y - size / 2f, size, size);
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, tex);
            GUI.color = old;
        }
    }
}

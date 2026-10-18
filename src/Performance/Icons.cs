using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// Segoe Fluent Icons glyphs pre-rendered into the embedded Icons.bin (tools/GenerateIcons.ps1 scans
    /// SettingsMenu.cs). The game can't load OS fonts, so each glyph is a white alpha texture, tinted in the UI.
    /// </summary>
    internal static class Icons
    {
        private static Dictionary<char, Texture2D> _icons;

        public static Texture2D Get(char code)
        {
            Load();
            return _icons.TryGetValue(code, out var t) ? t : null;
        }

        private static void Load()
        {
            if (_icons != null) return;
            _icons = new Dictionary<char, Texture2D>();
            using (var stream = typeof(Icons).Assembly.GetManifestResourceStream("Performance.Icons.bin"))
            {
                if (stream == null) { Plugin.Log.LogWarning("Icons.bin is missing, the menu has no icons."); return; }
                var reader = new BinaryReader(stream);
                int count = reader.ReadInt32(), size = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var code = (char)reader.ReadUInt16();
                    var alpha = reader.ReadBytes(size * size);
                    var pixels = new Color32[size * size];
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                            pixels[(size - 1 - y) * size + x] = new Color32(255, 255, 255, alpha[y * size + x]); // Unity is bottom-up
                    var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    tex.SetPixels32(pixels);
                    tex.Apply();
                    _icons[code] = tex;
                }
            }
        }

        public static void Unload()
        {
            if (_icons == null) return;
            foreach (var t in _icons.Values) if (t != null) Object.Destroy(t);
            _icons = null;
        }
    }
}

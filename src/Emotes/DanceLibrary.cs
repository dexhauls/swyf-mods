using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Emotes
{
    /// <summary>One Mixamo dance converted by tools/mixamo2dance.py: per-bone local rotations for every frame.</summary>
    internal sealed class Dance
    {
        public string Id, Label;
        public float Fps, LegLength;
        public int Frames;
        public bool Loop;
        public int LoopStart;            // frames before this are the intro, played once (file version 2)
        public string[] Bones;
        public Quaternion[][] Rotations; // [bone][frame]
        public Vector3[] HipsOffset;     // per frame, metres, relative to frame 0

        public float Length => Frames / Fps;
    }

    /// <summary>Loads Mods/Emotes/dances.bin (made by tools/mixamo2dance.py from Mixamo FBX files).</summary>
    internal static class DanceLibrary
    {
        public static readonly List<Dance> All = new List<Dance>();
        private static readonly Dictionary<string, Dance> ById = new Dictionary<string, Dance>();

        public const string IdPrefix = "swyf-dance:";

        public static Dance Find(string id) => id != null && ById.TryGetValue(id, out var d) ? d : null;

        /// <summary>Every *.bin dance file in the folder (dances.bin ships with the mod; others are your own extras).</summary>
        public static void LoadFolder(string folder)
        {
            All.Clear();
            ById.Clear();
            if (!Directory.Exists(folder)) return;
            var files = Directory.GetFiles(folder, "*.bin");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                try { Load(f); }
                catch (Exception e) { Plugin.Log.LogWarning($"Skipping dance file {Path.GetFileName(f)}: {e.Message}"); }
            }
            All.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        }

        private static void Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "SWYD") throw new InvalidDataException("not a dance file");
                uint version = r.ReadUInt32();
                if (version != 1 && version != 2) throw new InvalidDataException("unsupported dance file version " + version);
                uint count = r.ReadUInt32();
                for (int d = 0; d < count; d++)
                {
                    var dance = new Dance { Id = Str(r), Label = Str(r), Fps = r.ReadSingle(), Frames = (int)r.ReadUInt32(), LegLength = r.ReadSingle() };
                    dance.Loop = (r.ReadUInt32() & 1) != 0;
                    if (version >= 2) dance.LoopStart = Mathf.Clamp((int)r.ReadUInt32(), 0, Math.Max(0, dance.Frames - 2));
                    int bones = (int)r.ReadUInt32();
                    dance.Bones = new string[bones];
                    dance.Rotations = new Quaternion[bones][];
                    for (int b = 0; b < bones; b++)
                    {
                        dance.Bones[b] = Str(r);
                        var q = new Quaternion[dance.Frames];
                        for (int f = 0; f < dance.Frames; f++) q[f] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        dance.Rotations[b] = q;
                    }
                    dance.HipsOffset = new Vector3[dance.Frames];
                    for (int f = 0; f < dance.Frames; f++) dance.HipsOffset[f] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    // A later file replaces the same dance (same id, or same name written differently,
                    // e.g. "GangnamStyle" from an older pack and "Gangnam Style" from a newer one).
                    string key = DanceMusic.Key(dance.Label);
                    foreach (var old in All.FindAll(x => x.Id == dance.Id || DanceMusic.Key(x.Label) == key)) ById.Remove(old.Id);
                    All.RemoveAll(x => x.Id == dance.Id || DanceMusic.Key(x.Label) == key);
                    All.Add(dance);
                    ById[dance.Id] = dance;
                }
            }
        }

        private static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
    }
}

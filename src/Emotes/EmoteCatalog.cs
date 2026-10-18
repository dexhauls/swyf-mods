using System.Collections.Generic;
using UnityEngine;

namespace Emotes
{
    internal enum EmoteKind
    {
        Catalog,    // in the game's emote list: the host plays it for everyone, mod or not
        Extra,      // an animation the game ships but never offers as an emote; needs the mod on the host
        Dance,      // a Mixamo dance from dances.bin, played by DanceDriver; needs the mod on the host
        ButtScratch,
        Fart,
        HeadWobble,
    }

    internal sealed class EmoteEntry
    {
        private readonly string _cs, _en;
        public readonly string Clip; // animation clip name (Catalog / Extra)
        public readonly EmoteKind Kind;

        /// <summary>Name in the game's language.</summary>
        public string Label => Lang.T(_cs, _en);

        public EmoteEntry(string cs, string en, string clip, EmoteKind kind)
        {
            _cs = cs;
            _en = en ?? cs;
            Clip = clip;
            Kind = kind;
        }

        public EmoteEntry(string label, string clip, EmoteKind kind) : this(label, label, clip, kind) { }
    }

    internal sealed class EmotePage
    {
        private readonly string _cs, _en;
        public readonly EmoteEntry[] Entries;

        public string Title => Lang.T(_cs, _en);

        public EmotePage(string cs, string en, params EmoteEntry[] entries)
        {
            _cs = cs;
            _en = en;
            Entries = entries;
        }
    }

    internal static class EmoteCatalog
    {
        private static EmoteEntry C(string cs, string en, string clip) => new EmoteEntry(cs, en, clip, EmoteKind.Catalog);
        private static EmoteEntry X(string cs, string en, string clip) => new EmoteEntry(cs, en, clip, EmoteKind.Extra);

        /// <summary>The wheel's pages: the player's own picks (Employee setup → Emoty), or by default the built-in
        /// pages plus one page per eight dances.</summary>
        public static readonly List<EmotePage> Pages = new List<EmotePage>();

        /// <summary>Every emote there is, for the settings list: built-in ones first, then dances by name.</summary>
        public static readonly List<EmoteEntry> All = new List<EmoteEntry>();

        /// <summary>Stable id used to remember a pick in the config.</summary>
        public static string Id(EmoteEntry e) => e.Kind + ":" + (e.Clip ?? "");

        public static EmoteEntry FindById(string id)
        {
            foreach (var e in All) if (Id(e) == id) return e;
            return null;
        }

        /// <summary>Rebuilds the entry list and the wheel pages (after loading dances or changing the picks).</summary>
        public static void Rebuild()
        {
            All.Clear();
            foreach (var p in BuiltIn) All.AddRange(p.Entries);
            foreach (var d in DanceLibrary.All) All.Add(new EmoteEntry(d.Label, d.Id, EmoteKind.Dance));

            Pages.Clear();
            int maxPages = Plugin.WheelPages;
            var slots = Plugin.WheelEmotes;
            if (slots.Exists(id => FindById(id) != null))
            {
                // The player's locker: 8 slots per page, empty slots are skipped on the wheel.
                int pages = maxPages > 0 ? maxPages : System.Math.Max(1, (slots.Count + 7) / 8);
                for (int p = 0; p < pages; p++)
                {
                    var entries = new List<EmoteEntry>();
                    for (int i = p * 8; i < p * 8 + 8 && i < slots.Count; i++)
                    {
                        var e = FindById(slots[i]);
                        if (e != null) entries.Add(e); // availability is checked when the wheel shows a page
                    }
                    if (entries.Count > 0) Pages.Add(new EmotePage(pages > 1 ? $"Moje emoty {p + 1}" : "Moje emoty", pages > 1 ? $"My emotes {p + 1}" : "My emotes", entries.ToArray()));
                }
                if (Pages.Count > 0) return;
            }

            Pages.AddRange(BuiltIn);
            var all = DanceLibrary.All;
            for (int i = 0; i < all.Count; i += 8)
            {
                var entries = new List<EmoteEntry>();
                for (int j = i; j < all.Count && j < i + 8; j++) entries.Add(new EmoteEntry(all[j].Label, all[j].Id, EmoteKind.Dance));
                // Dances are sorted by name; the page title shows their first letters ("Tanečky A–C").
                char from = char.ToUpperInvariant(entries[0].Label[0]), to = char.ToUpperInvariant(entries[entries.Count - 1].Label[0]);
                Pages.Add(new EmotePage(from == to ? $"Tanečky {from}" : $"Tanečky {from}–{to}", from == to ? $"Dances {from}" : $"Dances {from}–{to}", entries.ToArray()));
            }
            if (maxPages > 0 && Pages.Count > maxPages) Pages.RemoveRange(maxPages, Pages.Count - maxPages);
        }

        /// <summary>Short tag shown next to an emote in the locker.</summary>
        public static string KindLabel(EmoteEntry e)
        {
            switch (e.Kind)
            {
                case EmoteKind.Dance: return Lang.T("tanec", "dance");
                case EmoteKind.Extra: return Lang.T("nové", "new");
                case EmoteKind.Catalog: return IsDancePageClip(e.Clip) ? Lang.T("tanec ze hry", "game dance") : Lang.T("ze hry", "game emote");
                default: return Lang.T("blbost", "silly");
            }
        }

        /// <summary>
        /// A dance needs some real emote clip running underneath (the game's emote system only plays clips); the
        /// dance overwrites the pose anyway. "Singing" is the longest emote, so it sets how long a dance lasts.
        /// </summary>
        public static AnimationClip DancePlaceholder() => FindCatalog("Singing") ?? FindCatalog("Advanced Shuffle");

        private static readonly EmotePage[] BuiltIn =
        {
            new EmotePage("Tanečky", "Dances",
                C("Shuffle", "Shuffle", "Advanced Shuffle"),
                C("Kuřecí tanec", "Chicken Dance", "Chicken Dance"),
                C("Hip hop", "Hip Hop", "Hip Hop Dancing"),
                C("Headspin", "Headspin", "Headspin Start"),
                C("Roblox", "Roblox", "Roblox Sweep"),
                C("Piruety", "Spins", "Northern Soul Spin Combo"),
                C("Swing", "Swing", "Swing Dancing"),
                X("Šoupák", "Shuffle Step", "Shuffling 1")),
            new EmotePage("Gesta", "Gestures",
                X("Mávání", "Wave", "Standing Greeting"),
                C("Ukazování", "Point", "Pointing Gesture"),
                X("Salutování", "Salute", "Salute"),
                C("Vítězství", "Victory", "Victory"),
                X("Svaly", "Flex", "Mutant Flexing Muscles"),
                X("Modlitba", "Pray", "Praying"),
                C("Rozcvička", "Warm-up", "Warming Up"),
                C("Zpěv", "Sing", "Singing")),
            new EmotePage("Blbosti", "Silly",
                new EmoteEntry("Podrbat se", "Butt Scratch", null, EmoteKind.ButtScratch),
                new EmoteEntry("Prd", "Fart", null, EmoteKind.Fart),
                C("Kadění", "Squat", "Squat And Shit"),
                C("Salto", "Backflip", "Backflip"),
                X("Strach", "Scared", "Terrified"),
                X("Porážka", "Defeated", "Defeated"),
                X("T-pose", "T-Pose", "T-Pose"),
                new EmoteEntry("Kývání hlavou", "Head Wobble", null, EmoteKind.HeadWobble)),
        };

        private static readonly Dictionary<string, AnimationClip> Extras = new Dictionary<string, AnimationClip>();
        private static readonly HashSet<string> ExtraNames = new HashSet<string>();

        static EmoteCatalog()
        {
            Pages.AddRange(BuiltIn);
            foreach (var p in BuiltIn)
                foreach (var e in p.Entries)
                    if (e.Kind == EmoteKind.Extra) ExtraNames.Add(e.Clip);
        }

        /// <summary>An extra (non-catalog) clip by name, looked up among the animations the game has loaded.</summary>
        public static AnimationClip FindExtra(string name)
        {
            if (string.IsNullOrEmpty(name) || !ExtraNames.Contains(name)) return null;
            if (Extras.TryGetValue(name, out var clip) && clip != null) return clip;
            foreach (var c in Resources.FindObjectsOfTypeAll<AnimationClip>())
                if (c != null && ExtraNames.Contains(c.name) && !c.legacy) Extras[c.name] = c;
            return Extras.TryGetValue(name, out clip) ? clip : null;
        }

        /// <summary>A clip from the game's own emote list (locked ones included).</summary>
        public static AnimationClip FindCatalog(string name)
        {
            var s = SkinManager.Instance;
            if (s == null) return null;
            for (int i = 0; i < s.GetEmoteCount(); i++)
            {
                var c = s.GetRawEmoteClip(i);
                if (c != null && c.name == name) return c;
            }
            return null;
        }

        /// <summary>True for clips on the built-in "Tanečky" page (the game's dances).</summary>
        public static bool IsDancePageClip(string clip)
        {
            foreach (var e in BuiltIn[0].Entries)
                if (e.Clip == clip) return true;
            return false;
        }

        public static bool IsAvailable(EmoteEntry e)
        {
            switch (e.Kind)
            {
                case EmoteKind.Catalog: return FindCatalog(e.Clip) != null;
                case EmoteKind.Extra: return FindExtra(e.Clip) != null;
                case EmoteKind.Dance: return DanceLibrary.Find(e.Clip) != null && DancePlaceholder() != null;
                default: return true;
            }
        }
    }
}

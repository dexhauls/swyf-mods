using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// Emote wheel on the butt-scratch key (B): hold it, point with the mouse, release to play.
    /// Game emotes go through the game's own networked emote command, so everyone sees them; the extra ones
    /// need the mod on the host (and on whoever wants to see them).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dex.swyf.emotes";
        public const string PluginName = "Emoty";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        private static ConfigEntry<bool> _wheelEnabled;
        internal static bool WheelEnabled => _wheelEnabled == null || _wheelEnabled.Value;
        private static ConfigEntry<bool> _loopDances;
        private static ConfigEntry<string> _wheelEmotes;
        private static ConfigEntry<int> _wheelPages;

        /// <summary>The player's own wheel (ids from EmoteCatalog.Id), empty = default pages.</summary>
        internal static List<string> WheelEmotes
        {
            get
            {
                // One entry per wheel slot (8 per page); "" = empty slot.
                var list = new List<string>();
                if (_wheelEmotes != null && _wheelEmotes.Value.Length > 0) list.AddRange(_wheelEmotes.Value.Split('|'));
                return list;
            }
            set
            {
                if (_wheelEmotes == null) return;
                int last = value.FindLastIndex(v => !string.IsNullOrEmpty(v));
                _wheelEmotes.Value = last < 0 ? "" : string.Join("|", value.GetRange(0, last + 1));
            }
        }

        /// <summary>How many pages the wheel has (0 = as many as needed).</summary>
        internal static int WheelPages
        {
            get => _wheelPages == null ? 0 : _wheelPages.Value;
            set { if (_wheelPages != null) _wheelPages.Value = value; }
        }
        internal static bool LoopDances => _loopDances == null || _loopDances.Value;

        private Harmony _harmony;
        private EmoteWheel _wheel;
        private bool _held;
        private float _heldSince;
        private EmoteEntry _last;

        private void Awake()
        {
            Log = Logger;
            _wheelEnabled = Config.Bind("General", "WheelOnButtScratchKey", true,
                "Hold the butt-scratch key (B) for the emote wheel. false = B scratches like in the original game.");
            var movable = Config.Bind("General", "MovableDances2",
                "ZombieShambles, MashedPotato, Chicken Dance, Advanced Shuffle, Dancing Maraschino Step, Dirtbike Challenge, Fast Feet, Fishin', Forget Me Not, Freedom Wheels, Gangnam Style, Get Griddy, Go Mufasa, In Da Party, I Like To Move It, Roller Vibes, Rushin' Around, Side Shuffle, Slide Stride, Smeeze, The Airplane, Walkin' Pretty, Wake Up, Wind Up",
                "Dances you can walk in (slower, no sprint). Clip or dance names, comma separated; name:0.3 = 30 % of walking speed (default 0.55).");
            MovableDances.Configure(movable.Value);
            movable.SettingChanged += (_, __) => MovableDances.Configure(movable.Value);
            _wheelEmotes = Config.Bind("Wheel", "Emotes", "", "Emotes on your wheel, in order (set in Employee setup → Emoty). Empty = all, grouped by type.");
            _wheelPages = Config.Bind("Wheel", "Pages", 0, "Number of wheel pages (8 emotes each). 0 = as many as needed.");
            _loopDances = Config.Bind("General", "LoopDances", true, "Dances repeat until you move. false = play once like the game does.");
            try
            {
                string folder = System.IO.Path.Combine(Paths.GameRootPath, "Mods", "Emotes");
                DanceLibrary.LoadFolder(folder);
                EmoteCatalog.Rebuild();
                DanceMusic.Scan(System.IO.Path.Combine(folder, "music"));
            }
            catch (System.Exception e)
            {
                Log.LogError("Could not load the Mixamo dances: " + e.Message);
            }
            gameObject.AddComponent<DanceDriver>();
            LanguageManager.OnLanguageChanged += OnLanguageChanged;
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded ({DanceLibrary.All.Count} dances, {DanceMusic.Count} songs). Hold B for the emote wheel.");
        }

        private void Update()
        {
            if (!WheelEnabled) return;
            bool down = GameInputBindings.Held("Shortcuts", "ButtScratch") && CanOpen();

            if (down && !_held)
            {
                _held = true;
                _heldSince = Time.unscaledTime;
                try
                {
                    if (_wheel == null) _wheel = new EmoteWheel();
                    _wheel.Open();
                }
                catch (System.Exception e)
                {
                    Log.LogError("Emote wheel could not open: " + e);
                }
            }
            else if (!down && _held)
            {
                _held = false;
                var choice = _wheel?.Close();
                // A quick tap without pointing anywhere repeats the last emote.
                if (choice == null && Time.unscaledTime - _heldSince < 0.2f) choice = _last;
                if (choice != null)
                {
                    _last = choice;
                    try { LocalPlayer.Play(choice, this); }
                    catch (System.Exception e) { Log.LogError($"Could not play {choice.Label}: {e}"); }
                }
            }
            _wheel?.Tick();
        }

        /// <summary>Same situations in which the game itself lets you emote: walking around, not at a computer or in a menu.</summary>
        private static bool CanOpen()
        {
            if (!LocalPlayer.Exists) return false;
            if (KolkataMonitorInputState.IsMonitorInputCaptured || FeedbackUi.IsOpen) return false;
            if (SettingsUi.Instance != null && SettingsUi.Instance.IsOpen) return false;
            if (TabMenu.Instance != null && TabMenu.Instance.IsOpen) return false;
            return Cursor.lockState == CursorLockMode.Locked;
        }

        // Page titles ("Dances A–C", "My emotes") are built in the current language.
        private static void OnLanguageChanged(string _) => EmoteCatalog.Rebuild();

        private void OnDestroy()
        {
            LanguageManager.OnLanguageChanged -= OnLanguageChanged;
            _wheel?.Dispose();
            _wheel = null;
            _harmony?.UnpatchSelf();
        }
    }
}

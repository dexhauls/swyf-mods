using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace CzechSpeech
{
    /// <summary>
    /// Better Czech speech recognition: swaps the game's small Whisper model (ggml-base) for a big one
    /// from Mods/CzechSpeech, forces Czech, uses beam search and cleans up typical Whisper nonsense.
    /// Only changes your own microphone transcription, so it is safe in any lobby.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dex.swyf.czechspeech";
        public const string PluginName = "Czech Speech";
        public const string PluginVersion = "1.0.1";

        /// <summary>Preferred models, best first. Anything else ending in .bin is used as a last resort.</summary>
        private static readonly string[] PreferredModels =
        {
            "ggml-large-v3.bin",
            "ggml-large-v3-turbo.bin",
            "ggml-large-v3-q5_0.bin",
            "ggml-large-v3-turbo-q8_0.bin",
            "ggml-large-v3-turbo-q5_0.bin",
        };

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> ModelFile;
        internal static ConfigEntry<bool> FlashAttention;
        internal static ConfigEntry<string> Language;
        internal static ConfigEntry<int> BeamSize;
        internal static ConfigEntry<bool> FastPartials;
        internal static ConfigEntry<string> InitialPrompt;
        internal static ConfigEntry<bool> NormalizeVolume;
        internal static ConfigEntry<float> SpeechThreshold;
        internal static ConfigEntry<bool> CleanTranscript;
        internal static ConfigEntry<bool> LogTranscripts;

        /// <summary>Folder the mod looks for models in: Mods/CzechSpeech.</summary>
        // Not Paths.PluginPath / Assembly.Location: the SWYF Mod Manager loads mods from memory (see MOD-FORMAT.md).
        internal static string ModelFolder => Path.Combine(Paths.GameRootPath, "Mods", "CzechSpeech");

        /// <summary>Lowercase language code to force, or null to follow the game's speech language.</summary>
        internal static string ForcedLanguage
        {
            get
            {
                string code = Language.Value?.Trim().ToLowerInvariant();
                return string.IsNullOrEmpty(code) || code == "auto" ? null : code;
            }
        }

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Use the better speech recognition.");
            ModelFile = Config.Bind("Model", "Model", "large-v3",
                "Which model from Mods/CzechSpeech to use: large-v3 (best, ~4 GB VRAM), turbo (faster, for weaker PCs, ~2 GB VRAM), " +
                "or a file name / full path. If it is missing, the best model found there is used.");
            FlashAttention = Config.Bind("Model", "FlashAttention", false,
                "Whisper flash attention. The game turns it on, but with its Vulkan build it makes recognition ~10x slower. Leave off.");
            Language = Config.Bind("Recognition", "Language", "cs",
                "Language Whisper listens for (cs, sk, en...). Empty = the game's speech language from Audio settings.");
            BeamSize = Config.Bind("Recognition", "BeamSize", 5,
                "Beam search width for finished sentences. 1 = greedy like the base game, 5 = more accurate.");
            FastPartials = Config.Bind("Recognition", "FastPartials", true,
                "Live preview while you are still talking uses fast greedy decoding; the final sentence uses beam search.");
            InitialPrompt = Config.Bind("Recognition", "InitialPrompt",
                "Přepis telefonního hovoru v češtině s diakritikou a interpunkcí.",
                "Text Whisper sees before your speech. Steers it to Czech spelling and punctuation. Use a sentence nobody would say out loud: " +
                "on noise Whisper may repeat it, and such echoes are thrown away. Empty = none.");
            NormalizeVolume = Config.Bind("Audio", "NormalizeVolume", true,
                "Even out loudness before recognition, so whispering and shouting both come through.");
            SpeechThreshold = Config.Bind("Audio", "SpeechThreshold", 0f,
                "Microphone level (RMS) that counts as speech. 0 = the game's value (0.002). Lower catches quieter whispering, " +
                "but must stay above your room noise, otherwise sentences never end.");
            CleanTranscript = Config.Bind("Recognition", "CleanTranscript", true,
                "Remove typical Whisper nonsense (\"Titulky vytvořil JohnyX\", repeated words, the prompt echoed back).");
            LogTranscripts = Config.Bind("Debug", "LogTranscripts", false,
                "Write what was recognized into BepInEx/LogOutput.log (for testing).");

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} {PluginVersion} is disabled in the config.");
                return;
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            string model = FindModel();
            Log.LogInfo(model != null
                ? $"{PluginName} {PluginVersion} loaded. Model: {model} ({new FileInfo(model).Length / 1048576} MiB), language: {ForcedLanguage ?? "game setting"}, beam: {BeamSize.Value}."
                : $"{PluginName} {PluginVersion} loaded without a model in {ModelFolder}; using the game's model with better settings.");
        }

        private Harmony _harmony;

        // Turned off by the Mod Manager while the game runs: stop patching. The big model the game already
        // loaded stays in memory until the game restarts (it is loaded once, at startup).
        private void OnDestroy() => _harmony?.UnpatchSelf();

        /// <summary>Full path of the model to use, or null when there is none.</summary>
        internal static string FindModel()
        {
            string configured = ModelFile.Value?.Trim();
            switch (configured?.ToLowerInvariant())
            {
                case "large-v3": configured = "ggml-large-v3.bin"; break;
                case "turbo":
                case "large-v3-turbo": configured = "ggml-large-v3-turbo.bin"; break;
            }
            if (!string.IsNullOrEmpty(configured))
            {
                string path = Path.IsPathRooted(configured) ? configured : Path.Combine(ModelFolder, configured);
                if (File.Exists(path))
                {
                    return path;
                }
                Log.LogWarning($"Configured model {path} does not exist, looking for another one.");
            }

            if (!Directory.Exists(ModelFolder))
            {
                return null;
            }
            foreach (string name in PreferredModels)
            {
                string path = Path.Combine(ModelFolder, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return Directory.GetFiles(ModelFolder, "*.bin")
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
    }
}

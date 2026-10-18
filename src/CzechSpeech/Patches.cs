using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Whisper;
using Whisper.Native;

namespace CzechSpeech
{
    /// <summary>Shared state of the patches below.</summary>
    internal static class Speech
    {
        /// <summary>Multilingual managers the mod takes over (the game's English-only one is left alone).</summary>
        internal static readonly HashSet<WhisperManager> Targets = new HashSet<WhisperManager>();

        /// <summary>Our model path -> the game's original model path, used as a fallback if ours fails to load.</summary>
        internal static readonly Dictionary<string, string> OriginalModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether the inference being started is a finished sentence (true) or a live preview.</summary>
        internal static bool CurrentIsFinal = true;

        private static bool _loggedParams;

        internal static readonly AccessTools.FieldRef<WhisperManager, WhisperParams> ManagerParams =
            AccessTools.FieldRefAccess<WhisperManager, WhisperParams>("_params");

        internal static readonly AccessTools.FieldRef<WhisperParams, WhisperNativeParams> NativeParams =
            AccessTools.FieldRefAccess<WhisperParams, WhisperNativeParams>("_param");

        private static readonly FieldInfo BeamSearchField = AccessTools.Field(typeof(WhisperNativeParams), "beam_search");
        private static readonly FieldInfo BeamSizeField = AccessTools.Field(BeamSearchField.FieldType, "beam_size");
        private static readonly FieldInfo PatienceField = AccessTools.Field(BeamSearchField.FieldType, "patience");
        private static readonly FieldInfo SuppressNonSpeechField = AccessTools.Field(typeof(WhisperNativeParams), "suppress_non_speech_tokens");

        [DllImport("libwhisper", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr whisper_init_from_file_with_params(string path, WhisperNativeContextParams contextParams);

        private static readonly ConstructorInfo WrapperCtor = AccessTools.Constructor(typeof(WhisperWrapper), new[] { typeof(IntPtr) });

        [DllImport("ggml", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr ggml_backend_dev_count();

        [DllImport("ggml", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ggml_backend_dev_get(UIntPtr index);

        [DllImport("ggml-base", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ggml_backend_dev_name(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeLogCallback(int level, IntPtr text, IntPtr userData);

        [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void whisper_log_set(NativeLogCallback callback, IntPtr userData);

        [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void whisper_print_timings(IntPtr ctx);

        [DllImport("libwhisper", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void whisper_reset_timings(IntPtr ctx);

        private static NativeLogCallback _nativeLog;
        private static readonly StringBuilder NativeLine = new StringBuilder();

        /// <summary>Debug only: copies whisper.cpp / ggml's own log (backend, timings) into the BepInEx log.</summary>
        internal static void ForwardNativeLog()
        {
            if (_nativeLog != null)
            {
                return;
            }
            _nativeLog = (level, text, userData) =>
            {
                lock (NativeLine)
                {
                    NativeLine.Append(Marshal.PtrToStringAnsi(text));
                    string line = NativeLine.ToString();
                    if (!line.EndsWith("\n"))
                    {
                        return;
                    }
                    NativeLine.Length = 0;
                    Plugin.Log.LogInfo("[whisper.cpp] " + line.TrimEnd());
                }
            };
            whisper_log_set(_nativeLog, IntPtr.Zero);
        }

        /// <summary>Names of the compute devices ggml can use in this process, e.g. "Vulkan0, CPU".</summary>
        internal static string DescribeDevices()
        {
            try
            {
                var names = new List<string>();
                ulong count = ggml_backend_dev_count().ToUInt64();
                for (ulong i = 0; i < count; i++)
                {
                    names.Add(Marshal.PtrToStringAnsi(ggml_backend_dev_name(ggml_backend_dev_get(new UIntPtr(i)))));
                }
                return string.Join(", ", names.ToArray());
            }
            catch (Exception e)
            {
                return "unknown (" + e.GetType().Name + ")";
            }
        }

        /// <summary>
        /// Loads a model straight from disk. The game reads the whole file into a managed byte[] first,
        /// which can't hold files over 2 GB (large-v3 is 3 GB) and wastes RAM; whisper.cpp can read the file itself.
        /// </summary>
        internal static Task<WhisperWrapper> LoadFromFileAsync(string path, WhisperContextParams contextParams)
        {
            return Task.Run(async () =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Plugin.Log.LogInfo($"Loading Whisper model {Path.GetFileName(path)} (GPU: {contextParams.NativeParams.use_gpu})...");
                IntPtr ctx = IntPtr.Zero;
                try
                {
                    if (Plugin.LogTranscripts.Value)
                    {
                        ForwardNativeLog();
                    }
                    ctx = whisper_init_from_file_with_params(path, contextParams.NativeParams);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Native model load threw: {e}");
                }

                if (ctx != IntPtr.Zero)
                {
                    Plugin.Log.LogInfo($"Whisper model {Path.GetFileName(path)} loaded in {timer.ElapsedMilliseconds} ms. Devices: {DescribeDevices()}.");
                    return (WhisperWrapper)WrapperCtor.Invoke(new object[] { ctx });
                }

                if (OriginalModels.TryGetValue(path, out string original))
                {
                    Plugin.Log.LogError($"Could not load {path}, falling back to the game's model {original}.");
                    return await WhisperWrapper.InitFromFileAsync(original, contextParams);
                }
                Plugin.Log.LogError($"Could not load {path}.");
                return null;
            });
        }

        /// <summary>Sets the decoding options the game's wrapper doesn't expose (beam size, non-speech suppression).</summary>
        internal static void ApplyNativeParams(WhisperManager manager)
        {
            WhisperParams param = ManagerParams(manager);
            if (param == null)
            {
                return;
            }

            int beam = Plugin.FastPartials.Value && !CurrentIsFinal ? 1 : Math.Max(1, Plugin.BeamSize.Value);
            ref WhisperNativeParams native = ref NativeParams(param);
            object boxed = native;
            if (beam > 1)
            {
                object beamSearch = BeamSearchField.GetValue(boxed);
                BeamSizeField.SetValue(beamSearch, beam);
                PatienceField.SetValue(beamSearch, -1f);
                BeamSearchField.SetValue(boxed, beamSearch);
            }
            SuppressNonSpeechField.SetValue(boxed, true);
            native = (WhisperNativeParams)boxed;
            native.strategy = beam > 1 ? WhisperSamplingStrategy.WHISPER_SAMPLING_BEAM_SEARCH : WhisperSamplingStrategy.WHISPER_SAMPLING_GREEDY;

            if (!_loggedParams)
            {
                _loggedParams = true;
                Plugin.Log.LogInfo($"Recognition settings: model={manager.ModelPath}, language={manager.language}, " +
                                   $"strategy={native.strategy}, beam={beam}, prompt=\"{manager.initialPrompt}\".");
            }
        }

        /// <summary>
        /// Brings the utterance to a steady loudness: whispering gets louder, shouting quieter, peaks never clip.
        /// The level is the loud end (90th percentile) of 20 ms windows, so silence and the zero padding
        /// the game adds to short clips don't count.
        /// </summary>
        internal static void NormalizeVolume(float[] samples)
        {
            const int window = 320;
            const float targetLevel = 0.1f;
            int windows = samples.Length / window;
            if (windows == 0)
            {
                return;
            }

            var levels = new float[windows];
            float peak = 0f;
            for (int w = 0; w < windows; w++)
            {
                double sum = 0;
                for (int i = w * window, end = i + window; i < end; i++)
                {
                    float s = samples[i];
                    sum += s * s;
                    float a = Math.Abs(s);
                    if (a > peak)
                    {
                        peak = a;
                    }
                }
                levels[w] = (float)Math.Sqrt(sum / window);
            }
            Array.Sort(levels);
            float level = levels[(int)(windows * 0.9f)];
            if (level < 1e-5f || peak < 1e-5f)
            {
                return;
            }

            float gain = Mathf.Clamp(targetLevel / level, 0.25f, 12f);
            gain = Math.Min(gain, 0.97f / peak);
            if (Math.Abs(gain - 1f) < 0.05f)
            {
                return;
            }
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }
    }

    /// <summary>Points the game's multilingual WhisperManager at our model before it loads.</summary>
    [HarmonyPatch(typeof(WhisperManager), nameof(WhisperManager.InitModel))]
    internal static class WhisperManager_InitModel_Patch
    {
        private static readonly AccessTools.FieldRef<WhisperManager, bool> UseGpu =
            AccessTools.FieldRefAccess<WhisperManager, bool>("useGpu");

        private static readonly AccessTools.FieldRef<WhisperManager, bool> FlashAttention =
            AccessTools.FieldRefAccess<WhisperManager, bool>("flashAttention");

        private static readonly AccessTools.FieldRef<WhisperManager, WhisperSamplingStrategy> Strategy =
            AccessTools.FieldRefAccess<WhisperManager, WhisperSamplingStrategy>("strategy");

        private static void Prefix(WhisperManager __instance)
        {
            if (__instance.IsLoaded || __instance.IsLoading)
            {
                return;
            }
            string current = __instance.ModelPath ?? "";
            if (current.EndsWith(".en.bin", StringComparison.OrdinalIgnoreCase))
            {
                return; // English-only model, can't do Czech.
            }

            Speech.Targets.Add(__instance);
            // The game turns flash attention on, but on its Vulkan build that makes the encoder ~10x slower
            // (large-v3: 2 s -> 17 s per sentence on an RTX 5070 Ti).
            FlashAttention(__instance) = Plugin.FlashAttention.Value;
            if (Plugin.BeamSize.Value > 1)
            {
                Strategy(__instance) = WhisperSamplingStrategy.WHISPER_SAMPLING_BEAM_SEARCH;
            }

            string model = Plugin.FindModel();
            if (model == null || string.Equals(current, model, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string original = __instance.IsModelPathInStreamingAssets ? Path.Combine(Application.streamingAssetsPath, current) : current;
            Speech.OriginalModels[model] = original;
            __instance.IsModelPathInStreamingAssets = false;
            __instance.ModelPath = model;
            UseGpu(__instance) = true;
            Plugin.Log.LogInfo($"Speech model {current} -> {model}");
        }
    }

    /// <summary>Loads our model straight from disk (see <see cref="Speech.LoadFromFileAsync"/>).</summary>
    [HarmonyPatch(typeof(WhisperWrapper), nameof(WhisperWrapper.InitFromFileAsync))]
    internal static class WhisperWrapper_InitFromFileAsync_Patch
    {
        private static bool Prefix(string modelPath, WhisperContextParams contextParams, ref Task<WhisperWrapper> __result)
        {
            if (!Speech.OriginalModels.ContainsKey(modelPath))
            {
                return true;
            }
            __result = Speech.LoadFromFileAsync(modelPath, contextParams);
            return false;
        }
    }

    /// <summary>Language and prompt go in before the game copies them to Whisper, beam size after.</summary>
    [HarmonyPatch(typeof(WhisperManager), "UpdateParams")]
    internal static class WhisperManager_UpdateParams_Patch
    {
        private static void Prefix(WhisperManager __instance)
        {
            if (!Speech.Targets.Contains(__instance))
            {
                return;
            }
            string language = Plugin.ForcedLanguage;
            if (language != null)
            {
                __instance.language = language;
            }
            __instance.translateToEnglish = false;
            __instance.initialPrompt = Plugin.InitialPrompt.Value?.Trim() ?? "";
        }

        private static void Postfix(WhisperManager __instance)
        {
            if (Speech.Targets.Contains(__instance))
            {
                Speech.ApplyNativeParams(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(WhisperManager), nameof(WhisperManager.GetTextAsync), typeof(float[]), typeof(int), typeof(int))]
    internal static class WhisperManager_GetTextAsync_Patch
    {
        private static void Prefix(WhisperManager __instance, float[] samples)
        {
            if (Plugin.NormalizeVolume.Value && samples != null && Speech.Targets.Contains(__instance))
            {
                Speech.NormalizeVolume(samples);
            }
        }
    }

    /// <summary>Remembers whether the inference about to run is a finished sentence or a live preview.</summary>
    [HarmonyPatch]
    internal static class WhisperVcPipeline_RunInferenceAsync_Patch
    {
        private static FieldInfo _isFinal;

        private static MethodBase TargetMethod()
        {
            _isFinal = AccessTools.Field(AccessTools.Inner(typeof(WhisperVcPipeline), "InferenceRequest"), "IsFinal");
            return AccessTools.Method(typeof(WhisperVcPipeline), "RunInferenceAsync");
        }

        private static void Prefix(object request)
        {
            Speech.CurrentIsFinal = request == null || (bool)_isFinal.GetValue(request);
        }
    }

    /// <summary>With a forced language the big multilingual model handles everything, English included.</summary>
    [HarmonyPatch(typeof(WhisperVcPipeline), "GetRecognizer")]
    internal static class WhisperVcPipeline_GetRecognizer_Patch
    {
        private static readonly AccessTools.FieldRef<WhisperVcPipeline, WhisperManager> MainManager =
            AccessTools.FieldRefAccess<WhisperVcPipeline, WhisperManager>("whisperManager");

        private static bool Prefix(WhisperVcPipeline __instance, ref WhisperManager __result)
        {
            if (Plugin.ForcedLanguage == null || MainManager(__instance) == null)
            {
                return true;
            }
            __result = MainManager(__instance);
            return false;
        }
    }

    /// <summary>Moonshine is an English-only fallback; never use it while another language is forced.</summary>
    [HarmonyPatch(typeof(WindowsSpeechCompatibility), nameof(WindowsSpeechCompatibility.UseMoonshine))]
    internal static class WindowsSpeechCompatibility_UseMoonshine_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            string language = Plugin.ForcedLanguage;
            if (language == null || language == "en")
            {
                return true;
            }
            __result = false;
            return false;
        }
    }

    /// <summary>Lower speech threshold so whispering starts a recognition.</summary>
    [HarmonyPatch(typeof(WhisperVcPipeline), "OnEnable")]
    internal static class WhisperVcPipeline_OnEnable_Patch
    {
        private static readonly AccessTools.FieldRef<WhisperVcPipeline, float> Threshold =
            AccessTools.FieldRefAccess<WhisperVcPipeline, float>("speechRmsThreshold");

        private static void Prefix(WhisperVcPipeline __instance)
        {
            float value = Plugin.SpeechThreshold.Value;
            if (value > 0f && Math.Abs(Threshold(__instance) - value) > 1e-6f)
            {
                Plugin.Log.LogInfo($"Speech threshold {Threshold(__instance)} -> {value}");
                Threshold(__instance) = value;
            }
        }
    }

    /// <summary>With LogTranscripts on, logs how long Whisper itself took (without the game's queueing).</summary>
    [HarmonyPatch(typeof(WhisperWrapper), nameof(WhisperWrapper.GetText))]
    internal static class WhisperWrapper_GetText_Patch
    {
        private static readonly AccessTools.FieldRef<WhisperWrapper, IntPtr> Context =
            AccessTools.FieldRefAccess<WhisperWrapper, IntPtr>("_whisperCtx");

        private static void Prefix(WhisperWrapper __instance, out System.Diagnostics.Stopwatch __state)
        {
            __state = System.Diagnostics.Stopwatch.StartNew();
            if (Plugin.LogTranscripts.Value)
            {
                Speech.whisper_reset_timings(Context(__instance));
            }
        }

        private static void Postfix(WhisperWrapper __instance, float[] samples, System.Diagnostics.Stopwatch __state)
        {
            if (Plugin.LogTranscripts.Value)
            {
                Plugin.Log.LogInfo($"Whisper took {__state.ElapsedMilliseconds} ms for {samples.Length / 16} ms of audio.");
                Speech.whisper_print_timings(Context(__instance));
            }
        }
    }

    [HarmonyPatch(typeof(WhisperVcPipeline), "NormalizeTranscript")]
    internal static class WhisperVcPipeline_NormalizeTranscript_Patch
    {
        private static void Postfix(string transcript, ref string __result)
        {
            if (Plugin.CleanTranscript.Value)
            {
                __result = TranscriptCleaner.Clean(__result);
            }
            if (Plugin.LogTranscripts.Value && !string.IsNullOrWhiteSpace(transcript))
            {
                Plugin.Log.LogInfo($"Heard: \"{transcript.Trim()}\" -> \"{__result}\"");
            }
        }
    }
}

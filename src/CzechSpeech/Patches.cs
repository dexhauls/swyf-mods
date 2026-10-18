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

        /// <summary>
        /// The game turns the GPU off for Whisper on some graphics cards (WindowsSpeechCompatibility.NeedsCpuRecognition:
        /// GTX 1650, RX 470-580, RX 7900 XTX...) by setting this variable before ggml loads its Vulkan backend.
        /// </summary>
        internal static bool GameForcesCpu => Environment.GetEnvironmentVariable("KOLKATA_WHISPER_CPU_ONLY") == "1";

        /// <summary>The game's own verdict for this graphics card, before the Device setting changed it.</summary>
        internal static bool GameWantedCpu;

        /// <summary>Graphics card as Unity sees it, e.g. "NVIDIA GeForce RTX 5090 (10DE:2B85)". Read on the main thread.</summary>
        internal static string GraphicsCard = "";

        /// <summary>Why recognition runs where it runs, for the log.</summary>
        internal static string DeviceReason()
        {
            string setting = "Device = " + (Plugin.Device.Value?.Trim().ToLowerInvariant() ?? "auto");
            if (Plugin.EffectiveDevice == "cpu")
            {
                return setting == "Device = cpu" ? setting
                    : File.Exists(Path.Combine(Plugin.ModelFolder, Plugin.GpuFailedFile))
                        ? setting + ", but the graphics card failed before (" + Plugin.GpuFailedFile + ")"
                        : setting + ", but the game was started with -whisper-cpu";
            }
            return GameWantedCpu ? setting + ", ignoring the game's choice to keep this card on the processor" : setting;
        }

        /// <summary>Whether the loaded model runs on a graphics card; null until a model is loaded.</summary>
        internal static bool? OnGpu;

        /// <summary>Length of the audio about to be transcribed, set just before the game copies the parameters.</summary>
        internal static double NextAudioSeconds = 30;

        /// <summary>Running average of how long one Whisper call takes, in ms.</summary>
        private static double _averageMs;

        /// <summary>Whisper threads on the processor: about one per physical core, the game needs the rest.</summary>
        internal static int CpuThreads => Math.Max(4, Math.Min(8, Environment.ProcessorCount / 2));

        /// <summary>Recognition is slow here: on the processor, or a weak graphics card.</summary>
        internal static bool IsSlow => OnGpu == false || _averageMs > 2500;

        /// <summary>
        /// The game transcribes the unfinished sentence every second as a live preview. Whisper does one thing at a time,
        /// so on a slow PC the finished sentence waits for a preview first: twice the delay. Skip previews there.
        /// </summary>
        internal static bool SkipPreview
        {
            get
            {
                switch (Plugin.LivePreview.Value?.Trim().ToLowerInvariant())
                {
                    case "on": return false;
                    case "off": return true;
                    default: return IsSlow;
                }
            }
        }

        internal static void RecordInference(long ms)
        {
            _averageMs = _averageMs <= 0 ? ms : _averageMs * 0.7 + ms * 0.3;
        }

        internal static void DetectGpu(bool gpuRequested)
        {
            OnGpu = gpuRequested && !GameForcesCpu && DeviceNames().Exists(n => !n.Equals("CPU", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly MethodInfo RecognitionErrorSetter = AccessTools.PropertySetter(typeof(WhisperVcPipeline), nameof(WhisperVcPipeline.RecognitionError));

        /// <summary>The line the phone shows under "TYPE OR SPEAK TO THE CALLER"; the game clears it once the model is ready.</summary>
        internal static void ShowStatus(WhisperManager manager, string text)
        {
            var pipeline = manager != null ? manager.GetComponent<WhisperVcPipeline>() : null;
            if (pipeline != null && RecognitionErrorSetter != null)
            {
                RecognitionErrorSetter.Invoke(pipeline, new object[] { text });
            }
        }

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

        private static bool ImportantNativeLine(string line)
        {
            string l = line.ToLowerInvariant();
            return l.Contains("backend") || l.Contains("ggml_vulkan: ") || l.Contains("total size") || l.Contains("use gpu") ||
                   l.Contains("flash attn") || l.Contains("error") || l.Contains("fail");
        }

        /// <summary>
        /// Copies whisper.cpp / ggml's own log into the BepInEx log: always which device it uses and errors,
        /// everything (incl. timings) with LogTranscripts.
        /// </summary>
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
                    // Without LogTranscripts only the lines that say which device whisper.cpp really uses, and errors.
                    if (Plugin.LogTranscripts.Value || ImportantNativeLine(line))
                    {
                        Plugin.Log.LogInfo("[whisper.cpp] " + line.TrimEnd());
                    }
                }
            };
            whisper_log_set(_nativeLog, IntPtr.Zero);
        }

        /// <summary>Compute devices ggml can use in this process, e.g. Vulkan0, CPU. Empty if that can't be read.</summary>
        internal static List<string> DeviceNames()
        {
            var names = new List<string>();
            try
            {
                ulong count = ggml_backend_dev_count().ToUInt64();
                for (ulong i = 0; i < count; i++)
                {
                    names.Add(Marshal.PtrToStringAnsi(ggml_backend_dev_name(ggml_backend_dev_get(new UIntPtr(i)))));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not list compute devices: " + e.GetType().Name);
            }
            return names;
        }

        /// <summary>
        /// One short run on silence right after loading, while the phone still says the model is loading. The first real
        /// sentence would otherwise pay for the first-use setup: ~1.5 s on an RTX 5070 Ti, ~10 s on the processor.
        /// </summary>
        private static unsafe void WarmUp(IntPtr ctx)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            WhisperNativeParams p = WhisperNative.whisper_full_default_params(WhisperSamplingStrategy.WHISPER_SAMPLING_GREEDY);
            p.no_context = true;
            p.single_segment = true;
            if (OnGpu != true)
            {
                p.n_threads = CpuThreads;
                p.audio_ctx = 256; // same window sizes the slow path uses later
            }
            var silence = new float[32000]; // whisper.cpp skips anything shorter than 1 s
            fixed (float* samples = silence)
            {
                WhisperNative.whisper_full(ctx, p, samples, silence.Length);
            }
            Plugin.Log.LogInfo($"Warmed up in {timer.ElapsedMilliseconds} ms.");
        }

        private static IntPtr Init(string path, WhisperNativeContextParams native)
        {
            try
            {
                return whisper_init_from_file_with_params(path, native);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Native model load threw: {e}");
                return IntPtr.Zero;
            }
        }

        private static void TryWrite(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not write {path}: {e.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not delete {path}: {e.Message}");
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
                WhisperNativeContextParams native = contextParams.NativeParams;
                Plugin.Log.LogInfo($"Loading Whisper model {Path.GetFileName(path)}. Graphics card: {GraphicsCard}. {DeviceReason()}.");
                ForwardNativeLog();

                // Trying the graphics card the game keeps off Whisper: leave a note, so that if the game dies in the
                // next seconds (driver crash), the next start knows and uses the processor (Plugin.ResolveDevice).
                bool overridingGame = Plugin.EffectiveDevice == "gpu" && GameWantedCpu;
                string tryPath = Path.Combine(Plugin.ModelFolder, Plugin.GpuTryFile);
                if (overridingGame)
                {
                    TryWrite(tryPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {GraphicsCard}, model {Path.GetFileName(path)}");
                }

                IntPtr ctx = Init(path, native);
                if (ctx == IntPtr.Zero && native.use_gpu)
                {
                    // whisper.cpp could not set the model up on the graphics card: the processor still works.
                    native.use_gpu = false;
                    string cpuPath = Plugin.ResolveModel(Plugin.CpuModel.Value) ?? path;
                    Plugin.Log.LogWarning($"Could not load the model on the graphics card, loading {Path.GetFileName(cpuPath)} on the processor.");
                    ctx = Init(cpuPath, native);
                    path = cpuPath;
                }

                if (ctx != IntPtr.Zero)
                {
                    DetectGpu(native.use_gpu);
                    Plugin.Log.LogInfo($"Whisper model {Path.GetFileName(path)} loaded in {timer.ElapsedMilliseconds} ms. " +
                                       $"Devices: {string.Join(", ", DeviceNames().ToArray())}. Runs on: " +
                                       (OnGpu == true ? "GPU" : $"CPU, {CpuThreads} threads") + $" ({DeviceReason()}).");
                    try
                    {
                        WarmUp(ctx);
                        if (overridingGame)
                        {
                            Plugin.Log.LogInfo("The graphics card handled a test run, keeping it.");
                        }
                        TryDelete(tryPath);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Warm-up failed: " + e.Message);
                    }
                    return (WhisperWrapper)WrapperCtor.Invoke(new object[] { ctx });
                }
                TryDelete(tryPath);

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

            if (OnGpu == null)
            {
                DetectGpu(true);
            }
            bool onCpu = OnGpu == false;
            bool slow = IsSlow;
            // Beam search multiplies the decoder work; when recognition is slow it costs seconds per sentence.
            int beam = slow || (Plugin.FastPartials.Value && !CurrentIsFinal) ? 1 : Math.Max(1, Plugin.BeamSize.Value);
            ref WhisperNativeParams native = ref NativeParams(param);
            if (onCpu)
            {
                native.n_threads = CpuThreads;
            }
            // Whisper always encodes a 30 s window, and on the processor or a weak GPU that encoder is most of the time.
            // Encoding only as much as the sentence needs (50 frames per second) cut large-v3 on the CPU from 11 s to
            // 3.4 s per sentence in tests, with the same text. A fast GPU keeps the full window (best quality).
            native.audio_ctx = slow ? Math.Max(256, Math.Min(1500, (int)(NextAudioSeconds * 50) + 64)) : 0;
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
                                   $"strategy={native.strategy}, beam={beam}, threads={native.n_threads}, " +
                                   $"live preview={(SkipPreview ? "off" : "on")}, prompt=\"{manager.initialPrompt}\".");
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

            // The game drops what you say until the model is ready; say so on the phone instead of staying silent.
            Speech.ShowStatus(__instance, "Načítám český model řeči… Za pár sekund můžeš mluvit, zatím piš.");

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
        private static void Prefix(WhisperManager __instance, float[] samples, int frequency, int channels)
        {
            if (samples == null || !Speech.Targets.Contains(__instance))
            {
                return;
            }
            Speech.NextAudioSeconds = samples.Length / (double)Math.Max(1, frequency * Math.Max(1, channels));
            if (Plugin.NormalizeVolume.Value)
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

    /// <summary>
    /// The game's check "keep Whisper off this graphics card" (RTX 5090, GTX 1650, RX 470-580...). It runs right after
    /// the mods load and before ggml loads Vulkan, so the Device setting can still decide either way.
    /// </summary>
    [HarmonyPatch(typeof(WindowsSpeechCompatibility), nameof(WindowsSpeechCompatibility.NeedsCpuRecognition))]
    internal static class WindowsSpeechCompatibility_NeedsCpuRecognition_Patch
    {
        private static void Postfix(int vendorId, int deviceId, ref bool __result)
        {
            Speech.GameWantedCpu |= __result;
            switch (Plugin.EffectiveDevice)
            {
                case "gpu":
                    __result = false;
                    break;
                case "cpu":
                    __result = true;
                    break;
            }
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

    /// <summary>On a slow PC, skip the live preview so the finished sentence never waits behind one.</summary>
    [HarmonyPatch(typeof(WhisperVcPipeline), "QueueInference")]
    internal static class WhisperVcPipeline_QueueInference_Patch
    {
        private static bool Prefix(bool isFinal) => isFinal || !Speech.SkipPreview;
    }

    /// <summary>
    /// Measures how long Whisper itself takes (without the game's queueing): feeds the slow-PC detection and logs every
    /// finished sentence's time, without its text, so a friend's log shows where the delay is.
    /// </summary>
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
            long ms = __state.ElapsedMilliseconds;
            bool wasSlow = Speech.IsSlow;
            Speech.RecordInference(ms);
            if (Speech.CurrentIsFinal || Plugin.LogTranscripts.Value)
            {
                Plugin.Log.LogInfo($"{(Speech.CurrentIsFinal ? "Sentence" : "Live preview")} recognized in {ms} ms " +
                                   $"({samples.Length / 16000.0:F1} s of audio, {(Speech.OnGpu == true ? "GPU" : "CPU")}).");
            }
            if (!wasSlow && Speech.IsSlow && Plugin.LivePreview.Value?.Trim().ToLowerInvariant() == "auto")
            {
                Plugin.Log.LogWarning("Recognition is slow on this PC: live preview turned off so finished sentences come sooner. " +
                                      "The turbo model (Model = turbo) would be faster.");
            }
            if (Plugin.LogTranscripts.Value)
            {
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

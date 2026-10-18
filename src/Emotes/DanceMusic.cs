using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Emotes
{
    /// <summary>
    /// Music for dances: Mods/Emotes/music/&lt;dance name&gt;.mp3|ogg|wav, played as a 3D sound on the dancer, so
    /// everyone nearby (with the mod) hears it, fading with distance. Ducks while anyone close by is talking.
    /// </summary>
    internal static class DanceMusic
    {
        public const float MinDistance = 3f, MaxDistance = 28f, Volume = 0.55f, DuckedVolume = 0.2f;

        private static string _folder;
        private static readonly Dictionary<string, string> Files = new Dictionary<string, string>();   // key -> loop path
        private static readonly Dictionary<string, string> Intros = new Dictionary<string, string>();  // key -> intro path
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static readonly Dictionary<string, Beat> Beats = new Dictionary<string, Beat>();         // key -> tempo
        private static readonly Dictionary<AudioSource, (string key, double loopDsp)> Playing =
            new Dictionary<AudioSource, (string, double)>();

        /// <summary>A loop's tempo: seconds per beat and when the first beat falls in the loop.</summary>
        private struct Beat
        {
            public float Period, Offset, Bpm;
        }
        private static readonly HashSet<string> Loading = new HashSet<string>();
        private static PlayerPushToTalk[] _talkers = new PlayerPushToTalk[0];
        private static float _nextTalkerScan;

        public static void Scan(string folder)
        {
            _folder = folder;
            Files.Clear();
            if (!Directory.Exists(folder)) return;
            Intros.Clear();
            foreach (var f in Directory.GetFiles(folder))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".mp3" && ext != ".ogg" && ext != ".wav") continue;
                string stem = Path.GetFileNameWithoutExtension(f);
                // "<dance>.intro.ogg" plays once before "<dance>.ogg" loops.
                var target = stem.EndsWith(".intro", System.StringComparison.OrdinalIgnoreCase) ? Intros : Files;
                if (target == Intros) stem = stem.Substring(0, stem.Length - 6);
                string k = Key(stem);
                if (!target.TryGetValue(k, out var existing) || Rank(f) > Rank(existing)) target[k] = f; // ogg beats mp3 beats wav
            }
        }

        private static int Rank(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".ogg": return 3;
                case ".mp3": return 2;
                default: return 1;
            }
        }

        public static int Count => Files.Count;

        /// <summary>"Dance" ids look like "swyf-dance:gangnamstyle"; music files are matched by name, ignoring punctuation.</summary>
        public static string Key(string name)
        {
            if (name.StartsWith(DanceLibrary.IdPrefix)) name = name.Substring(DanceLibrary.IdPrefix.Length);
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public static bool Has(Dance d) => d != null && Files.ContainsKey(Key(d.Id));

        /// <summary>Starts the dance's music on <paramref name="dancer"/>; returns the source (null = no music).</summary>
        /// <param name="onScheduled">Told the game time (Time.time) the intro and the loop start playing, once known.</param>
        public static AudioSource Start(MonoBehaviour host, Transform dancer, Dance d, System.Action<float, float> onScheduled = null)
        {
            if (!Has(d) || dancer == null) return null;
            var go = new GameObject("Emoty music");
            go.transform.SetParent(dancer, false);
            go.transform.localPosition = Vector3.up * 1f;
            string key = Key(d.Id);
            var loop = NewSource(go, true);
            var intro = Intros.ContainsKey(key) ? NewSource(go, false) : null;
            host.StartCoroutine(PlayWhenLoaded(intro, loop, key, onScheduled));
            return loop;
        }

        private static AudioSource NewSource(GameObject go, bool loop)
        {
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = MinDistance;
            src.maxDistance = MaxDistance;
            src.dopplerLevel = 0f;
            src.loop = loop;
            src.volume = Volume;
            src.playOnAwake = false;
            return src;
        }

        private static IEnumerator PlayWhenLoaded(AudioSource intro, AudioSource loop, string key, System.Action<float, float> onScheduled)
        {
            if (intro != null) yield return Load(Intros[key], "intro:" + key);
            yield return Load(Files[key], key);
            if (loop == null || !Clips.TryGetValue(key, out var loopClip) || loopClip == null) yield break;
            loop.clip = loopClip;
            double now = AudioSettings.dspTime, start = now + 0.05, loopAt = start;
            if (intro != null && Clips.TryGetValue("intro:" + key, out var introClip) && introClip != null)
            {
                // Sample-exact hand-over from the intro to the loop.
                intro.clip = introClip;
                intro.PlayScheduled(start);
                loopAt = start + (double)introClip.samples / introClip.frequency;
            }
            loop.PlayScheduled(loopAt);
            Playing[loop] = (key, loopAt);
            if (!Beats.ContainsKey(key)) Beats[key] = FindBeat(loopClip);
            onScheduled?.Invoke(Time.time + (float)(start - now), Time.time + (float)(loopAt - now));
        }

        /// <summary>
        /// Where the music playing from <paramref name="loop"/> is in its beat, 0..1 (0 = on the beat). Very quick
        /// songs count every other beat. False when unknown (no music, not started, tempo not found).
        /// </summary>
        public static bool BeatPhase(AudioSource loop, out float phase)
        {
            phase = 0f;
            if (loop == null || !Playing.TryGetValue(loop, out var p) || !Beats.TryGetValue(p.key, out var b) || b.Period <= 0f)
                return false;
            float period = b.Bpm > 140f ? b.Period * 2f : b.Period;
            phase = Mathf.Repeat((float)(AudioSettings.dspTime - p.loopDsp - b.Offset) / period, 1f);
            return true;
        }

        public static void Forget(AudioSource loop)
        {
            if (loop != null) Playing.Remove(loop);
        }

        // Tempo of a loop (tools/beat_detect.py): the loop holds whole bars, so try every tempo with a multiple of 4
        // beats per loop between 70 and 180 BPM and keep the beat grid that lines up best with the drum hits (the
        // jumps in high-frequency loudness, measured 100 times a second).
        private static Beat FindBeat(AudioClip clip)
        {
            try
            {
                int ch = Mathf.Max(1, clip.channels), sr = clip.frequency, total = clip.samples, hop = Mathf.Max(1, sr / 100);
                var env = new List<float>(total / hop + 1);
                var buf = new float[sr * ch];
                float x1 = 0f, x2 = 0f, acc = 0f;
                int count = 0;
                for (int start = 0; start < total; start += sr)
                {
                    int len = Mathf.Min(sr, total - start);
                    if (len < sr) buf = new float[len * ch];
                    if (!clip.GetData(buf, start)) return default;
                    for (int i = 0; i < len; i++)
                    {
                        float x = 0f;
                        for (int c = 0; c < ch; c++) x += buf[i * ch + c];
                        x /= ch;
                        acc += Mathf.Abs(x - 2f * x1 + x2);
                        x2 = x1;
                        x1 = x;
                        if (++count == hop) { env.Add(acc / hop); acc = 0f; count = 0; }
                    }
                }
                int m = env.Count - 1;
                if (m < 50) return default;
                var flux = new float[m];
                for (int i = 0; i < m; i++) flux[i] = Mathf.Max(0f, env[i + 1] - env[i]);
                float length = (float)total / sr, bestScore = -1f;
                var best = default(Beat);
                for (int beats = 4; beats <= (int)(180f * length / 60f) + 4; beats += 4)
                {
                    float bpm = beats * 60f / length;
                    if (bpm < 70f || bpm > 180f) continue;
                    float period = (float)m / beats;
                    for (int ph = 0; ph < (int)period; ph++)
                    {
                        float sum = 0f;
                        for (int k = 0; k < beats; k++)
                        {
                            int i = Mathf.RoundToInt(ph + k * period);
                            float v = 0f;
                            for (int d = -1; d <= 1; d++) v = Mathf.Max(v, flux[((i + d) % m + m) % m]);
                            sum += v;
                        }
                        float score = sum / beats * (bpm >= 85f && bpm <= 150f ? 1f : 0.85f);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new Beat { Period = length / beats, Offset = (ph + 1) / 100f, Bpm = bpm };
                        }
                    }
                }
                Plugin.Log.LogInfo($"Music tempo {clip.name}: {best.Bpm:0.0} BPM, first beat {best.Offset:0.00}s");
                return best;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Could not find the tempo of {clip.name}: {e.Message}");
                return default;
            }
        }

        private static IEnumerator Load(string path, string cacheKey)
        {
            if (!Clips.ContainsKey(cacheKey) && Loading.Add(cacheKey))
            {
                var type = Path.GetExtension(path).ToLowerInvariant() == ".mp3" ? AudioType.MPEG
                    : Path.GetExtension(path).ToLowerInvariant() == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
                using (var req = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), type))
                {
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) Clips[cacheKey] = DownloadHandlerAudioClip.GetContent(req);
                    else Plugin.Log.LogWarning($"Could not load music {path}: {req.error}");
                }
                Loading.Remove(cacheKey);
            }
            while (Loading.Contains(cacheKey)) yield return null;
        }

        /// <summary>True when a player near the listener is talking (voice chat goes first).</summary>
        public static bool SomeoneTalkingNear(Vector3 at)
        {
            if (Time.unscaledTime >= _nextTalkerScan)
            {
                _nextTalkerScan = Time.unscaledTime + 1f;
                _talkers = Object.FindObjectsOfType<PlayerPushToTalk>();
            }
            foreach (var p in _talkers)
                if (p != null && p.IsSpeaking && (p.transform.position - at).sqrMagnitude < MaxDistance * MaxDistance) return true;
            return false;
        }
    }
}

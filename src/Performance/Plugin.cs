using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Performance
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dex.swyf.performance";
        public const string PluginName = "Výkon";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal sealed class CustomConfig
        {
            public ConfigEntry<bool> ParkIdleAudio, ShadowCulling, TextureCameraShadows, TaaToSmaa, SkipCursorUpdates, DetachConsole, PropsUseSceneProbes;
            public ConfigEntry<float> ShadowCullFactor, ShadowCullMinDistance, ShadowDistance;
            public ConfigEntry<int> ExteriorShadowsOff, ShadowCascades, ShadowResolution, TextureCameraFps, MenuFps, BackgroundFps, FpsLimit;
        }

        private ConfigEntry<Preset> _preset;
        private ConfigEntry<bool> _showFps;
        private ConfigEntry<Key> _fpsKey, _menuKey;
        private SettingsMenu _menu;
        private bool _batchEdit;
        private CustomConfig _custom;
        private Profile _profile = new Profile();
        private Preset? _forced; // the benchmark switches presets without touching the config

        private Harmony _harmony;
        private GameObject _hooks;
        private Overlay _overlay;
        private Camera _cam;
        private float _nextCamLookup, _nextSlowTick;

        private string _commandFile, _reportFile;
        private float _nextPoll;
        private Bench _bench;
        private Preset _presetBeforeBench;
        private float _profileUntil = -1;
        private string[] _pendingBench;
        private string _pendingCommand;
        private float _stillSince = -1;
        private Vector3 _lastPos;
        private Quaternion _lastRot;
        internal static int MovedFrames;
        private Queue<Preset> _shots;
        private Preset? _shotPresetBefore;
        private Preset _shotPreset;
        private float _nextShot;
        private bool _shotTaken;
        private int _profileFrames;
        private int _turnTarget; // stop the turn recording once both buckets have this many frames

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            _preset = Config.Bind("General", "Preset", Preset.Balanced,
                "Off = the game as it is. Balanced = optimisations that don't change the look. MaxPerformance = a bit more (shorter shadows, SMAA instead of TAA). Custom = the [Custom] section below.");
            _showFps = Config.Bind("Overlay", "ShowFps", false, "FPS and frame time in the top-left corner.");
            _fpsKey = Config.Bind("Overlay", "FpsKey", Key.F10, "Shows / hides the FPS readout.");
            _menuKey = Config.Bind("Overlay", "MenuKey", Key.F11, "Opens the settings window (presets and every option).");
            _custom = new CustomConfig
            {
                ParkIdleAudio = Config.Bind("Custom", "ParkIdleAudio", true, "Switch off silent AudioSources (1400+ in the office) until the game plays them."),
                ShadowCulling = Config.Bind("Custom", "SmallShadowCulling", true, "Small props cast shadows only near the camera."),
                ShadowCullFactor = Config.Bind("Custom", "ShadowCullFactor", 20f, "A prop casts a shadow up to (its size × this) metres away."),
                ShadowCullMinDistance = Config.Bind("Custom", "ShadowCullMinDistance", 8f, "…but always within this many metres."),
                ShadowCascades = Config.Bind("Custom", "ShadowCascades", 2, "Main light shadow cascades (game: 4). 0 = the game's."),
                ShadowResolution = Config.Bind("Custom", "ShadowResolution", 4096, "Main light shadow map size (game: 1024). 0 = the game's."),
                ShadowDistance = Config.Bind("Custom", "ShadowDistance", 0f, "Main light shadow distance in m (game: 60). 0 = the game's."),
                TextureCameraFps = Config.Bind("Custom", "TextureCameraFps", 30, "Max FPS of cameras that render into a texture (boss webcam…). 0 = every frame."),
                TextureCameraShadows = Config.Bind("Custom", "TextureCameraShadows", true, "Shadows in those cameras."),
                TaaToSmaa = Config.Bind("Custom", "TaaToSmaa", false, "Use SMAA instead of TAA (a bit cheaper, slightly less smooth edges)."),
                SkipCursorUpdates = Config.Bind("Custom", "SkipCursorUpdates", true, "While the cursor is locked and hidden, don't let Unity re-set it on every mouse move (Windows call that costs a lot while turning the camera)."),
                DetachConsole = Config.Bind("Custom", "DetachConsole", true, "Close the BepInEx console window (its input queue is tied to the game's, so moving the mouse waited on it). The log still goes to BepInEx/LogOutput.log."),
                PropsUseSceneProbes = Config.Bind("Custom", "PropsUseSceneProbes", false, "Office props use the scene's light probes instead of their own baked lighting (that one turns off the SRP Batcher for ~1600 props, ~1 ms a frame). Small props get a slightly different shade."),
                ExteriorShadowsOff = Config.Bind("Custom", "ExteriorShadowsOff", 1, "Shadows of the 3 street lights outside (52 m range, they redraw the whole office 6×): 0 = keep, 1 = off while you are inside the office, 2 = always off."),
                MenuFps = Config.Bind("Custom", "MenuFpsLimit", 60, "FPS cap in the main menu. 0 = none."),
                BackgroundFps = Config.Bind("Custom", "BackgroundFpsLimit", 0, "FPS cap while the game window is in the background. 0 = none."),
                FpsLimit = Config.Bind("Custom", "FpsLimit", 0, "FPS cap everywhere. 0 = none."),
            };
            Config.SettingChanged += (s, e) => { if (!_batchEdit) Reconfigure(); };

            _commandFile = Path.Combine(Paths.BepInExRootPath, "perf-command.txt");
            _reportFile = Path.Combine(Paths.BepInExRootPath, "perf-report.txt");
            FrameStats.Init();
            StackSampler.CaptureMainThread();
            _harmony = new Harmony(PluginGuid);
            try { PropProbes.Patch(_harmony); }
            catch (Exception e) { Log.LogError("Prop lighting patch failed: " + e); }
            try { AudioParking.Patch(_harmony); }
            catch (Exception e) { Log.LogError("Audio patches failed, idle audio stays on: " + e); }
            _hooks = new GameObject("Výkon hooks") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(_hooks);
            _hooks.AddComponent<EarlyHook>();
            _hooks.AddComponent<LateHook>();
            try { _menu = SettingsMenu.Create(this, _hooks); }
            catch (Exception e) { Log.LogWarning("No settings window: " + e.Message); }
            try { _overlay = new Overlay(); }
            catch (Exception e) { Log.LogWarning("No FPS overlay: " + e.Message); }
            RepairFrameSettings();
            Reconfigure();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded, preset {CurrentPreset}.");
        }

        // A benchmark interrupted by a reload of this mod would leave VSync off: put back the game's own setting.
        private static void RepairFrameSettings()
        {
            try
            {
                if (PlayerPrefs.GetInt("Graphics VSync", 1) != 0 && QualitySettings.vSyncCount == 0)
                {
                    QualitySettings.vSyncCount = 1;
                    Application.targetFrameRate = -1;
                    Log.LogInfo("VSync was left off by an interrupted measurement; restored the game's setting (on).");
                }
            }
            catch { }
        }

        // ---------- used by the settings window ----------

        internal CustomConfig Custom => _custom;
        internal ConfigEntry<bool> ShowFps => _showFps;
        internal Profile ActiveProfile => _profile;

        internal void SetPreset(Preset p)
        {
            _forced = null;
            if (p == Preset.Custom && _preset.Value != Preset.Custom) { BeginCustomEdit(); EndCustomEdit(); return; }
            _preset.Value = p;
        }

        /// <summary>Switch to Custom starting from what runs now, so changing one option changes only that.</summary>
        internal void BeginCustomEdit()
        {
            _batchEdit = true;
            _forced = null;
            if (_preset.Value == Preset.Custom) return;
            var p = _profile;
            var c = _custom;
            c.ParkIdleAudio.Value = p.ParkIdleAudio;
            c.ShadowCulling.Value = p.ShadowCullFactor > 0;
            if (p.ShadowCullFactor > 0) { c.ShadowCullFactor.Value = p.ShadowCullFactor; c.ShadowCullMinDistance.Value = p.ShadowCullMinDistance; }
            c.ShadowCascades.Value = p.ShadowCascades;
            c.ShadowResolution.Value = p.ShadowResolution;
            c.ShadowDistance.Value = p.ShadowDistance;
            c.TextureCameraFps.Value = p.TextureCameraFps;
            c.TextureCameraShadows.Value = !p.TextureCameraNoShadows;
            c.TaaToSmaa.Value = p.TaaToSmaa;
            c.SkipCursorUpdates.Value = p.SkipCursorUpdates;
            c.DetachConsole.Value = p.DetachConsole;
            c.PropsUseSceneProbes.Value = p.PropsUseSceneProbes;
            c.ExteriorShadowsOff.Value = p.ExteriorShadowsOff;
            c.MenuFps.Value = p.MenuFps;
            c.BackgroundFps.Value = p.BackgroundFps;
            c.FpsLimit.Value = p.FpsLimit;
            _preset.Value = Preset.Custom;
        }

        internal void EndCustomEdit()
        {
            _batchEdit = false;
            Reconfigure();
        }

        internal Preset CurrentPreset => _forced ?? _preset.Value;

        private void Reconfigure()
        {
            _profile = Profile.For(CurrentPreset, _custom);
            AudioParking.SetEnabled(_profile.ParkIdleAudio);
            ShadowCulling.Configure(_profile.ShadowCullFactor, _profile.ShadowCullMinDistance);
            RenderTweaks.RestoreTextureCameras();
            RenderTweaks.ApplyShadows(_profile);
            RenderTweaks.ApplyAntiAliasing(_profile.TaaToSmaa);
            SetConsole(!_profile.DetachConsole);
            PropProbes.SetEnabled(_profile.PropsUseSceneProbes);
            ExteriorShadows.SetMode(_profile.ExteriorShadowsOff);
            try { CursorFilter.SetEnabled(_profile.SkipCursorUpdates); } catch (Exception e) { Log.LogWarning("Cursor filter: " + e.Message); }
            _nextSlowTick = 0;
        }

        // BepInEx's ConsoleManager is internal; only touch the console when our wish differs from its state.
        private static void SetConsole(bool show)
        {
            try
            {
                var cm = typeof(Paths).Assembly.GetType("BepInEx.ConsoleManager");
                bool active = (bool)cm.GetProperty("ConsoleActive").GetValue(null, null);
                bool enabledInConfig = (bool)cm.GetProperty("ConsoleEnabled").GetValue(null, null);
                if (show && !active && enabledInConfig) cm.GetMethod("CreateConsole").Invoke(null, null);
                else if (!show && active) cm.GetMethod("DetachConsole").Invoke(null, null);
            }
            catch (Exception e) { Log.LogWarning("Console: " + e.Message); }
        }

        internal void EarlyUpdate() => RenderTweaks.ReleaseTextureCameras();

        internal void VeryLateUpdate()
        {
            RenderTweaks.HoldTextureCameras(_profile);
            if (TurnProbe.Sweeping)
            {
                string done = TurnProbe.SweepTick();
                if (done != null) Write(done);
            }
            if (LoopTimer.Running) TurnProbe.TagFrame(_cam);
            else if (CursorAB.Running)
            {
                string ab = CursorAB.Tick(_cam);
                if (ab != null)
                {
                    Write(ab);
                    CursorFilter.SetEnabled(_profile.SkipCursorUpdates);
                }
            }
        }

        private void Update()
        {
            FrameStats.Tick();
            Keys();

            if (Time.unscaledTime > _nextCamLookup) { _nextCamLookup = Time.unscaledTime + 1f; _cam = Camera.main; }
            AudioParking.Tick();
            ShadowCulling.Tick(_cam);
            CursorFilter.Tick();
            PropProbes.Tick();
            ExteriorShadows.Tick(_cam);
            StaticMerge.Tick();
            if (!CursorAB.Running) GapTimer.Set(_showFps.Value);
            if (_bench == null) FrameLimiter.Tick(_profile); // the benchmark runs uncapped
            if (Time.unscaledTime > _nextSlowTick)
            {
                // The game may reset its URP asset / camera on a settings change; re-apply once a second.
                _nextSlowTick = Time.unscaledTime + 1f;
                RenderTweaks.ApplyShadows(_profile);
                RenderTweaks.ApplyAntiAliasing(_profile.TaaToSmaa);
            }
            _overlay?.Tick(_showFps.Value, Profile.Name(CurrentPreset));

            if (_shots != null) ShotsTick();
            TrackStillness();
            _bench?.Tick();
            if (_bench != null && _bench.Finished) _bench = null;
            if (_profileUntil > 0)
            {
                _profileFrames++;
                if (Time.realtimeSinceStartup > _profileUntil
                    || (_turnTarget > 0 && LoopTimer.FramesIn(0) >= _turnTarget && LoopTimer.FramesIn(1) >= _turnTarget))
                {
                    _turnTarget = 0;
                    FinishProfile();
                }
            }
            if (Time.realtimeSinceStartup > _nextPoll)
            {
                _nextPoll = Time.realtimeSinceStartup + 0.5f;
                PollCommand();
            }
        }

        private void Keys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            try { if (KolkataMonitorInputState.IsMonitorInputCaptured || FeedbackUi.IsOpen) return; } catch { }
            if (Pressed(kb, _fpsKey.Value)) _showFps.Value = !_showFps.Value;
            if (SettingsMenu.IsOpen && Pressed(kb, _menuKey.Value)) { _menu?.Close(); return; }
            if (Pressed(kb, _menuKey.Value)) _menu?.Toggle();
        }

        private static bool Pressed(Keyboard kb, Key key)
        {
            if (key == Key.None) return false;
            try { return kb[key].wasPressedThisFrame; } catch { return false; }
        }

        // ---------- Measurement commands (BepInEx/perf-command.txt → BepInEx/perf-report.txt) ----------

        private void PollCommand()
        {
            if (!File.Exists(_commandFile)) return;
            string cmd;
            try { cmd = File.ReadAllText(_commandFile).Trim(); File.Delete(_commandFile); }
            catch { return; }
            Log.LogInfo("Command: " + cmd);
            var parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            try
            {
                switch (parts[0])
                {
                    case "dump":
                        Write($"=== Scene report {DateTime.Now:HH:mm:ss} (preset {CurrentPreset}, {AudioParking.Stats}, {ShadowCulling.CulledCount} shadows culled)\n" + SceneReport.Build());
                        break;
                    case "profile":
                        StartProfile(parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 5f, parts.Length > 2 && parts[2] == "scripts");
                        break;
                    case "preset":
                        _forced = (Preset)Enum.Parse(typeof(Preset), parts[1], true);
                        Reconfigure();
                        break;
                    case "setpreset": // like F11: saved in the config
                        _forced = null;
                        _preset.Value = (Preset)Enum.Parse(typeof(Preset), parts[1], true);
                        Reconfigure();
                        Write($"Preset set to {_preset.Value}.");
                        break;
                    case "bench":
                        StartBench(parts);
                        break;
                    case "whenstill":
                        _pendingCommand = string.Join(" ", parts, 1, parts.Length - 1);
                        _stillSince = -1;
                        break;
                    case "benchwhenstill":
                        _pendingBench = parts;
                        _stillSince = -1;
                        break;
                    case "shotexp":
                    {
                        var exps = parts[1] == "merge" ? StaticMerge.Experiments() : parts[1] == "round4" ? Probes.Round4() : parts[1] == "round3" ? Probes.Round3() : parts[1] == "round2" ? Probes.Round2() : Probes.Diagnostic();
                        StartCoroutine(ShotExperiment(exps[int.Parse(parts[2])]));
                        break;
                    }
                    case "shots":
                        _shots = new Queue<Preset>(new[] { Preset.Off, Preset.Balanced, Preset.MaxPerformance, Preset.Off });
                        _shotPresetBefore = _forced;
                        _nextShot = Time.realtimeSinceStartup;
                        _shotTaken = true;
                        break;
                    case "fps":
                        _showFps.Value = parts.Length < 2 || parts[1] != "off";
                        break;
                    case "shot":
                        string dir = Path.Combine(Paths.BepInExRootPath, "perf-shots");
                        Directory.CreateDirectory(dir);
                        StartCoroutine(Capture(Path.Combine(dir, $"{DateTime.Now:HHmmss}-single.png")));
                        break;
                    case "turnwatch":
                        StartProfile(parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 20f, false);
                        TurnProbe.HookInput(true);
                        break;
                    case "hitches":
                        LoopTimer.HitchMs = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 25;
                        StartProfile(parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 120f, false);
                        break;
                    case "turnsample":
                        StartProfile(parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 30f, false);
                        _turnTarget = parts.Length > 2 ? int.Parse(parts[2]) : 0;
                        TurnProbe.HookInput(true);
                        StackSampler.Start();
                        break;
                    case "urpfix":
                        Write(RenderTweaks.Repair(int.Parse(parts[1]), int.Parse(parts[2])));
                        Reconfigure();
                        break;
                    case "menu":
                        _menu?.Toggle();
                        break;
                    case "menutab":
                        _menu?.ShowTabFor(int.Parse(parts[1]));
                        break;
                    case "urp":
                        Write(RenderTweaks.Debug());
                        break;
                    case "draws":
                        Write(DrawReport.Build());
                        break;
                    case "cursorab":
                        CursorAB.Start(parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 180f, parts.Length > 2 ? int.Parse(parts[2]) : 1500);
                        break;
                    case "cursor":
                        CursorFilter.SetEnabled(parts.Length < 2 || parts[1] != "off");
                        Write($"Cursor filter {(parts.Length < 2 || parts[1] != "off" ? "on" : "off")}.");
                        break;
                    case "console":
                    {
                        // ConsoleManager is internal in BepInEx 5.
                        var cm = typeof(BepInEx.Paths).Assembly.GetType("BepInEx.ConsoleManager");
                        cm.GetMethod(parts.Length > 1 && parts[1] == "off" ? "DetachConsole" : "CreateConsole").Invoke(null, null);
                        Write($"BepInEx console active: {cm.GetProperty("ConsoleActive").GetValue(null, null)}.");
                    }
                        break;
                    case "sweep":
                        TurnProbe.TurnSpeed = parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 270f;
                        TurnProbe.StartSweep(_cam);
                        break;
                    case "abort":
                        TurnProbe.AbortSweep();
                        _bench?.Abort();
                        _bench = null;
                        break;
                    default:
                        MeshMerge.Command(parts, Write);
                        break;
                }
            }
            catch (Exception e) { Write("Command failed: " + e); }
        }

        // Same moment, each preset: switch, let shadow culling settle, capture the screen.
        private void ShotsTick()
        {
            if (Time.realtimeSinceStartup < _nextShot) return;
            if (!_shotTaken)
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "perf-shots");
                Directory.CreateDirectory(dir);
                StartCoroutine(Capture(Path.Combine(dir, $"{DateTime.Now:HHmmss}-{_shotPreset}.png")));
                _shotTaken = true;
                _nextShot = Time.realtimeSinceStartup + 0.5f;
                return;
            }
            if (_shots.Count == 0)
            {
                _shots = null;
                _forced = _shotPresetBefore;
                Reconfigure();
                Write("Screenshots written to BepInEx/perf-shots.");
                return;
            }
            _shotPreset = _shots.Dequeue();
            _forced = _shotPreset;
            Reconfigure();
            _shotTaken = false;
            _nextShot = Time.realtimeSinceStartup + 1.5f;
        }

        // Same view with an experiment off and on, for a pixel comparison.
        private System.Collections.IEnumerator ShotExperiment(Bench.Experiment e)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "perf-shots");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("HHmmss");
            yield return Capture(Path.Combine(dir, $"{stamp}-exp-off.png"));
            e.Apply();
            yield return new WaitForSecondsRealtime(1f);
            yield return Capture(Path.Combine(dir, $"{stamp}-exp-on.png"));
            e.Revert();
            yield return new WaitForSecondsRealtime(1f);
            yield return Capture(Path.Combine(dir, $"{stamp}-exp-off2.png"));
            Write($"Experiment screenshots '{e.Name}': perf-shots/{stamp}-exp-*.png");
        }

        internal static System.Collections.IEnumerator Capture(string path)
        {
            yield return new WaitForEndOfFrame();
            var rt = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            // The render texture comes out upside down on Direct3D: flip rows.
            if (SystemInfo.graphicsUVStartsAtTop)
            {
                var px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                var flipped = new Color32[px.Length];
                for (int y = 0; y < h; y++) Array.Copy(px, y * w, flipped, (h - 1 - y) * w, w);
                tex.SetPixels32(flipped);
                tex.Apply();
            }
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Destroy(tex);
        }

        // Waits until the player has stood still for 8 s in a level, then runs the queued benchmark.
        private void TrackStillness()
        {
            var cam = _cam;
            if (cam == null) return;
            var t = cam.transform;
            bool moved = (t.position - _lastPos).sqrMagnitude > 0.0004f || Quaternion.Angle(t.rotation, _lastRot) > 0.3f;
            _lastPos = t.position;
            _lastRot = t.rotation;
            if (moved) MovedFrames++;
            if (_pendingBench == null && _pendingCommand == null) return;
            if (moved || FrameLimiter.InMenu() || Mirror.NetworkClient.localPlayer == null) { _stillSince = -1; return; }
            if (_stillSince < 0) _stillSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _stillSince > 8f)
            {
                if (_pendingCommand != null)
                {
                    try { File.WriteAllText(_commandFile, _pendingCommand); } catch { }
                    _pendingCommand = null;
                    return;
                }
                var parts = _pendingBench;
                _pendingBench = null;
                Write($"=== Player still for 8 s at {t.position}, starting the benchmark.");
                StartBench(parts);
            }
        }

        private void StartBench(string[] parts)
        {
            MovedFrames = 0;
            _bench?.Abort();
            FrameLimiter.Release();
            int rounds = parts.Length > 1 ? int.Parse(parts[1]) : 2;
            float secs = parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 2.5f;
            string set = parts.Length > 3 ? parts[3] : "presets";
            List<Bench.Experiment> list;
            float settle = 0.6f;
            if (set == "diagnostic") list = Probes.Diagnostic();
            else if (set == "round2") list = Probes.Round2();
            else if (set == "round3") list = Probes.Round3();
            else if (set == "round4") list = Probes.Round4();
            else if (set == "merge") list = StaticMerge.Experiments();
            else
            {
                settle = 1.2f;
                _presetBeforeBench = CurrentPreset;
                _forced = Preset.Off;
                Reconfigure();
                list = PresetExperiments();
                if (set == "presets2") list = list.GetRange(0, 2);
            }
            _bench = new Bench(set, list, rounds, settle, secs, text =>
            {
                Write(text + $"  (camera moved in {MovedFrames} frames during the run)");
                if (set.StartsWith("presets")) { _forced = null; Reconfigure(); }
            });
            _bench.Start();
        }

        private List<Bench.Experiment> PresetExperiments()
        {
            var list = new List<Bench.Experiment>();
            void Add(string name, Preset preset, Func<Profile, Profile> only = null) =>
                list.Add(new Bench.Experiment(name, () =>
                {
                    _forced = preset;
                    _profile = Profile.For(preset, _custom);
                    if (only != null) _profile = only(_profile);
                    AudioParking.SetEnabled(_profile.ParkIdleAudio);
                    ShadowCulling.Configure(_profile.ShadowCullFactor, _profile.ShadowCullMinDistance);
                    RenderTweaks.ApplyShadows(_profile);
                    RenderTweaks.ApplyAntiAliasing(_profile.TaaToSmaa);
                }, () => { _forced = Preset.Off; RenderTweaks.RestoreTextureCameras(); Reconfigure(); }));
            Add("PRESET Balanced", Preset.Balanced);
            Add("PRESET MaxPerformance", Preset.MaxPerformance);
            Add("  only idle audio parking", Preset.Balanced, b => new Profile { ParkIdleAudio = true });
            Add("  only small shadow culling (20/8)", Preset.Balanced, b => new Profile { ShadowCullFactor = 20, ShadowCullMinDistance = 8 });
            Add("  only 2 cascades @4096", Preset.Balanced, b => new Profile { ShadowCascades = 2, ShadowResolution = 4096 });
            Add("  only texture cameras 30 fps", Preset.Balanced, b => new Profile { TextureCameraFps = 30 });
            Add("  only TAA -> SMAA", Preset.MaxPerformance, b => new Profile { TaaToSmaa = true });
            return list;
        }

        private void StartProfile(float seconds, bool scripts)
        {
            FinishProfile();
            LoopTimer.Start();
            FrameStats.HookCameras(true);
            FrameStats.TakeCameraReport(1);
            if (scripts) ScriptProfiler.Start();
            _profileFrames = 0;
            _profileUntil = Time.realtimeSinceStartup + seconds;
        }

        private void FinishProfile()
        {
            if (_profileUntil < 0) return;
            _profileUntil = -1;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Profile {DateTime.Now:HH:mm:ss} preset {CurrentPreset}, {_profileFrames} frames, avg {FrameStats.AverageMs(Math.Min(_profileFrames, FrameStats.Window)):F2} ms; " +
                          $"draws {FrameStats.DrawCalls} batches {FrameStats.Batches} setpass {FrameStats.SetPass} tris {FrameStats.Triangles} shadowCasters {FrameStats.ShadowCasters}; " +
                          $"{AudioParking.Stats}, {ShadowCulling.CulledCount} shadows culled, {CursorFilter.Skipped} WM_SETCURSOR skipped, {PropProbes.Swapped} props on scene probes, {ExteriorShadows.Count} exterior shadows off");
            LoopTimer.Stop();
            sb.Append(LoopTimer.Report(TurnProbe.BucketNames, TurnProbe.ExtraNames));
            if (LoopTimer.HitchMs > 0) { sb.Append(LoopTimer.HitchReport()); LoopTimer.HitchMs = 0; }
            StackSampler.Stop();
            sb.Append(StackSampler.Report(TurnProbe.BucketNames));
            TurnProbe.HookInput(false);
            sb.AppendLine("Camera render CPU time:");
            foreach (var l in FrameStats.TakeCameraReport(_profileFrames)) sb.AppendLine(l);
            FrameStats.HookCameras(false);
            if (ScriptProfiler.Running)
            {
                ScriptProfiler.Stop();
                sb.Append(ScriptProfiler.Report());
            }
            Write(sb.ToString());
        }

        private void Write(string text)
        {
            try { File.AppendAllText(_reportFile, text + "\n"); } catch (Exception e) { Log.LogWarning("Report: " + e.Message); }
            Log.LogInfo("Report written (" + text.Length + " chars).");
        }

        private void OnDestroy()
        {
            _bench?.Abort();
            TurnProbe.AbortSweep();
            CursorAB.Abort();
            TurnProbe.HookInput(false);
            StackSampler.Stop();
            LoopTimer.Stop();
            ScriptProfiler.Stop();
            FrameStats.Shutdown();
            _harmony?.UnpatchSelf();
            AudioParking.SetEnabled(false);
            ShadowCulling.Configure(0, 0);
            RenderTweaks.RestoreTextureCameras();
            RenderTweaks.RestoreShadows();
            RenderTweaks.RestoreAntiAliasing();
            FrameLimiter.Release();
            CursorFilter.SetEnabled(false);
            GapTimer.Set(false);
            SetConsole(true);
            PropProbes.SetEnabled(false);
            ExteriorShadows.SetMode(0);
            StaticMerge.Configure(0);
            _overlay?.Dispose();
            if (_hooks != null) Destroy(_hooks);
            if (Instance == this) Instance = null;
        }
    }
}

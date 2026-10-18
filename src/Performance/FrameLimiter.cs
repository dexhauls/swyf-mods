using UnityEngine;

namespace Performance
{
    /// <summary>
    /// Caps the frame rate in the main menu and while the window is in the background (the menu otherwise renders
    /// at the monitor's 280 Hz). With VSync on, Unity ignores targetFrameRate, so the cap uses a VSync interval
    /// (every 2nd–4th refresh) instead: no tearing. The game's own values come back as soon as the cap ends.
    /// </summary>
    internal static class FrameLimiter
    {
        private static bool _active;
        private static int _gameVsync, _gameTarget, _setVsync, _setTarget;

        public static bool Suspended; // a measurement runs uncapped

        public static void Tick(Profile p)
        {
            if (Suspended) return;
            int cap = 0;
            if (!Application.isFocused && p.BackgroundFps > 0) cap = p.BackgroundFps;
            else if (InMenu() && p.MenuFps > 0) cap = p.MenuFps;
            if (p.FpsLimit > 0 && (cap == 0 || p.FpsLimit < cap)) cap = p.FpsLimit;

            if (cap == 0) { Release(); return; }

            if (_active && (QualitySettings.vSyncCount != _setVsync || Application.targetFrameRate != _setTarget))
            {
                // The game (its settings menu) changed VSync / the cap meanwhile: that is the new "own" value.
                _gameVsync = QualitySettings.vSyncCount;
                _gameTarget = Application.targetFrameRate;
                _active = false;
            }
            if (!_active)
            {
                _gameVsync = QualitySettings.vSyncCount;
                _gameTarget = Application.targetFrameRate;
            }

            int vsync = _gameVsync, target = _gameTarget;
            if (_gameVsync > 0)
            {
                double hz = RefreshRate();
                int interval = Mathf.Clamp(Mathf.RoundToInt((float)(hz / cap)), 1, 4);
                vsync = Mathf.Max(_gameVsync, interval);
                // 4 refreshes still too fast (e.g. 280 Hz / 4 = 70 for a 30 fps cap): add the frame cap on top.
                if (hz / vsync > cap * 1.15) { vsync = 0; target = cap; }
            }
            else if (_gameTarget <= 0 || _gameTarget > cap) target = cap;

            if (QualitySettings.vSyncCount != vsync) QualitySettings.vSyncCount = vsync;
            if (Application.targetFrameRate != target) Application.targetFrameRate = target;
            _setVsync = vsync;
            _setTarget = target;
            _active = true;
        }

        private static double _hz;
        private static float _hzAt = -10;

        // Screen.currentResolution asks DXGI to enumerate the monitors: cheap enough every 2 s, not every frame.
        private static double RefreshRate()
        {
            if (Time.unscaledTime - _hzAt > 2f)
            {
                _hzAt = Time.unscaledTime;
                _hz = Screen.currentResolution.refreshRateRatio.value;
                if (_hz <= 0) _hz = 60;
            }
            return _hz;
        }

        public static void Release()
        {
            if (!_active) return;
            _active = false;
            if (QualitySettings.vSyncCount == _setVsync) QualitySettings.vSyncCount = _gameVsync;
            if (Application.targetFrameRate == _setTarget) Application.targetFrameRate = _gameTarget;
        }

        public static bool InMenu()
        {
            try { return MainMenuSingleton.Instance != null && Mirror.NetworkClient.localPlayer == null; } catch { return false; }
        }
    }
}

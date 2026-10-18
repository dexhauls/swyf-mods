using BepInEx.Configuration;

namespace Performance
{
    public enum Preset { Off, Balanced, MaxPerformance, Custom }

    /// <summary>What each optimisation should do. 0 / false = leave the game's own setting.</summary>
    internal sealed class Profile
    {
        public bool ParkIdleAudio;
        public float ShadowCullFactor, ShadowCullMinDistance; // factor 0 = off
        public int ShadowCascades, ShadowResolution;
        public float ShadowDistance;
        public int TextureCameraFps;
        public bool TextureCameraNoShadows;
        public bool TaaToSmaa;
        public bool SkipCursorUpdates;
        public bool DetachConsole;
        public bool PropsUseSceneProbes;
        public int ExteriorShadowsOff; // 0 game, 1 while inside the office, 2 always
        public int MenuFps, BackgroundFps, FpsLimit;

        public static Profile For(Preset preset, Plugin.CustomConfig c)
        {
            switch (preset)
            {
                case Preset.Off:
                    return new Profile();
                case Preset.Balanced:
                    // Measured to look the same: shadows of small things fade out only far away, 2 sharper cascades
                    // instead of 4 (4096 map: each texel as fine as before), the boss webcam (128x96) renders at 30 fps.
                    // The BepInEx console window shares the game thread's input queue: every mouse move then waits
                    // on conhost (turning the camera cost 3-5 ms a frame, 1.5 ms without it).
                    return new Profile
                    {
                        ParkIdleAudio = true,
                        ShadowCullFactor = 20, ShadowCullMinDistance = 8,
                        ShadowCascades = 2, ShadowResolution = 4096,
                        TextureCameraFps = 30,
                        ExteriorShadowsOff = 1,
                        DetachConsole = true,
                        SkipCursorUpdates = true,
                        MenuFps = 60,
                    };
                case Preset.MaxPerformance:
                    return new Profile
                    {
                        ParkIdleAudio = true,
                        ShadowCullFactor = 10, ShadowCullMinDistance = 5,
                        ShadowCascades = 2, ShadowResolution = 4096, ShadowDistance = 40,
                        TextureCameraFps = 15, TextureCameraNoShadows = true,
                        TaaToSmaa = true,
                        PropsUseSceneProbes = true,
                        ExteriorShadowsOff = 2,
                        DetachConsole = true,
                        SkipCursorUpdates = true,
                        MenuFps = 60, BackgroundFps = 30,
                    };
                default:
                    return new Profile
                    {
                        ParkIdleAudio = c.ParkIdleAudio.Value,
                        ShadowCullFactor = c.ShadowCulling.Value ? c.ShadowCullFactor.Value : 0,
                        ShadowCullMinDistance = c.ShadowCullMinDistance.Value,
                        ShadowCascades = c.ShadowCascades.Value,
                        ShadowResolution = c.ShadowResolution.Value,
                        ShadowDistance = c.ShadowDistance.Value,
                        TextureCameraFps = c.TextureCameraFps.Value,
                        TextureCameraNoShadows = !c.TextureCameraShadows.Value,
                        TaaToSmaa = c.TaaToSmaa.Value,
                        SkipCursorUpdates = c.SkipCursorUpdates.Value,
                        DetachConsole = c.DetachConsole.Value,
                        PropsUseSceneProbes = c.PropsUseSceneProbes.Value,
                        ExteriorShadowsOff = c.ExteriorShadowsOff.Value,
                        MenuFps = c.MenuFps.Value,
                        BackgroundFps = c.BackgroundFps.Value,
                        FpsLimit = c.FpsLimit.Value,
                    };
            }
        }

        public static string Name(Preset p) => p switch
        {
            Preset.Off => Lang.T("Vypnuto", "Off"),
            Preset.Balanced => Lang.T("Vyvážené", "Balanced"),
            Preset.MaxPerformance => Lang.T("Maximální výkon", "Max performance"),
            _ => Lang.T("Vlastní", "Custom"),
        };
    }
}

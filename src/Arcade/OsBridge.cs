using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using UnityEngine.UIElements;

namespace Arcade
{
    /// <summary>
    /// Talks to the game's desktop (KolkataOsController). Its app types are internal, so everything goes
    /// through reflection on a handful of members.
    /// </summary>
    internal static class OsBridge
    {
        internal const string AppId = "swyf-arcade";
        internal const string ContentType = "swyfArcade";
        internal static string IconPath;

        /// <summary>Desktop/window title of the app in the current game language.</summary>
        internal static string Title => Lang.T("Arkáda", "Arcade");

        private static readonly Type DefinitionType = AccessTools.TypeByName("Kolkata.UI.KolkataOsAppDefinition");
        private static readonly Type ContentTypeType = AccessTools.TypeByName("Kolkata.UI.KolkataOsAppContent");
        private static readonly Type RuntimeType = AccessTools.TypeByName("Kolkata.UI.KolkataOsAppRuntime");

        private static readonly FieldInfo AppDefinitions = AccessTools.Field(typeof(KolkataOsController), "m_appDefinitions");
        private static readonly FieldInfo DesktopInitialized = AccessTools.Field(typeof(KolkataOsController), "m_desktopInitialized");
        private static readonly MethodInfo BuildDesktop = AccessTools.Method(typeof(KolkataOsController), "BuildDesktop");
        private static readonly MethodInfo CaptureRuntimeSnapshots = AccessTools.Method(typeof(KolkataOsController), "CaptureRuntimeSnapshots");
        private static readonly MethodInfo NormalizeDefinition = AccessTools.Method(typeof(KolkataOsController), "NormalizeDefinition");

        private static readonly FieldInfo RtDefinition = AccessTools.Field(RuntimeType, "Definition");
        private static readonly FieldInfo RtState = AccessTools.Field(RuntimeType, "State");
        private static readonly FieldInfo RtWindow = AccessTools.Field(RuntimeType, "Window");
        private static readonly FieldInfo RtWindowContent = AccessTools.Field(RuntimeType, "WindowContent");
        private static readonly FieldInfo RtNeedsAutoSize = AccessTools.Field(RuntimeType, "NeedsAutoSize");
        private static readonly FieldInfo DefId = AccessTools.Field(DefinitionType, "id");
        private static readonly FieldInfo DefContentType = AccessTools.Field(DefinitionType, "contentType");
        private static readonly FieldInfo DefTitle = AccessTools.Field(DefinitionType, "title");

        // Controllers whose desktop we have touched; used to add/remove the app when the mod is hot (un)loaded.
        private static readonly List<WeakReference> Seen = new List<WeakReference>();

        internal static bool IsArcade(object runtime)
        {
            if (runtime == null || RtDefinition == null) return false;
            object def = RtDefinition.GetValue(runtime);
            return def != null && ContentType.Equals(DefContentType.GetValue(def) as string);
        }

        internal static VisualElement WindowContent(object runtime) => RtWindowContent.GetValue(runtime) as VisualElement;
        internal static VisualElement Window(object runtime) => RtWindow.GetValue(runtime) as VisualElement;
        internal static void DisableAutoSize(object runtime) => RtNeedsAutoSize.SetValue(runtime, false);

        /// <summary>0 = closed, 1 = open and visible, 2 = minimized (KolkataOsAppState).</summary>
        internal static int State(object runtime) => Convert.ToInt32(RtState.GetValue(runtime));

        /// <summary>Called right before the game builds a desktop: make sure our app is in its list.</summary>
        internal static void EnsureDefinition(KolkataOsController controller)
        {
            Remember(controller);
            if (!(AppDefinitions.GetValue(controller) is IList list)) return;
            foreach (object def in list)
                if (def != null && AppId.Equals(DefId.GetValue(def) as string))
                {
                    // Already there (e.g. a rebuild after a language change): keep its title in the current language.
                    DefTitle?.SetValue(def, Title);
                    return;
                }
            list.Add(CreateDefinition(controller));
            Plugin.Log.LogInfo($"Arkáda added to the computer desktop ({controller.name}).");
        }

        private static void RemoveDefinition(KolkataOsController controller)
        {
            if (!(AppDefinitions.GetValue(controller) is IList list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && AppId.Equals(DefId.GetValue(list[i]) as string)) list.RemoveAt(i);
        }

        private static object CreateDefinition(KolkataOsController controller)
        {
            object def = Activator.CreateInstance(DefinitionType, true);
            void Set(string name, object value) => AccessTools.Field(DefinitionType, name).SetValue(def, value);
            Set("id", AppId);
            Set("title", Title);
            Set("contentType", ContentType);
            Set("iconResource", "KolkataOS/SVG/trailer-gamepad-variant");
            Set("iconSvg", "svg/trailer-gamepad-variant.svg");
            if (!string.IsNullOrEmpty(IconPath)) Set("iconPath", IconPath);
            Set("showOnDesktop", true);
            Set("desktopOrder", 19);
            Set("windowLeft", 260f);
            Set("windowTop", 50f);
            Set("windowWidth", 620f);
            Set("windowHeight", 640f);
            Set("minWindowWidth", 420f);
            Set("minWindowHeight", 460f);
            Set("accentColor", "#7B2FF7");
            Set("initialState", "closed");
            Set("content", Activator.CreateInstance(ContentTypeType, true));
            NormalizeDefinition?.Invoke(controller, new[] { def });
            return def;
        }

        /// <summary>
        /// Hot load / unload: add or remove the app on desktops that are already showing. The desktop is rebuilt
        /// the same way the game rebuilds it after being disabled, keeping open windows where they were.
        /// </summary>
        internal static void ApplyToLiveDesktops(bool add)
        {
            var controllers = LiveControllers();
            Plugin.Log.LogInfo($"Computers found: {controllers.Count}.");

            foreach (var controller in controllers)
            {
                try
                {
                    if (add) EnsureDefinition(controller); else RemoveDefinition(controller);
                    if (Rebuild(controller))
                        Plugin.Log.LogInfo(add ? "Desktop refreshed: Arkáda is on it now." : "Desktop refreshed: Arkáda removed.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Could not refresh the desktop (the app shows up after the next round/lobby): " + (e.InnerException ?? e).Message);
                }
            }
        }

        /// <summary>
        /// The game language changed: put the app title in the new language on every desktop that has the app
        /// and rebuild those desktops so the icon label and window title update.
        /// </summary>
        internal static void ApplyLanguage()
        {
            foreach (var controller in LiveControllers())
            {
                try
                {
                    if (!(AppDefinitions.GetValue(controller) is IList list)) continue;
                    bool has = false;
                    foreach (object def in list)
                        if (def != null && AppId.Equals(DefId.GetValue(def) as string)) { DefTitle?.SetValue(def, Title); has = true; }
                    if (has) Rebuild(controller);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Could not update the app title after a language change: " + (e.InnerException ?? e).Message);
                }
            }
        }

        /// <summary>Rebuilds a showing desktop the way the game does after being disabled. False = not showing.</summary>
        private static bool Rebuild(KolkataOsController controller)
        {
            if (!(DesktopInitialized.GetValue(controller) is bool ready) || !ready || !controller.isActiveAndEnabled) return false;
            object snapshots = CaptureRuntimeSnapshots.Invoke(controller, null);
            BuildDesktop.Invoke(controller, new[] { snapshots });
            return true;
        }

        private static HashSet<KolkataOsController> LiveControllers()
        {
            var controllers = new HashSet<KolkataOsController>();
            foreach (var w in Seen)
                if (w.Target is KolkataOsController c && c != null) controllers.Add(c);
            var main = KolkataOsSingleton.Instance != null ? KolkataOsSingleton.Instance.Controller : null;
            if (main != null) controllers.Add(main);
            foreach (var c in UnityEngine.Resources.FindObjectsOfTypeAll<KolkataOsController>())
                if (c != null && c.gameObject.scene.IsValid()) controllers.Add(c);
            return controllers;
        }

        private static void Remember(KolkataOsController controller)
        {
            Seen.RemoveAll(w => !(w.Target is KolkataOsController c) || c == null);
            foreach (var w in Seen)
                if (ReferenceEquals(w.Target, controller)) return;
            Seen.Add(new WeakReference(controller));
        }
    }

    [HarmonyPatch(typeof(KolkataOsController), "BuildDesktop")]
    internal static class BuildDesktopPatch
    {
        private static void Prefix(KolkataOsController __instance)
        {
            try { OsBridge.EnsureDefinition(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Could not add Arkáda to the desktop: " + e); }
        }
    }

    // The game builds a window's content in a chain of TryBuildX calls; the Tic-Tac-Toe one is a convenient
    // place to claim our content type before it falls through to the generic builder.
    [HarmonyPatch(typeof(KolkataOsController), "TryBuildTicTacToe")]
    internal static class BuildContentPatch
    {
        private static bool Prefix(object app, ref bool __result)
        {
            if (!OsBridge.IsArcade(app)) return true;
            try { ArcadeView.Build(app); }
            catch (Exception e) { Plugin.Log.LogError("Arkáda could not open: " + e); }
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(KolkataOsController), "RefreshTicTacToe")]
    internal static class StateChangedPatch
    {
        private static void Prefix(object app)
        {
            if (OsBridge.IsArcade(app)) ArcadeView.StateChanged(app);
        }
    }

    // While a game runs in a focused Arkáda window, the game's own keybinds stay quiet (arrows/WASD/space would
    // otherwise fart, wobble the head, toggle cameras...). Leaving the computer and clicking still work.
    [HarmonyPatch]
    internal static class MuteGameKeysWhilePlaying
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameInputBindings), nameof(GameInputBindings.Pressed));
            yield return AccessTools.Method(typeof(GameInputBindings), nameof(GameInputBindings.Held));
            yield return AccessTools.Method(typeof(GameInputBindings), nameof(GameInputBindings.Released));
        }

        private static bool Prefix(string map, string action, ref bool __result)
        {
            if (!ArcadeView.CapturesKeys || Allowed(map, action)) return true;
            __result = false;
            return false;
        }

        internal static bool Allowed(string map, string action)
        {
            if (map == "Controller") return true;
            switch (action)
            {
                case "MonitorEscape":
                case "PauseMenu":
                case "MonitorLeftClick":
                case "MonitorRightClick":
                case "MonitorMiddleClick":
                case "Look":
                    return true;
                default:
                    return false;
            }
        }
    }

    [HarmonyPatch(typeof(GameInputBindings), nameof(GameInputBindings.Axis))]
    internal static class MuteGameAxisWhilePlaying
    {
        private static bool Prefix(string map, string action, ref float __result)
        {
            if (!ArcadeView.CapturesKeys || MuteGameKeysWhilePlaying.Allowed(map, action)) return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInputBindings), nameof(GameInputBindings.Vector))]
    internal static class MuteGameVectorWhilePlaying
    {
        private static bool Prefix(string map, string action, ref UnityEngine.Vector2 __result)
        {
            if (!ArcadeView.CapturesKeys || MuteGameKeysWhilePlaying.Allowed(map, action)) return true;
            __result = UnityEngine.Vector2.zero;
            return false;
        }
    }
}

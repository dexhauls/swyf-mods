using HarmonyLib;
using UnityEngine;

namespace DevMenuUnlock
{
    // The game's menu: window chrome and the three drawing helpers.

    [HarmonyPatch(typeof(NetworkingDevMenu), "OnGUI")]
    internal static class DevMenuOnGuiPatch
    {
        private static bool _failed;

        private static bool Prefix(NetworkingDevMenu __instance)
        {
            if (!Plugin.ModernUi.Value || _failed) return true;
            try
            {
                ModernDevMenu.OnGUI(__instance);
            }
            catch (System.Exception e) when (!(e is ExitGUIException))
            {
                // Never leave the player without a menu: fall back to the game's own look.
                _failed = true;
                Plugin.Log.LogError($"Modern menu failed, using the game's original menu instead: {e}");
                return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkingDevMenu), "DrawSectionHeader")]
    internal static class SectionHeaderPatch
    {
        private static bool Prefix(string title)
        {
            if (!ModernDevMenu.InMenu) return true;
            ModernDevMenu.SectionHeader(title);
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkingDevMenu), "DrawMenuButton")]
    internal static class MenuButtonPatch
    {
        private static bool Prefix(string text, ref bool __result)
        {
            if (!ModernDevMenu.InMenu) return true;
            __result = ModernDevMenu.MenuButton(text);
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkingDevMenu), "DrawDisabledMenuButton")]
    internal static class DisabledButtonPatch
    {
        private static bool Prefix(string text, ref bool __result)
        {
            if (!ModernDevMenu.InMenu) return true;
            ModernDevMenu.DisabledButton(text);
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkingDevMenu), "DrawToggleRow")]
    internal static class ToggleRowPatch
    {
        private static bool Prefix(string label, string value, ref bool __result)
        {
            if (!ModernDevMenu.InMenu) return true;
            __result = ModernDevMenu.ToggleRow(label, value);
            return false;
        }
    }

    // While you type into the menu's search box, the game must not react to the keys. All of its
    // keybinds go through GameInputBindings (the same gate it uses while you rebind keys).

    [HarmonyPatch]
    internal static class MuteGameKeysWhileTyping
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameInputBindings), nameof(GameInputBindings.Pressed));
            yield return AccessTools.Method(typeof(GameInputBindings), nameof(GameInputBindings.Held));
            yield return AccessTools.Method(typeof(GameInputBindings), nameof(GameInputBindings.Released));
        }

        private static bool Prefix(ref bool __result)
        {
            if (!ModernDevMenu.IsTyping) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInputBindings), nameof(GameInputBindings.Axis))]
    internal static class MuteGameAxisWhileTyping
    {
        private static bool Prefix(ref float __result)
        {
            if (!ModernDevMenu.IsTyping) return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInputBindings), nameof(GameInputBindings.Vector))]
    internal static class MuteGameVectorWhileTyping
    {
        private static bool Prefix(ref Vector2 __result)
        {
            if (!ModernDevMenu.IsTyping) return true;
            __result = Vector2.zero;
            return false;
        }
    }

    // Raw GUILayout calls the menu makes between helpers (hints, text fields, the spawn list). Only active
    // while the menu is drawing: hidden in collapsed sections / during search, otherwise translated.

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.Label), typeof(string), typeof(GUILayoutOption[]))]
    internal static class LabelPatch
    {
        private static bool Prefix(ref string text)
        {
            if (!ModernDevMenu.InMenu) return true;
            if (ModernDevMenu.HideMisc()) return false;
            text = MenuLabels.Text(text);
            return true;
        }
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.Label), typeof(string), typeof(GUIStyle), typeof(GUILayoutOption[]))]
    internal static class LabelStylePatch
    {
        private static bool Prefix(ref string text, GUIStyle style)
        {
            if (!ModernDevMenu.InMenu) return true;
            if (!ModernDevMenu.IsFeedback(style) && ModernDevMenu.HideMisc()) return false;
            text = MenuLabels.Text(text);
            return true;
        }
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.TextField), typeof(string), typeof(int), typeof(GUILayoutOption[]))]
    internal static class TextFieldPatch
    {
        private static bool Prefix(string text, ref string __result)
        {
            if (!ModernDevMenu.HideMisc()) return true;
            __result = text;
            return false;
        }
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.TextField), typeof(string), typeof(int), typeof(GUIStyle), typeof(GUILayoutOption[]))]
    internal static class TextFieldLengthStylePatch
    {
        private static bool Prefix(string text, ref string __result)
        {
            if (!ModernDevMenu.HideMisc()) return true;
            __result = text;
            return false;
        }
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.TextField), typeof(string), typeof(GUIStyle), typeof(GUILayoutOption[]))]
    internal static class TextFieldStylePatch
    {
        private static bool Prefix(string text, ref string __result)
        {
            if (!ModernDevMenu.HideMisc()) return true;
            __result = text;
            return false;
        }
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.Button), typeof(string), typeof(GUIStyle), typeof(GUILayoutOption[]))]
    internal static class ButtonPatch
    {
        private static bool Prefix(ref string text, ref bool __result)
        {
            if (!ModernDevMenu.InMenu) return true;
            if (ModernDevMenu.HideMisc())
            {
                __result = false;
                return false;
            }
            text = MenuLabels.Text(text);
            return true;
        }
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.Space), typeof(float))]
    internal static class SpacePatch
    {
        private static bool Prefix() => !ModernDevMenu.HideMisc();
    }

    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.BeginScrollView), typeof(Vector2), typeof(GUIStyle), typeof(GUILayoutOption[]))]
    internal static class ScrollViewPatch
    {
        // The spawn list's own scroll view; collapse it to zero height instead of skipping it,
        // so the matching EndScrollView stays balanced.
        private static void Prefix(ref GUILayoutOption[] options)
        {
            var height = ModernDevMenu.ScrollViewHeight();
            if (height.HasValue) options = new[] { GUILayout.Height(height.Value) };
        }
    }
}

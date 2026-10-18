using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DevMenuUnlock
{
    /// <summary>
    /// Redraws the game's IMGUI developer menu with a modern look. The game's own DrawMenu still runs and
    /// decides what is shown and what each button does; we only replace the window chrome and the
    /// DrawSectionHeader / DrawMenuButton / DrawToggleRow helpers, and hide raw GUILayout calls that
    /// belong to collapsed sections or don't match the search.
    ///
    /// IMGUI calls OnGUI several times per frame (Layout, then input/Repaint) and the set of controls must
    /// be identical across them, so search text and collapse changes are only applied on Layout.
    ///
    /// The game's IMGUI module is code-stripped: only GUI.Label/Button/Box with GUIContent, GUI.Window,
    /// GUILayoutUtility.GetRect, GUIStyle.Draw and the GUILayout calls the game itself uses exist.
    /// </summary>
    internal static class ModernDevMenu
    {
        private const int WindowId = 0x5EED;
        private const float WindowWidth = 446f;

        private static readonly AccessTools.FieldRef<NetworkingDevMenu, bool> Visible =
            AccessTools.FieldRefAccess<NetworkingDevMenu, bool>("isMenuVisible");
        private static readonly AccessTools.FieldRef<NetworkingDevMenu, bool> CanUse =
            AccessTools.FieldRefAccess<NetworkingDevMenu, bool>("canUseDevMenu");
        private static readonly AccessTools.FieldRef<NetworkingDevMenu, Rect> MenuRect =
            AccessTools.FieldRefAccess<NetworkingDevMenu, Rect>("menuRect");
        private static readonly Action<NetworkingDevMenu, int> DrawMenu =
            AccessTools.MethodDelegate<Action<NetworkingDevMenu, int>>(AccessTools.Method(typeof(NetworkingDevMenu), "DrawMenu"));
        private static readonly Action<NetworkingDevMenu> EnsureGameStyles =
            AccessTools.MethodDelegate<Action<NetworkingDevMenu>>(AccessTools.Method(typeof(NetworkingDevMenu), "EnsureGuiStyles"));

        internal static bool InMenu;

        /// <summary>Last frame the search field had keyboard focus (game keyboard input is muted meanwhile).</summary>
        internal static int SearchFocusedFrame = -10;

        internal static bool IsTyping => Time.frameCount - SearchFocusedFrame <= 2;

        private static NetworkingDevMenu _menu;
        private static string _section = "";
        private static string _query = "";
        private static string _pendingQuery = "";
        private static string _pendingToggle;
        private static HashSet<string> _expanded;
        private static Dictionary<string, int> _matches = new Dictionary<string, int>();
        private static Dictionary<string, int> _lastMatches = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> SwitchAnim = new Dictionary<string, float>();
        private static readonly GUIContent Content = new GUIContent();
        private static readonly GUIContent Empty = new GUIContent();

        // ---------- Theme ----------
        private static readonly Color Panel = Hex(0x111318, 0.97f);
        private static readonly Color Surface = Hex(0x1B1E26);
        private static readonly Color SurfaceHover = Hex(0x252935);
        private static readonly Color SurfacePressed = Hex(0x2E3342);
        private static readonly Color Stroke = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color Text = Hex(0xE9EBF1);
        private static readonly Color TextMuted = Hex(0x8B91A1);
        private static readonly Color Accent = Hex(0xFFC23D);
        private static readonly Color AccentDark = Hex(0x1C1505);
        private static readonly Color Danger = Hex(0xFF6B6B);
        private static readonly Color TrackOff = Hex(0x3A3F4C);

        private static bool _ready;
        private static GUISkin _skin;
        private static GUIStyle _none, _panel, _shadow, _row, _rowDisabled, _rowText, _rowTextMuted, _money, _header,
            _headerText, _title, _subtitle, _tile, _iconButton, _searchBox, _searchText, _placeholder, _label,
            _textField, _feedback, _scroll, _smallButton, _pill, _badge;
        private static Texture2D _trackOn, _trackOff, _knob, _panelTexture;

        // ---------- Entry point (called from the OnGUI patch) ----------

        internal static void OnGUI(NetworkingDevMenu menu)
        {
            if (!Visible(menu) || !CanUse(menu)) return;
            EnsureGameStyles(menu);
            EnsureReady();
            AssignGameStyles(menu);

            var scale = Plugin.UiScale.Value > 0f ? Plugin.UiScale.Value : Mathf.Clamp(Screen.height / 1080f, 1f, 2f);
            var oldMatrix = GUI.matrix;
            var oldSkin = GUI.skin;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            var sw = Screen.width / scale;
            var sh = Screen.height / scale;
            ref var rect = ref MenuRect(menu);
            rect.width = WindowWidth;
            rect.height = Mathf.Min(780f, sh - 40f);
            rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, sw - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, sh - rect.height));

            if (Event.current.type == EventType.Repaint)
                _shadow.Draw(new Rect(rect.x - 22f, rect.y - 14f, rect.width + 44f, rect.height + 44f), false, false, false, false);

            _menu = menu;
            GUI.skin = _skin;
            rect = GUI.Window(WindowId, rect, DrawWindow, Empty, _none);
            GUI.skin = oldSkin;
            GUI.matrix = oldMatrix;
        }

        private static void DrawWindow(int id)
        {
            var menu = _menu;
            if (menu == null) return;
            if (Event.current.type == EventType.Layout) ApplyPending();

            var rect = MenuRect(menu);
            if (Event.current.type == EventType.Repaint)
                _panel.Draw(new Rect(0f, 0f, rect.width, rect.height), false, false, false, false);

            GUILayout.BeginArea(new Rect(16f, 14f, rect.width - 32f, rect.height - 28f));
            try
            {
                DrawHeader(menu);
                DrawSearch();
                GUILayout.Space(8f);

                _section = "";
                InMenu = true;
                DrawMenu(menu, id);
            }
            finally
            {
                InMenu = false;
                GUILayout.EndArea();
            }
        }

        // ---------- Window chrome ----------

        private static void DrawHeader(NetworkingDevMenu menu)
        {
            var r = GUILayoutUtility.GetRect(10f, 44f, _none, GUILayout.ExpandWidth(true));
            var tile = new Rect(r.x, r.y + 2f, 40f, 40f);
            if (Event.current.type == EventType.Repaint) _tile.Draw(tile, false, false, false, false);
            IconAtlas.Draw(tile, MenuLabels.IconSettings, AccentDark, 22f);

            Label(new Rect(r.x + 52f, r.y + 1f, r.width - 100f, 24f), "Dev Menu", _title);
            Label(new Rect(r.x + 52f, r.y + 24f, r.width - 100f, 18f),
                Lang.T($"Hostuješ  ·  {Plugin.ToggleKeyName} zavře", $"Hosting  ·  {Plugin.ToggleKeyName} to close"), _subtitle);

            var close = new Rect(r.xMax - 34f, r.y + 5f, 34f, 34f);
            if (Clicked(close, "#close", _iconButton)) Visible(menu) = false;
            IconAtlas.Draw(close, MenuLabels.IconClose, TextMuted, 14f);
        }

        private static void DrawSearch()
        {
            GUILayout.Space(10f);
            // Background row first, then pull the layout back up and lay the text field over it.
            var r = GUILayoutUtility.GetRect(10f, 36f, _none, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) _searchBox.Draw(r, false, false, false, false);
            IconAtlas.Draw(new Rect(r.x + 8f, r.y, 24f, r.height), MenuLabels.IconSearch, TextMuted, 16f);
            GUILayout.Space(-36f);

            GUILayout.BeginHorizontal(GUILayout.Height(36f));
            GUILayout.Space(38f);
            var fieldId = GUIUtility.GetControlID(FocusType.Passive) + 1; // the text field takes the next id
            var focused = GUIUtility.keyboardControl == fieldId;
            if (focused) SearchFocusedFrame = Time.frameCount;
            if (focused && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _pendingQuery = "";
                GUIUtility.keyboardControl = 0;
            }
            _pendingQuery = GUILayout.TextField(_pendingQuery ?? "", 40, _searchText, GUILayout.Height(36f), GUILayout.ExpandWidth(true));
            GUILayout.Space(36f);
            GUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_pendingQuery) && !focused)
                Label(new Rect(r.x + 38f, r.y, r.width - 76f, r.height), Lang.T("Hledat funkci…", "Search…"), _placeholder);

            if (_query.Length > 0)
            {
                var clear = new Rect(r.xMax - 34f, r.y + 2f, 32f, 32f);
                if (Clicked(clear, "#clear", _iconButton)) _pendingQuery = "";
                IconAtlas.Draw(clear, MenuLabels.IconClose, TextMuted, 12f);
            }
        }

        // ---------- Replacements for the game's helpers ----------

        internal static void SectionHeader(string title)
        {
            _section = title ?? "";
            _lastMatches.TryGetValue(_section, out var n);
            if (_query.Length > 0 && n == 0) return;

            GUILayout.Space(6f);
            var r = GUILayoutUtility.GetRect(10f, 30f, _none, GUILayout.ExpandWidth(true));
            if (Clicked(r, "#section/" + _section, _header) && _query.Length == 0) _pendingToggle = _section;

            DrawIcon(new Rect(r.x + 6f, r.y, 22f, r.height), MenuLabels.SectionIcon(_section), Accent, 16f);
            Label(new Rect(r.x + 34f, r.y, r.width - 80f, r.height), MenuLabels.Section(_section).ToUpperInvariant(), _headerText);

            if (_query.Length > 0)
                Label(new Rect(r.xMax - 40f, r.y + 6f, 32f, 18f), n.ToString(), _badge);
            else
                IconAtlas.Draw(new Rect(r.xMax - 28f, r.y, 22f, r.height),
                    IsCollapsed(_section) ? MenuLabels.IconChevronRight : MenuLabels.IconChevronDown, TextMuted, 12f);
        }

        internal static bool MenuButton(string text)
        {
            if (HideRow(text)) return false;
            var selected = text.StartsWith("Selected: ");
            var label = selected ? text.Substring(10) : text.StartsWith("Use ") ? text.Substring(4) : text;

            var r = RowRect();
            var clicked = Clicked(r, _section + "/" + text, _row);
            var color = MenuLabels.IsDanger(text) ? Danger : selected ? Accent : TextMuted;
            DrawIcon(new Rect(r.x + 10f, r.y, 22f, r.height), MenuLabels.Icon(text, _section), color, 17f);
            Label(new Rect(r.x + 42f, r.y, r.width - 52f, r.height), MenuLabels.Text(label), _rowText);
            return clicked;
        }

        internal static void DisabledButton(string text)
        {
            if (HideRow(text)) return;
            var r = RowRect();
            if (Event.current.type == EventType.Repaint) _rowDisabled.Draw(r, false, false, false, false);
            DrawIcon(new Rect(r.x + 10f, r.y, 22f, r.height), MenuLabels.Icon(text, _section), TextMuted, 17f);
            Label(new Rect(r.x + 42f, r.y, r.width - 52f, r.height), MenuLabels.Text(text), _rowTextMuted);
        }

        internal static bool ToggleRow(string label, string value)
        {
            if (HideRow(label)) return false;
            var r = RowRect();
            var clicked = Clicked(r, _section + "/toggle/" + label, _row);
            var state = MenuLabels.ToggleState(value);
            DrawIcon(new Rect(r.x + 10f, r.y, 22f, r.height), MenuLabels.ToggleIcon(label), state == true ? Accent : TextMuted, 17f);
            Label(new Rect(r.x + 42f, r.y, r.width - 110f, r.height), MenuLabels.Text(label), _rowText);

            if (state.HasValue)
            {
                var target = state.Value ? 1f : 0f;
                if (!SwitchAnim.TryGetValue(label, out var t)) t = target;
                if (Event.current.type == EventType.Repaint)
                {
                    t = Mathf.MoveTowards(t, target, Time.unscaledDeltaTime * 8f);
                    SwitchAnim[label] = t;
                    var track = new Rect(r.xMax - 50f, r.center.y - 10f, 38f, 20f);
                    var old = GUI.color;
                    GUI.DrawTexture(track, _trackOff);
                    GUI.color = new Color(1f, 1f, 1f, t);
                    GUI.DrawTexture(track, _trackOn);
                    GUI.color = old;
                    var knob = new Rect(Mathf.Lerp(track.x + 3f, track.xMax - 17f, Smooth(t)), track.y + 3f, 14f, 14f);
                    GUI.DrawTexture(knob, _knob);
                }
            }
            else
            {
                Label(new Rect(r.xMax - 92f, r.y + 7f, 82f, r.height - 14f), value, _pill);
            }
            return clicked;
        }

        // ---------- Raw GUILayout calls made by the menu (via patches) ----------

        internal static bool HideMisc() => InMenu && (_query.Length > 0 || IsCollapsed(_section));

        /// <summary>
        /// Height for the spawn list's scroll view: 0 when hidden, fitted to the matches while searching,
        /// null to keep the game's own height.
        /// </summary>
        internal static float? ScrollViewHeight()
        {
            if (!InMenu) return null;
            if (_query.Length == 0) return IsCollapsed(_section) ? 0f : (float?)null;
            _lastMatches.TryGetValue(_section, out var n);
            return Mathf.Min(220f, n * 38f);
        }

        internal static bool IsFeedback(GUIStyle style) => style != null && style == _feedback;

        // ---------- Helpers ----------

        private static bool HideRow(string englishLabel)
        {
            if (!InMenu) return false;
            if (_query.Length == 0) return IsCollapsed(_section);

            var match = Contains(englishLabel, _query) || Contains(MenuLabels.Text(englishLabel), _query);
            if (match && Event.current.type == EventType.Layout)
            {
                _matches.TryGetValue(_section, out var n);
                _matches[_section] = n + 1;
            }
            return !match;
        }

        private static bool IsCollapsed(string section) => section.Length > 0 && !Expanded.Contains(section);

        private static HashSet<string> Expanded =>
            _expanded ?? (_expanded = new HashSet<string>(
                Plugin.ExpandedSections.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)));

        private static void ApplyPending()
        {
            _query = (_pendingQuery ?? "").Trim().ToLowerInvariant();
            _lastMatches = _matches;
            _matches = new Dictionary<string, int>();
            if (_pendingToggle == null) return;
            if (!Expanded.Remove(_pendingToggle)) Expanded.Add(_pendingToggle);
            Plugin.ExpandedSections.Value = string.Join(",", Expanded.ToArray());
            _pendingToggle = null;
        }

        private static string _pressed;

        /// <summary>
        /// Draws <paramref name="style"/> with hover/pressed states and returns true on a full click (mouse
        /// pressed and released on the same element). Deliberately doesn't use GUI.Button: that one fires on
        /// any MouseUp while its control id is hot, and ids aren't stable across this menu's changing layout.
        /// </summary>
        private static bool Clicked(Rect r, string key, GUIStyle style)
        {
            var e = Event.current;
            var inside = r.Contains(e.mousePosition);
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && inside:
                    _pressed = key;
                    e.Use();
                    break;
                case EventType.MouseUp when e.button == 0 && _pressed == key:
                    _pressed = null;
                    e.Use();
                    return inside;
                case EventType.Repaint:
                    style.Draw(r, inside, _pressed == key && inside, false, false);
                    break;
            }
            return false;
        }

        private static Rect RowRect() => GUILayoutUtility.GetRect(10f, 34f, _row, GUILayout.Height(34f), GUILayout.ExpandWidth(true));

        private static void Label(Rect r, string text, GUIStyle style)
        {
            Content.text = text;
            GUI.Label(r, Content, style);
        }

        private static void DrawIcon(Rect r, string icon, Color color, float size)
        {
            if (icon == MenuLabels.Money)
            {
                _money.normal.textColor = color;
                Label(r, "$", _money);
            }
            else
            {
                IconAtlas.Draw(r, icon, color, size);
            }
        }

        private static bool Contains(string s, string q) => s != null && s.ToLowerInvariant().Contains(q);

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        // ---------- Styles ----------

        private static void AssignGameStyles(NetworkingDevMenu menu)
        {
            // Raw GUILayout calls in the game's DrawMenu use these fields.
            SetStyle(menu, "labelStyle", _label);
            SetStyle(menu, "textFieldStyle", _textField);
            SetStyle(menu, "feedbackStyle", _feedback);
            SetStyle(menu, "scrollStyle", _scroll);
            SetStyle(menu, "buttonStyle", _smallButton);
        }

        private static void SetStyle(NetworkingDevMenu menu, string field, GUIStyle style) =>
            Traverse.Create(menu).Field(field).SetValue(style);

        private static void EnsureReady()
        {
            if (_ready && _panelTexture != null) return;
            _ready = true;
            IconAtlas.Load();

            var baseSkin = GUI.skin;
            _skin = ScriptableObject.CreateInstance<GUISkin>();
            _skin.hideFlags = HideFlags.HideAndDontSave;
            _skin.font = baseSkin.font;
            _skin.box = baseSkin.box;
            _skin.button = baseSkin.button;
            _skin.toggle = baseSkin.toggle;
            _skin.window = baseSkin.window;
            _skin.textArea = baseSkin.textArea;
            _skin.horizontalSlider = baseSkin.horizontalSlider;
            _skin.horizontalSliderThumb = baseSkin.horizontalSliderThumb;
            _skin.verticalSlider = baseSkin.verticalSlider;
            _skin.verticalSliderThumb = baseSkin.verticalSliderThumb;
            _skin.horizontalScrollbar = baseSkin.horizontalScrollbar;
            _skin.horizontalScrollbarThumb = baseSkin.horizontalScrollbarThumb;
            _skin.horizontalScrollbarLeftButton = baseSkin.horizontalScrollbarLeftButton;
            _skin.horizontalScrollbarRightButton = baseSkin.horizontalScrollbarRightButton;
            _skin.scrollView = baseSkin.scrollView;
            _skin.customStyles = baseSkin.customStyles;

            _none = new GUIStyle();

            _panelTexture = Rounded(14, Panel, Stroke, 1f);
            _panel = Box(_panelTexture, 14);
            _shadow = Box(Shadow(18, 22, 0.55f), 41);
            _tile = Box(Rounded(10, Accent), 10);
            _searchBox = Box(Rounded(9, Surface, Stroke, 1f), 9);

            _row = Box(Rounded(8, Surface), 8);
            _row.hover.background = Rounded(8, SurfaceHover);
            _row.active.background = Rounded(8, SurfacePressed);
            _row.margin = new RectOffset(0, 0, 2, 2);
            _rowDisabled = Box(Rounded(8, new Color(Surface.r, Surface.g, Surface.b, 0.5f)), 8);

            _header = Box(null, 8);
            _header.hover.background = Rounded(8, new Color(1f, 1f, 1f, 0.04f));
            _iconButton = Box(null, 8);
            _iconButton.hover.background = Rounded(8, new Color(1f, 1f, 1f, 0.07f));
            _iconButton.active.background = Rounded(8, new Color(1f, 1f, 1f, 0.12f));

            _rowText = TextStyle(13, Text, TextAnchor.MiddleLeft);
            _rowTextMuted = TextStyle(13, TextMuted, TextAnchor.MiddleLeft);
            _title = TextStyle(17, Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            _subtitle = TextStyle(12, TextMuted, TextAnchor.MiddleLeft);
            _headerText = TextStyle(11, TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            _placeholder = TextStyle(13, TextMuted, TextAnchor.MiddleLeft);
            _money = TextStyle(15, TextMuted, TextAnchor.MiddleCenter, FontStyle.Bold);

            _searchText = TextStyle(13, Text, TextAnchor.MiddleLeft);
            _searchText.padding = new RectOffset(0, 0, 0, 0);

            _label = TextStyle(12, TextMuted, TextAnchor.MiddleLeft);
            _label.wordWrap = true;
            _label.padding = new RectOffset(4, 4, 4, 4);

            _textField = TextStyle(13, Text, TextAnchor.MiddleLeft);
            _textField.padding = new RectOffset(10, 10, 6, 6);
            _textField.normal.background = _textField.hover.background = _textField.active.background = Rounded(7, Surface, Stroke, 1f);
            _textField.focused.background = Rounded(7, Surface, new Color(Accent.r, Accent.g, Accent.b, 0.6f), 1f);
            _textField.border = new RectOffset(7, 7, 7, 7);

            _feedback = TextStyle(12, Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            _feedback.normal.background = Rounded(8, new Color(Accent.r, Accent.g, Accent.b, 0.12f), new Color(Accent.r, Accent.g, Accent.b, 0.35f), 1f);
            _feedback.border = new RectOffset(8, 8, 8, 8);
            _feedback.wordWrap = true;
            _feedback.padding = new RectOffset(12, 12, 8, 8);
            _feedback.margin = new RectOffset(0, 0, 4, 6);

            _smallButton = TextStyle(12, Text, TextAnchor.MiddleCenter);
            _smallButton.normal.background = Rounded(8, Surface);
            _smallButton.hover.background = Rounded(8, SurfaceHover);
            _smallButton.active.background = Rounded(8, SurfacePressed);
            _smallButton.border = new RectOffset(8, 8, 8, 8);
            _smallButton.margin = new RectOffset(4, 0, 0, 0);

            _pill = TextStyle(11, Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            _pill.normal.background = Rounded(8, SurfacePressed);
            _pill.border = new RectOffset(8, 8, 8, 8);

            _badge = TextStyle(11, AccentDark, TextAnchor.MiddleCenter, FontStyle.Bold);
            _badge.normal.background = Rounded(8, Accent);
            _badge.border = new RectOffset(8, 8, 8, 8);

            _scroll = new GUIStyle { padding = new RectOffset(0, 8, 0, 0) };

            _trackOn = Rounded(10, Accent);
            _trackOff = Rounded(10, TrackOff);
            _knob = Rounded(7, Color.white);

            // Thin scrollbar. GUIStyle.fixedWidth can't be set (stripped), so copy the default styles to keep
            // their width and draw a 6 px pill in the middle of a transparent texture instead.
            var vsb = new GUIStyle(baseSkin.verticalScrollbar) { margin = new RectOffset(2, 0, 2, 2), border = new RectOffset(5, 5, 5, 5), padding = new RectOffset(0, 0, 0, 0) };
            vsb.normal.background = ScrollPill(new Color(1f, 1f, 1f, 0.035f));
            vsb.hover.background = vsb.active.background = vsb.focused.background = null;
            var thumb = new GUIStyle(baseSkin.verticalScrollbarThumb) { border = new RectOffset(5, 5, 5, 5), padding = new RectOffset(0, 0, 0, 0) };
            thumb.normal.background = ScrollPill(Hex(0x3C4252));
            thumb.hover.background = ScrollPill(Hex(0x4C5366));
            thumb.active.background = thumb.focused.background = ScrollPill(Hex(0x5A6278));
            _skin.verticalScrollbar = vsb;
            _skin.verticalScrollbarThumb = thumb;
            _skin.verticalScrollbarUpButton = new GUIStyle();
            _skin.verticalScrollbarDownButton = new GUIStyle();
            _skin.horizontalScrollbar = new GUIStyle();
            _skin.horizontalScrollbarThumb = new GUIStyle();
            _skin.horizontalScrollbarLeftButton = new GUIStyle();
            _skin.horizontalScrollbarRightButton = new GUIStyle();
            _skin.label = _label;
            _skin.textField = _textField;
        }

        private static GUIStyle TextStyle(int size, Color color, TextAnchor anchor, FontStyle fontStyle = FontStyle.Normal)
        {
            var s = new GUIStyle { fontSize = size, alignment = anchor, fontStyle = fontStyle };
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = color;
            return s;
        }

        private static GUIStyle Box(Texture2D background, int border)
        {
            var s = new GUIStyle { border = new RectOffset(border, border, border, border) };
            s.normal.background = background;
            return s;
        }

        // ---------- Procedural textures ----------

        /// <summary>Anti-aliased rounded square, meant for 9-slicing with border = radius + 1.</summary>
        private static Texture2D Rounded(int radius, Color fill, Color? stroke = null, float strokeWidth = 0f)
        {
            var size = radius * 2 + 2;
            var half = size / 2f;
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = RoundedDistance(x + 0.5f - half, y + 0.5f - half, half, radius);
                var c = fill;
                if (stroke.HasValue && strokeWidth > 0f)
                    c = Color.Lerp(stroke.Value, fill, Mathf.Clamp01(0.5f - (d + strokeWidth)));
                c.a *= Mathf.Clamp01(0.5f - d);
                px[y * size + x] = c;
            }
            return NewTexture(size, px);
        }

        /// <summary>11x11 texture: transparent edges with a 3 px-radius pill in the middle (9-slice border 5).</summary>
        private static Texture2D ScrollPill(Color fill)
        {
            const int size = 11;
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Pill is 6 px wide (x 2.5..8.5) and fills the height; middle row/column get stretched.
                var d = RoundedDistance(x + 0.5f - 5.5f, y + 0.5f - 5.5f, 3f, 3f);
                var c = fill;
                c.a *= Mathf.Clamp01(0.5f - d);
                px[y * size + x] = c;
            }
            return NewTexture(size, px);
        }

        private static Texture2D Shadow(int radius, int blur, float alpha)
        {
            var size = (radius + blur) * 2 + 2;
            var half = size / 2f;
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = RoundedDistance(x + 0.5f - half, y + 0.5f - half, half - blur, radius);
                var t = Mathf.Clamp01((d + blur * 0.35f) / (blur * 1.35f));
                px[y * size + x] = new Color(0f, 0f, 0f, alpha * (1f - t) * (1f - t));
            }
            return NewTexture(size, px);
        }

        // Signed distance from a rounded square centred at 0 (negative inside).
        private static float RoundedDistance(float x, float y, float half, float radius)
        {
            var qx = Mathf.Abs(x) - (half - radius);
            var qy = Mathf.Abs(y) - (half - radius);
            var ox = Mathf.Max(qx, 0f);
            var oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        private static Texture2D NewTexture(int size, Color32[] pixels)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}

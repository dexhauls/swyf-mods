using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Emotes
{
    /// <summary>
    /// The radial emote picker: a dark disc with up to eight emotes around it. Moving the mouse points at one,
    /// the scroll wheel switches pages. Drawn with UI Toolkit on the game's own panel settings (fonts, scaling).
    /// </summary>
    internal sealed class EmoteWheel : IDisposable
    {
        public static bool IsOpen { get; private set; }

        private const float Deg2Rad = 3.14159265f / 180f; // Mathf.Deg2Rad is stripped from the game's UnityEngine
        private const float Disc = 480, Radius = 172, ItemW = 136, ItemH = 46, Dead = 28, Reach = 120;

        private static readonly Color DiscColor = new Color(0.05f, 0.06f, 0.12f, 0.82f);
        private static readonly Color ItemColor = new Color(0.12f, 0.14f, 0.25f, 0.95f);
        private static readonly Color ItemHot = new Color(0.48f, 0.18f, 0.97f, 1f);
        private static readonly Color Ink = new Color(0.94f, 0.95f, 1f);
        private static readonly Color Muted = new Color(0.62f, 0.66f, 0.8f);

        private readonly GameObject _go;
        private readonly UIDocument _doc;
        private readonly PanelSettings _panel;
        private VisualElement _root, _disc, _pointer;
        private Label _name, _hint;
        private VisualElement _tabBar;
        private readonly List<Label> _tabs = new List<Label>();
        private readonly List<VisualElement> _items = new List<VisualElement>();
        private readonly List<EmoteEntry> _shown = new List<EmoteEntry>();

        private int _page, _selected = -1;
        private Vector2 _cursor;

        public EmoteWheel()
        {
            _go = new GameObject("Emoty wheel") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_go);
            var source = Resources.Load<PanelSettings>("Announcements/AnnouncementPanelSettings");
            if (source == null)
                foreach (var p in Resources.FindObjectsOfTypeAll<PanelSettings>())
                    if (p != null && p.themeStyleSheet != null) { source = p; break; }
            if (source == null) throw new InvalidOperationException("no UI panel settings found in the game");
            _panel = UnityEngine.Object.Instantiate(source);
            _panel.sortingOrder = 90;
            _doc = _go.AddComponent<UIDocument>();
            _doc.panelSettings = _panel;
            Build();
            SetVisible(false);
        }

        private void Build()
        {
            _root = _doc.rootVisualElement;
            _root.pickingMode = PickingMode.Ignore;
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.right = 0;
            _root.style.top = 0;
            _root.style.bottom = 0;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _disc = new VisualElement { pickingMode = PickingMode.Ignore };
            _disc.style.width = Disc;
            _disc.style.height = Disc;
            _disc.style.backgroundColor = DiscColor;
            Round(_disc, Disc / 2);
            Border(_disc, 2, new Color(1, 1, 1, 0.08f));
            _root.Add(_disc);

            // Page tabs across the top of the inner area.
            _tabBar = new VisualElement { pickingMode = PickingMode.Ignore };
            _tabBar.style.position = Position.Absolute;
            _tabBar.style.top = Disc / 2 - 78;
            _tabBar.style.left = 24;
            _tabBar.style.right = 24;
            _tabBar.style.flexDirection = FlexDirection.Row;
            _tabBar.style.flexWrap = Wrap.Wrap;
            _tabBar.style.justifyContent = Justify.Center;
            _disc.Add(_tabBar);

            _name = Text("", 22, Ink, bold: true);
            _name.style.position = Position.Absolute;
            _name.style.left = 0;
            _name.style.right = 0;
            _name.style.top = Disc / 2 - 18;
            _name.style.unityTextAlign = TextAnchor.MiddleCenter;
            _disc.Add(_name);

            _hint = Text("", 11, Muted);
            _hint.style.position = Position.Absolute;
            _hint.style.left = 0;
            _hint.style.right = 0;
            _hint.style.top = Disc / 2 + 18;
            _hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            _disc.Add(_hint);

            _pointer = new VisualElement { pickingMode = PickingMode.Ignore };
            _pointer.style.position = Position.Absolute;
            _pointer.style.width = 12;
            _pointer.style.height = 12;
            _pointer.style.backgroundColor = ItemHot;
            Round(_pointer, 6);
            _disc.Add(_pointer);
        }

        private void ShowPage(int page)
        {
            _page = (page % EmoteCatalog.Pages.Count + EmoteCatalog.Pages.Count) % EmoteCatalog.Pages.Count;
            foreach (var i in _items) i.RemoveFromHierarchy();
            _items.Clear();
            _shown.Clear();
            foreach (var e in EmoteCatalog.Pages[_page].Entries)
                if (EmoteCatalog.IsAvailable(e)) _shown.Add(e);

            for (int i = 0; i < _shown.Count; i++)
            {
                float a = AngleOf(i) * Deg2Rad;
                var item = new VisualElement { pickingMode = PickingMode.Ignore };
                item.style.position = Position.Absolute;
                item.style.width = ItemW;
                item.style.height = ItemH;
                item.style.left = Disc / 2 + Mathf.Sin(a) * Radius - ItemW / 2;
                item.style.top = Disc / 2 - Mathf.Cos(a) * Radius - ItemH / 2;
                item.style.backgroundColor = ItemColor;
                item.style.alignItems = Align.Center;
                item.style.justifyContent = Justify.Center;
                Round(item, ItemH / 2);
                var label = Text(_shown[i].Label, 15, Ink, bold: true);
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                item.Add(label);
                _disc.Add(item);
                _items.Add(item);
            }
            for (int t = 0; t < _tabs.Count; t++)
            {
                _tabs[t].style.color = t == _page ? Ink : Muted;
                // Many pages (lots of dances): show the current one and its neighbours only.
                _tabs[t].style.display = _tabs.Count <= 4 || Mathf.Abs(t - _page) <= 1 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            Select(-1);
        }

        private float AngleOf(int i) => _shown.Count == 0 ? 0 : 360f / _shown.Count * i;

        /// <summary>Page titles in the current language. With many pages only the neighbours of the current one show.</summary>
        private void BuildTabs()
        {
            _tabBar.Clear();
            _tabs.Clear();
            for (int p = 0; p < EmoteCatalog.Pages.Count; p++)
            {
                var t = Text(EmoteCatalog.Pages[p].Title, 12, Muted, bold: true);
                t.style.marginLeft = 5;
                t.style.marginRight = 5;
                _tabBar.Add(t);
                _tabs.Add(t);
            }
        }

        public void Open()
        {
            IsOpen = true;
            _cursor = Vector2.zero;
            _hint.text = Lang.T("Pusť B = zahrát · kolečko myši = další stránka", "Release B = play · mouse wheel = next page");
            BuildTabs();
            ShowPage(_page);
            SetVisible(true);
        }

        /// <summary>Closes the wheel and returns the emote it pointed at (null = none).</summary>
        public EmoteEntry Close()
        {
            IsOpen = false;
            SetVisible(false);
            return _selected >= 0 && _selected < _shown.Count ? _shown[_selected] : null;
        }

        public void Tick()
        {
            if (!IsOpen) return;
            var mouse = Mouse.current;
            if (mouse == null) return;

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll > 0.1f) ShowPage(_page - 1);
            else if (scroll < -0.1f) ShowPage(_page + 1);

            var d = mouse.delta.ReadValue();
            _cursor += new Vector2(d.x, -d.y) * 0.9f; // UI y grows downwards
            if (_cursor.magnitude > Reach) _cursor = _cursor.normalized * Reach;

            if (_cursor.magnitude < Dead || _shown.Count == 0) Select(-1);
            else
            {
                float ang = Mathf.Atan2(_cursor.x, -_cursor.y) * (180f / 3.14159265f); // 0 = up, clockwise
                if (ang < 0) ang += 360;
                float step = 360f / _shown.Count;
                Select(Mathf.RoundToInt(ang / step) % _shown.Count);
            }

            var dir = _cursor.magnitude < 1 ? Vector2.zero : _cursor.normalized * Mathf.Min(_cursor.magnitude, 92);
            _pointer.style.left = Disc / 2 + dir.x - 6;
            _pointer.style.top = Disc / 2 + dir.y - 6;
        }

        private void Select(int index)
        {
            if (index == _selected && _name.text != "") return;
            _selected = index;
            for (int i = 0; i < _items.Count; i++)
            {
                bool hot = i == index;
                _items[i].style.backgroundColor = hot ? ItemHot : ItemColor;
                Border(_items[i], hot ? 2 : 0, new Color(1, 1, 1, 0.55f));
            }
            _name.text = index >= 0 && index < _shown.Count
                ? _shown[index].Label + (MovableDances.IsMovable(_shown[index].Clip) ? Lang.T(" · jde chodit", " · can walk") : "")
                : EmoteCatalog.Pages[_page].Title;
        }

        private void SetVisible(bool on) => _root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

        private static Label Text(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.fontSize = size;
            l.style.color = color;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginLeft = l.style.marginRight = l.style.marginTop = l.style.marginBottom = 0;
            l.style.paddingLeft = l.style.paddingRight = l.style.paddingTop = l.style.paddingBottom = 0;
            return l;
        }

        private static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = w;
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = c;
        }

        public void Dispose()
        {
            IsOpen = false;
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
        }
    }
}

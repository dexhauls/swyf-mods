using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Metater;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emotes
{
    /// <summary>
    /// "Emoty" tab in Employee setup, laid out like the Fortnite locker: the emote wheel with 8 slots per page on top,
    /// details of the selected emote next to it, and below a searchable grid of rarity-coloured tiles.
    /// Click a slot, then a tile, to put the emote there; right-click a slot to empty it.
    /// </summary>
    internal sealed class EmoteSettingsTab
    {
        private static EmoteSettingsTab _current;

        private static readonly AccessTools.FieldRef<RandomNpcSkinPreviewUi, VisualElement> AppearancePage =
            AccessTools.FieldRefAccess<RandomNpcSkinPreviewUi, VisualElement>("appearancePage");
        private static readonly AccessTools.FieldRef<RandomNpcSkinPreviewUi, VisualElement> NamePage =
            AccessTools.FieldRefAccess<RandomNpcSkinPreviewUi, VisualElement>("namePage");
        private static readonly AccessTools.FieldRef<RandomNpcSkinPreviewUi, Button> AppearanceTab =
            AccessTools.FieldRefAccess<RandomNpcSkinPreviewUi, Button>("appearanceTab");
        private static readonly AccessTools.FieldRef<RandomNpcSkinPreviewUi, Button> NameTab =
            AccessTools.FieldRefAccess<RandomNpcSkinPreviewUi, Button>("nameTab");
        private static readonly MethodInfo BindFeedback = AccessTools.Method(typeof(RandomNpcSkinPreviewUi), "BindFeedback");
        private static readonly FieldInfo ClassListField = AccessTools.Field(typeof(VisualElement), "m_ClassList");

        private const int SlotsPerPage = 8;
        private const float Ring = 226, SlotSize = 54, RingRadius = 84;
        private const float TileW = 104, TileH = 132;

        private static readonly Color Panel = new Color(0.04f, 0.05f, 0.1f, 0.55f);
        private static readonly Color SlotEmpty = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color Gold = new Color(1f, 0.88f, 0.3f);
        private static readonly Color Ink = Color.white, Muted = new Color(1f, 1f, 1f, 0.6f);

        private enum Filter { All, Dances, Game, New, Fun, Equipped }

        private readonly RandomNpcSkinPreviewUi _ui;
        private readonly Button _tab;
        private readonly VisualElement _page, _ringEl, _grid, _detailArt, _detailIcon;
        private readonly Label _ringCenter, _detailName, _detailRarity, _detailInfo, _message;
        private readonly TextField _search;
        private readonly List<VisualElement> _slotEls = new List<VisualElement>();
        private readonly List<(EmoteEntry entry, VisualElement tile, VisualElement badge)> _tiles = new List<(EmoteEntry, VisualElement, VisualElement)>();
        private readonly Dictionary<Filter, Button> _filterButtons = new Dictionary<Filter, Button>();
        private readonly List<string> _slots;
        private int _pages, _page0, _selected = -1;
        private Filter _filter = Filter.All;
        private EmoteEntry _previewed;

        public static void Attach(RandomNpcSkinPreviewUi ui)
        {
            try { _current = new EmoteSettingsTab(ui); }
            catch (Exception e) { Plugin.Log.LogError("Could not add the Emoty tab to Employee setup: " + e); }
        }

        public static void GameTabSelected(RandomNpcSkinPreviewUi ui)
        {
            if (_current != null && _current._ui == ui) _current.Show(false);
        }

        private EmoteSettingsTab(RandomNpcSkinPreviewUi ui)
        {
            _ui = ui;
            _slots = Plugin.WheelEmotes;
            _pages = Math.Max(1, Plugin.WheelPages > 0 ? Plugin.WheelPages : (_slots.Count + SlotsPerPage - 1) / SlotsPerPage);
            Pad();

            var appearance = AppearancePage(ui);
            var nameTab = NameTab(ui);
            _tab = new Button { text = Lang.T("Emoty", "Emotes") };
            foreach (var c in Classes(nameTab)) _tab.AddToClassList(c);
            _tab.RemoveFromClassList("is-selected");
            nameTab.parent.Insert(nameTab.parent.IndexOf(nameTab) + 1, _tab);
            Feedback(_tab, () => Show(true));

            _page = new VisualElement();
            foreach (var c in Classes(appearance)) _page.AddToClassList(c);
            _page.style.display = DisplayStyle.None;
            _page.style.flexDirection = FlexDirection.Column;
            appearance.parent.Insert(appearance.parent.IndexOf(appearance) + 1, _page);

            // ---- Top: wheel + details ----
            var top = Row();
            top.style.marginBottom = 10;
            top.style.alignItems = Align.FlexStart;
            _page.Add(top);

            var wheelCol = new VisualElement();
            wheelCol.style.alignItems = Align.Center;
            top.Add(wheelCol);
            _ringEl = new VisualElement();
            _ringEl.style.width = Ring;
            _ringEl.style.height = Ring;
            _ringEl.style.backgroundColor = Panel;
            Round(_ringEl, Ring / 2);
            wheelCol.Add(_ringEl);
            // Centre of the wheel: ‹ page › like the locker's page switcher.
            var centre = Row();
            centre.style.position = Position.Absolute;
            centre.style.left = 0;
            centre.style.right = 0;
            centre.style.top = Ring / 2 - 16;
            centre.style.justifyContent = Justify.Center;
            _ringEl.Add(centre);
            centre.Add(Arrow("<", -1));
            _ringCenter = Text("", 13, Ink, true);
            _ringCenter.style.unityTextAlign = TextAnchor.MiddleCenter;
            _ringCenter.style.minWidth = 44;
            centre.Add(_ringCenter);
            centre.Add(Arrow(">", 1));
            for (int i = 0; i < SlotsPerPage; i++) _ringEl.Add(MakeSlot(i));


            var detail = new VisualElement();
            detail.style.flexGrow = 1;
            detail.style.marginLeft = 16;
            detail.style.flexDirection = FlexDirection.Row;
            top.Add(detail);
            _detailArt = new VisualElement();
            _detailArt.style.width = 116;
            _detailArt.style.height = 146;
            Round(_detailArt, 10);
            _detailArt.style.overflow = Overflow.Hidden;
            detail.Add(_detailArt);
            _detailIcon = new VisualElement { pickingMode = PickingMode.Ignore };
            _detailIcon.style.flexGrow = 1;
            _detailArt.Add(_detailIcon);
            var info = new VisualElement();
            info.style.flexGrow = 1;
            info.style.flexShrink = 1;
            info.style.marginLeft = 12;
            detail.Add(info);
            _detailRarity = Text("", 12, Muted, true);
            info.Add(_detailRarity);
            _detailName = Text("", 22, Ink, true);
            _detailName.style.whiteSpace = WhiteSpace.Normal;
            info.Add(_detailName);
            _detailInfo = Text("", 12, Muted);
            _detailInfo.style.whiteSpace = WhiteSpace.Normal;
            _detailInfo.style.marginTop = 6;
            info.Add(_detailInfo);
            var actions = Row();
            actions.style.marginTop = 10;
            actions.style.flexWrap = Wrap.Wrap;
            actions.Add(SmallButton(Lang.T("Nasadit", "Equip"), () => { if (_previewed != null) Equip(_previewed); }));
            actions.Add(SmallButton(Lang.T("Vyprázdnit slot", "Clear slot"), () => { if (_selected >= 0) { _slots[_selected] = ""; Save(); } }));
            actions.Add(SmallButton(Lang.T("Vyprázdnit stránku", "Clear page"), () => { for (int i = 0; i < SlotsPerPage; i++) _slots[_page0 * SlotsPerPage + i] = ""; Save(); }));
            actions.Add(SmallButton(Lang.T("Vyprázdnit vše", "Clear all"), () => { for (int i = 0; i < _slots.Count; i++) _slots[i] = ""; Save(); }));
            actions.Add(SmallButton(Lang.T("+ stránka", "+ page"), () => SetPages(_pages + 1)));
            actions.Add(SmallButton(Lang.T("− stránka", "− page"), () => SetPages(_pages - 1)));
            info.Add(actions);
            _message = Text("", 12, Gold);
            _message.style.marginTop = 8;
            _message.style.whiteSpace = WhiteSpace.Normal;
            info.Add(_message);

            // ---- Toolbar: search + filters ----
            var bar = Row();
            bar.style.marginBottom = 8;
            bar.style.flexWrap = Wrap.Wrap;
            _page.Add(bar);
            _search = new TextField();
            _search.style.width = 150;
            _search.style.marginRight = 6;
            _search.RegisterValueChangedCallback(_ => ApplyFilter());
            var input = _search.Q(className: "unity-base-text-field__input") ?? _search.Q(className: "unity-text-field__input");
            if (input != null)
            {
                input.style.backgroundColor = new Color(0, 0, 0, 0.45f);
                input.style.color = Ink;
                Border(input, 1, new Color(1, 1, 1, 0.25f));
                Round(input, 6);
            }
            bar.Add(Text(Lang.T("Hledat", "Search"), 12, Muted));
            bar.Add(_search);
            foreach (var (f, label) in new[] { (Filter.All, Lang.T("Vše", "All")), (Filter.Dances, Lang.T("Tanečky", "Dances")), (Filter.Game, Lang.T("Ze hry", "Game")), (Filter.New, Lang.T("Nové", "New")), (Filter.Fun, Lang.T("Blbosti", "Silly")), (Filter.Equipped, Lang.T("V kole", "On wheel")) })
            {
                var b = SmallButton(label, () => { _filter = f; ApplyFilter(); });
                _filterButtons[f] = b;
                bar.Add(b);
            }

            // ---- Grid of tiles ----
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.minHeight = 0;
            _page.Add(scroll);
            _grid = Row();
            _grid.style.flexWrap = Wrap.Wrap;
            scroll.Add(_grid);
            foreach (var e in EmoteCatalog.All) _grid.Add(MakeTile(e));

            Select(_slots.FindIndex(s => s.Length == 0));
            Refresh();
        }

        // ---------------------------------------------------------------- building blocks

        private VisualElement MakeSlot(int i)
        {
            float a = i * 360f / SlotsPerPage * 3.14159265f / 180f;
            var slot = new VisualElement();
            slot.style.position = Position.Absolute;
            slot.style.width = SlotSize;
            slot.style.height = SlotSize;
            slot.style.left = Ring / 2 + Mathf.Sin(a) * RingRadius - SlotSize / 2;
            slot.style.top = Ring / 2 - Mathf.Cos(a) * RingRadius - SlotSize / 2;
            slot.style.alignItems = Align.Center;
            slot.style.justifyContent = Justify.Center;
            Round(slot, 10);
            slot.style.overflow = Overflow.Hidden;
            var icon = new VisualElement { name = "icon", pickingMode = PickingMode.Ignore };
            icon.style.position = Position.Absolute;
            icon.style.left = 3;
            icon.style.right = 3;
            icon.style.top = 3;
            icon.style.bottom = 3;
            slot.Add(icon);
            var plus = Text("+", 22, Muted, true);
            plus.name = "plus";
            plus.pickingMode = PickingMode.Ignore;
            slot.Add(plus);
            slot.RegisterCallback<PointerDownEvent>(evt =>
            {
                int index = _page0 * SlotsPerPage + i;
                if (evt.button == 1) { _slots[index] = ""; Select(index); Save(); }
                else { Select(index); Refresh(); }
            });
            slot.RegisterCallback<PointerEnterEvent>(_ => ShowDetail(EmoteCatalog.FindById(_slots[_page0 * SlotsPerPage + i])));
            slot.RegisterCallback<PointerLeaveEvent>(_ => ShowDetail(null));
            _slotEls.Add(slot);
            return slot;
        }

        private VisualElement MakeTile(EmoteEntry e)
        {
            var rarity = LockerArt.RarityOf(e);
            var tile = new VisualElement();
            tile.style.width = TileW;
            tile.style.height = TileH;
            tile.style.marginRight = 8;
            tile.style.marginBottom = 8;
            tile.style.overflow = Overflow.Hidden;
            tile.style.backgroundImage = new StyleBackground(LockerArt.Background(rarity));
            Round(tile, 8);
            Border(tile, 2, new Color(0, 0, 0, 0));

            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.style.position = Position.Absolute;
            icon.style.left = 6;
            icon.style.right = 6;
            icon.style.top = 4;
            icon.style.height = TileW - 12;
            icon.style.backgroundImage = new StyleBackground(LockerArt.Icon(e));
            tile.Add(icon);

            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.bottom = 0;
            bar.style.height = 34;
            bar.style.backgroundColor = new Color(0, 0, 0, 0.55f);
            bar.style.borderTopWidth = 3;
            bar.style.borderTopColor = LockerArt.Accent(rarity);
            bar.style.justifyContent = Justify.Center;
            tile.Add(bar);
            var name = Text(e.Label, 11, Ink, true);
            name.pickingMode = PickingMode.Ignore;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.overflow = Overflow.Hidden;
            bar.Add(name);

            // Small badges in the corner: music, walkable.
            var badges = new VisualElement { pickingMode = PickingMode.Ignore };
            badges.style.position = Position.Absolute;
            badges.style.left = 4;
            badges.style.top = 4;
            tile.Add(badges);
            if (e.Kind == EmoteKind.Dance && DanceMusic.Has(DanceLibrary.Find(e.Clip))) badges.Add(Pill(Lang.T("HUDBA", "MUSIC"), new Color(0, 0, 0, 0.5f)));
            if (MovableDances.IsMovable(e.Clip)) badges.Add(Pill(Lang.T("CHŮZE", "WALK"), new Color(0, 0, 0, 0.5f)));
            var equipped = Pill(Lang.T("V KOLE", "EQUIPPED"), new Color(1f, 0.82f, 0.2f, 0.95f));
            equipped.Q<Label>().style.color = new Color(0.1f, 0.08f, 0);
            equipped.style.position = Position.Absolute;
            equipped.style.right = 4;
            equipped.style.top = 4;
            tile.Add(equipped);

            tile.RegisterCallback<PointerEnterEvent>(_ => { Border(tile, 2, Gold); ShowDetail(e); });
            tile.RegisterCallback<PointerLeaveEvent>(_ => { Border(tile, 2, e == _previewed ? Gold : new Color(0, 0, 0, 0)); ShowDetail(null); });
            // Click = try it on (only you see it), double-click = put it on the wheel.
            tile.RegisterCallback<ClickEvent>(evt => { if (evt.clickCount >= 2) Equip(e); else Preview(e); });
            _tiles.Add((e, tile, equipped));
            return tile;
        }

        private VisualElement Pill(string text, Color bg)
        {
            var p = new VisualElement { pickingMode = PickingMode.Ignore };
            p.style.backgroundColor = bg;
            p.style.paddingLeft = 4;
            p.style.paddingRight = 4;
            p.style.marginBottom = 2;
            p.style.alignSelf = Align.FlexStart;
            Round(p, 3);
            var l = Text(text, 9, Ink, true);
            l.pickingMode = PickingMode.Ignore;
            p.Add(l);
            return p;
        }

        // ---------------------------------------------------------------- behaviour

        private void Preview(EmoteEntry e)
        {
            var old = _previewed;
            _previewed = e;
            foreach (var (entry, tile, _) in _tiles)
                if (entry == old || entry == e) Border(tile, 2, entry == e ? Gold : new Color(0, 0, 0, 0));
            _message.text = EmotePreview.Play(e)
                ? Lang.T("Zkoušíš si ho jen ty. Dvojklik nebo Nasadit ho dá do kola.", "Only you see this preview. Double-click or Equip puts it on the wheel.")
                : Lang.T("Tenhle emote nejde ukázat v náhledu. Dvojklik nebo Nasadit ho dá do kola.", "This emote can't be previewed. Double-click or Equip puts it on the wheel.");
            ShowDetail(null);
        }

        private void Equip(EmoteEntry e)
        {
            string id = EmoteCatalog.Id(e);
            int target = _selected;
            if (target < 0) target = _slots.FindIndex(s => s.Length == 0);
            if (target < 0) { _message.text = Lang.T("Kolo je plné. Přidej stránku (+ stránka), nebo vyprázdni slot.", "The wheel is full. Add a page (+ page) or clear a slot."); return; }
            int existing = _slots.IndexOf(id);
            if (existing >= 0 && existing != target) _slots[existing] = ""; // an emote sits in one slot only
            _slots[target] = id;
            _message.text = "";
            // Next empty slot on this page, so a few clicks fill a page.
            int next = -1;
            for (int i = 0; i < SlotsPerPage && next < 0; i++)
            {
                int idx = _page0 * SlotsPerPage + i;
                if (_slots[idx].Length == 0) next = idx;
            }
            Select(next);
            Save();
        }

        private void Select(int index)
        {
            _selected = index;
            if (index >= 0) _page0 = index / SlotsPerPage;
        }

        private void SetPages(int pages)
        {
            _pages = Mathf.Clamp(pages, 1, 20);
            Pad();
            if (_page0 >= _pages) _page0 = _pages - 1;
            if (_selected >= _pages * SlotsPerPage) _selected = -1;
            Save();
        }

        private void Pad()
        {
            while (_slots.Count < _pages * SlotsPerPage) _slots.Add("");
            if (_slots.Count > _pages * SlotsPerPage) _slots.RemoveRange(_pages * SlotsPerPage, _slots.Count - _pages * SlotsPerPage);
        }

        private void Save()
        {
            Plugin.WheelEmotes = _slots;
            Plugin.WheelPages = _pages;
            EmoteCatalog.Rebuild();
            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < SlotsPerPage; i++)
            {
                int idx = _page0 * SlotsPerPage + i;
                var slot = _slotEls[i];
                var e = EmoteCatalog.FindById(_slots[idx]);
                if (e != null)
                {
                    slot.style.backgroundImage = new StyleBackground(LockerArt.Background(LockerArt.RarityOf(e)));
                    slot.Q("icon").style.backgroundImage = new StyleBackground(LockerArt.Icon(e));
                }
                else
                {
                    slot.style.backgroundImage = new StyleBackground((Texture2D)null);
                    slot.style.backgroundColor = SlotEmpty;
                    slot.Q("icon").style.backgroundImage = new StyleBackground((Texture2D)null);
                }
                slot.Q<Label>("plus").style.display = e == null ? DisplayStyle.Flex : DisplayStyle.None;
                Border(slot, idx == _selected ? 3 : 1, idx == _selected ? Gold : new Color(1, 1, 1, 0.2f));
            }
            _ringCenter.text = $"{_page0 + 1}/{_pages}";

            foreach (var (entry, tile, badge) in _tiles)
                badge.style.display = _slots.Contains(EmoteCatalog.Id(entry)) ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var kv in _filterButtons) Border(kv.Value, kv.Key == _filter ? 2 : 0, Gold);
            ShowDetail(null);
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = DanceMusic.Key(_search.value ?? "");
            foreach (var kv in _filterButtons) Border(kv.Value, kv.Key == _filter ? 2 : 0, Gold);
            foreach (var (e, tile, _) in _tiles)
            {
                bool show = q.Length == 0 || DanceMusic.Key(e.Label).Contains(q);
                switch (_filter)
                {
                    case Filter.Dances: show &= e.Kind == EmoteKind.Dance || (e.Kind == EmoteKind.Catalog && EmoteCatalog.IsDancePageClip(e.Clip)); break;
                    case Filter.Game: show &= e.Kind == EmoteKind.Catalog; break;
                    case Filter.New: show &= e.Kind == EmoteKind.Extra || e.Kind == EmoteKind.Dance; break;
                    case Filter.Fun: show &= e.Kind == EmoteKind.ButtScratch || e.Kind == EmoteKind.Fart || e.Kind == EmoteKind.HeadWobble || e.Clip == "Squat And Shit"; break;
                    case Filter.Equipped: show &= _slots.Contains(EmoteCatalog.Id(e)); break;
                }
                tile.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>Details of the hovered emote, or of the selected slot when nothing is hovered.</summary>
        private void ShowDetail(EmoteEntry hovered)
        {
            var e = hovered ?? _previewed ?? (_selected >= 0 ? EmoteCatalog.FindById(_slots[_selected]) : null);
            if (e == null)
            {
                _detailArt.style.backgroundImage = new StyleBackground(LockerArt.Background(Rarity.Common));
                _detailIcon.style.backgroundImage = new StyleBackground((Texture2D)null);
                _detailRarity.style.color = Muted;
                _detailRarity.text = _selected >= 0 ? Lang.T($"SLOT {_selected % SlotsPerPage + 1} · STRÁNKA {_selected / SlotsPerPage + 1}", $"SLOT {_selected % SlotsPerPage + 1} · PAGE {_selected / SlotsPerPage + 1}") : Lang.T("SKŘÍŇKA", "LOCKER");
                _detailName.text = _selected >= 0 ? Lang.T("Prázdný slot", "Empty slot") : Lang.T("Vyber slot v kole", "Pick a wheel slot");
                _detailInfo.text = _slots.Exists(s => s.Length > 0)
                    ? Lang.T("Klik na emote ho ukáže na postavě, dvojklik ho dá do vybraného slotu. Pravé tlačítko slot vyprázdní.", "Click an emote to try it on, double-click to put it in the selected slot. Right-click empties a slot.")
                    : Lang.T("Kolo je prázdné, takže na B ukazuje všechny emoty. Jakmile sem něco dáš, ukáže jen tvoje.", "The wheel is empty, so B shows every emote. Once you equip some, it shows only yours.");
                return;
            }
            var r = LockerArt.RarityOf(e);
            _detailArt.style.backgroundImage = new StyleBackground(LockerArt.Background(r));
            _detailIcon.style.backgroundImage = new StyleBackground(LockerArt.Icon(e));
            _detailRarity.text = LockerArt.Name(r) + " · " + EmoteCatalog.KindLabel(e).ToUpperInvariant();
            _detailRarity.style.color = LockerArt.Accent(r);
            _detailName.text = e.Label;
            var parts = new List<string>();
            if (e.Kind == EmoteKind.Dance)
            {
                var d = DanceLibrary.Find(e.Clip);
                if (d != null) parts.Add($"{d.Length:0} s" + (d.LoopStart > 0 ? Lang.T(", s úvodem", ", with intro") : "") + Lang.T(", hraje dokola", ", loops"));
                if (DanceMusic.Has(d)) parts.Add(Lang.T("s hudbou", "with music"));
            }
            if (MovableDances.IsMovable(e.Clip)) parts.Add(Lang.T("dá se v něm chodit", "you can walk in it"));
            if (e.Kind == EmoteKind.Extra || e.Kind == EmoteKind.Dance) parts.Add(Lang.T("ostatní ho uvidí, když mají mod", "others see it if they have the mod"));
            int slot = _slots.IndexOf(EmoteCatalog.Id(e));
            if (slot >= 0) parts.Add(Lang.T($"v kole: stránka {slot / SlotsPerPage + 1}, slot {slot % SlotsPerPage + 1}", $"equipped: page {slot / SlotsPerPage + 1}, slot {slot % SlotsPerPage + 1}"));
            _detailInfo.text = string.Join(" · ", parts);
        }

        private void Show(bool on)
        {
            _page.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            _tab.EnableInClassList("is-selected", on);
            if (!on) return;
            AppearancePage(_ui).style.display = DisplayStyle.None;
            NamePage(_ui).style.display = DisplayStyle.None;
            AppearanceTab(_ui).RemoveFromClassList("is-selected");
            NameTab(_ui).RemoveFromClassList("is-selected");
            Refresh();
        }

        // ---------------------------------------------------------------- helpers

        private void Feedback(Button b, Action action) => BindFeedback.Invoke(_ui, new object[] { b, action });

        private Button SmallButton(string text, Action action)
        {
            var b = new Button { text = text };
            b.style.marginLeft = 2;
            b.style.marginRight = 2;
            b.style.marginBottom = 3;
            b.style.paddingLeft = 8;
            b.style.paddingRight = 8;
            Feedback(b, action);
            return b;
        }

        private Button Arrow(string text, int dir)
        {
            var b = SmallButton(text, () => { _page0 = (_page0 + dir + _pages) % _pages; _selected = -1; Refresh(); });
            b.style.paddingLeft = 6;
            b.style.paddingRight = 6;
            return b;
        }

        // VisualElement.GetClasses() is stripped from the game; read the class list it would return.
        private static IEnumerable<string> Classes(VisualElement e)
        {
            if (ClassListField?.GetValue(e) is IEnumerable<string> list) return new List<string>(list);
            return new string[0];
        }

        private static VisualElement Row()
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row;
            r.style.alignItems = Align.Center;
            return r;
        }

        private static Label Text(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text);
            l.style.fontSize = size;
            l.style.color = color;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            return l;
        }

        private static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius = e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = w;
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = c;
        }
    }

    [HarmonyPatch(typeof(RandomNpcSkinPreviewUi), "Initialize")]
    internal static class EmployeeSetupInitPatch
    {
        private static void Postfix(RandomNpcSkinPreviewUi __instance) => EmoteSettingsTab.Attach(__instance);
    }

    [HarmonyPatch(typeof(RandomNpcSkinPreviewUi), "SelectTab")]
    internal static class EmployeeSetupTabPatch
    {
        private static void Postfix(RandomNpcSkinPreviewUi __instance) => EmoteSettingsTab.GameTabSelected(__instance);
    }
}

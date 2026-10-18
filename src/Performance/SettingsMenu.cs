using System;
using System.Collections.Generic;
using Metater;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Performance
{
    /// <summary>
    /// In-game settings window (F11): a sidebar with tabs (overview with live stats, a frame-time graph and the
    /// presets; graphics; CPU and input; FPS limits; display), every option with an icon, what it measured and
    /// whether it changes the look. Changing an option switches to the Custom preset, seeded with the values in use,
    /// so nothing jumps. Clicks are hit-tested by hand: the game's UI input doesn't reach mod panels.
    /// Icons are Segoe Fluent Icons code points written as \uEXXX (tools/GenerateIcons.ps1 renders them).
    /// </summary>
    internal sealed class SettingsMenu : MonoBehaviour
    {
        // Palette (matches the other mods: deep navy, violet accent).
        private static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color Window = new Color(0.06f, 0.07f, 0.12f, 0.97f);
        private static readonly Color Sidebar = new Color(0.035f, 0.04f, 0.075f, 1f);
        private static readonly Color Card = new Color(1f, 1f, 1f, 0.045f);
        private static readonly Color CardHot = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color Seg = new Color(0f, 0f, 0f, 0.35f);
        private static readonly Color SegHot = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color Accent = new Color(0.52f, 0.27f, 1f, 1f);
        private static readonly Color AccentHot = new Color(0.62f, 0.4f, 1f, 1f);
        private static readonly Color AccentSoft = new Color(0.52f, 0.27f, 1f, 0.18f);
        private static readonly Color Ink = new Color(0.95f, 0.96f, 1f);
        private static readonly Color Muted = new Color(0.62f, 0.66f, 0.78f);
        private static readonly Color Faint = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color Good = new Color(0.36f, 0.86f, 0.55f);
        private static readonly Color Warn = new Color(1f, 0.76f, 0.3f);
        private static readonly Color Bad = new Color(1f, 0.42f, 0.42f);

        private const float W = 1120, H = 680, Side = 230;
        private const int Bars = 96;

        /// <summary>Anything clickable: a tab, a preset card, an option, the close button.</summary>
        private sealed class Hit
        {
            public VisualElement El;
            public Func<bool> IsOn = () => false;
            public Action Pick;
            public Color Off = Seg, OffHot = SegHot, On = Accent, OnHot = AccentHot;
            public Action<bool, bool> Paint; // custom look (on, hot)
            public int Tab = -1; // only active while this tab shows (-1 = always)
        }

        public static bool IsOpen { get; private set; }

        private Plugin _plugin;
        private GameObject _go;
        private PanelSettings _panel;
        private UIDocument _doc;
        private VisualElement _root, _content;
        private readonly List<VisualElement> _pages = new List<VisualElement>();
        private readonly List<Hit> _hits = new List<Hit>();
        private readonly List<(Label label, Func<string> text)> _texts = new List<(Label, Func<string>)>();
        private Label _pageTitle, _pageSub, _presetChip;
        private Label _fps, _ms, _batches, _input, _graphNote;
        private readonly VisualElement[] _bars = new VisualElement[Bars];
        private int _tab;
        private MetaPlayerController _blocked;
        private float _nextStats;

        private static readonly (char icon, Func<string> name, Func<string> sub)[] Tabs =
        {
            ('', () => Lang.T("Přehled", "Overview"), () => Lang.T("Živý výkon a předvolby", "Live performance and presets")),
            ('', () => Lang.T("Grafika", "Graphics"), () => Lang.T("Stíny, světla, vyhlazování, kamery", "Shadows, lights, anti-aliasing, cameras")),
            ('', () => Lang.T("Procesor a ovládání", "CPU and input"), () => Lang.T("Co zdržuje hru mimo grafiku", "What slows the game outside rendering")),
            ('', () => Lang.T("Limity FPS", "FPS limits"), () => Lang.T("Kde má hra zbytečně nejet naplno", "Where the game needn't run flat out")),
            ('', () => Lang.T("Zobrazení", "Display"), () => Lang.T("Ukazatel FPS a klávesy", "FPS readout and keys")),
        };

        public static SettingsMenu Create(Plugin plugin, GameObject host)
        {
            var m = host.AddComponent<SettingsMenu>();
            m._plugin = plugin;
            m.Build();
            Lang.Watch(m.Retranslate);
            return m;
        }

        // ================= layout =================

        private void Build()
        {
            _go = new GameObject("Výkon menu") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(_go);
            var source = Resources.Load<PanelSettings>("Announcements/AnnouncementPanelSettings");
            if (source == null)
                foreach (var p in Resources.FindObjectsOfTypeAll<PanelSettings>())
                    if (p != null && p.themeStyleSheet != null) { source = p; break; }
            if (source == null) throw new InvalidOperationException("no UI panel settings found in the game");
            _panel = Instantiate(source);
            _panel.sortingOrder = 96;
            _panel.targetTexture = null;
            _doc = _go.AddComponent<UIDocument>();
            _doc.panelSettings = _panel;

            _root = _doc.rootVisualElement;
            Fill(_root);
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;
            _root.style.backgroundColor = Backdrop;
            _root.style.display = DisplayStyle.None;

            var window = Box(Window, 20);
            window.style.width = W;
            window.style.height = H;
            window.style.flexDirection = FlexDirection.Row;
            window.style.overflow = Overflow.Hidden;
            Border(window, 1, new Color(1, 1, 1, 0.09f));
            _root.Add(window);

            BuildSidebar(window);

            var main = Column();
            main.style.paddingLeft = 28; main.style.paddingRight = 24; main.style.paddingTop = 22; main.style.paddingBottom = 18;
            window.Add(main);

            var head = Row();
            head.style.alignItems = Align.FlexStart;
            head.style.marginBottom = 16;
            main.Add(head);
            var titles = Column();
            head.Add(titles);
            _pageTitle = Text("", 26, Ink, true);
            titles.Add(_pageTitle);
            _pageSub = Text("", 13, Muted);
            _pageSub.style.marginTop = 2;
            titles.Add(_pageSub);
            var close = Box(Faint, 10);
            close.style.width = 38; close.style.height = 38;
            close.style.alignItems = Align.Center; close.style.justifyContent = Justify.Center;
            close.Add(Icon('', 16, Ink));
            head.Add(close);
            _hits.Add(new Hit { El = close, Pick = Close, Off = Faint, OffHot = new Color(1f, 0.35f, 0.35f, 0.5f) });

            _content = new VisualElement { pickingMode = PickingMode.Ignore };
            _content.style.flexGrow = 1;
            main.Add(_content);

            _pages.Add(BuildOverview());
            _pages.Add(BuildGraphics());
            _pages.Add(BuildCpu());
            _pages.Add(BuildLimits());
            _pages.Add(BuildDisplay());
            foreach (var p in _pages) _content.Add(p);
            ShowTab(0);
            Retranslate();
        }

        private void BuildSidebar(VisualElement window)
        {
            var side = new VisualElement { pickingMode = PickingMode.Ignore };
            side.style.width = Side;
            side.style.backgroundColor = Sidebar;
            side.style.paddingLeft = 16; side.style.paddingRight = 16; side.style.paddingTop = 22; side.style.paddingBottom = 18;
            side.style.borderRightWidth = 1;
            side.style.borderRightColor = new Color(1, 1, 1, 0.06f);
            window.Add(side);

            var logo = Row();
            logo.style.alignItems = Align.Center;
            logo.style.marginBottom = 26;
            side.Add(logo);
            var badge = Box(Accent, 12);
            badge.style.width = 44; badge.style.height = 44;
            badge.style.alignItems = Align.Center; badge.style.justifyContent = Justify.Center;
            badge.style.marginRight = 12;
            badge.Add(Icon('', 26, Color.white));
            logo.Add(badge);
            var name = Column();
            logo.Add(name);
            var t = Text("", 20, Ink, true);
            _texts.Add((t, () => Lang.T("Výkon", "Performance")));
            name.Add(t);
            name.Add(Text("SWYF mod · " + Plugin.PluginVersion, 11, Muted));

            for (int i = 0; i < Tabs.Length; i++)
            {
                int idx = i;
                var tab = Box(Color.clear, 10);
                tab.style.flexDirection = FlexDirection.Row;
                tab.style.alignItems = Align.Center;
                tab.style.height = 44;
                tab.style.paddingLeft = 12;
                tab.style.marginBottom = 4;
                var ic = Icon(Tabs[i].icon, 20, Muted);
                ic.style.marginRight = 12;
                tab.Add(ic);
                var l = Text("", 14, Muted, true);
                _texts.Add((l, Tabs[i].name));
                tab.Add(l);
                side.Add(tab);
                _hits.Add(new Hit
                {
                    El = tab,
                    IsOn = () => _tab == idx,
                    Pick = () => ShowTab(idx),
                    Paint = (on, hot) =>
                    {
                        tab.style.backgroundColor = on ? AccentSoft : hot ? Faint : Color.clear;
                        var c = on ? Ink : hot ? Ink : Muted;
                        l.style.color = c;
                        ic.style.unityBackgroundImageTintColor = on ? AccentHot : c;
                    },
                });
            }

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1;
            side.Add(spacer);

            var chipBox = Box(Card, 12);
            chipBox.style.paddingLeft = 12; chipBox.style.paddingRight = 12; chipBox.style.paddingTop = 10; chipBox.style.paddingBottom = 10;
            side.Add(chipBox);
            var cap = Text("", 11, Muted);
            _texts.Add((cap, () => Lang.T("Předvolba", "Preset")));
            chipBox.Add(cap);
            _presetChip = Text("", 15, Ink, true);
            _presetChip.style.marginTop = 2;
            chipBox.Add(_presetChip);

            var keys = Text("", 11, Muted);
            keys.style.marginTop = 12;
            keys.style.whiteSpace = WhiteSpace.Normal;
            _texts.Add((keys, () => Lang.T("F11 / Esc zavřít · F10 FPS", "F11 / Esc close · F10 FPS")));
            side.Add(keys);
        }

        // ---------- overview ----------

        private VisualElement BuildOverview()
        {
            var page = Page();

            var stats = Row();
            stats.style.marginBottom = 12;
            page.Add(stats);
            _fps = StatCard(stats, '', () => "FPS", Good);
            _ms = StatCard(stats, '', () => Lang.T("Čas snímku", "Frame time"), Ink);
            _batches = StatCard(stats, '', () => Lang.T("Dávky kreslení", "Draw batches"), Ink);
            _input = StatCard(stats, '', () => Lang.T("Windows a myš", "Windows & mouse"), Ink, last: true);

            var graph = Box(Card, 14);
            graph.style.height = 150;
            graph.style.paddingLeft = 16; graph.style.paddingRight = 16; graph.style.paddingTop = 12; graph.style.paddingBottom = 12;
            graph.style.marginBottom = 16;
            page.Add(graph);
            var gh = Row();
            gh.style.justifyContent = Justify.SpaceBetween;
            graph.Add(gh);
            var gt = Text("", 13, Ink, true);
            _texts.Add((gt, () => Lang.T("Čas snímku – posledních " + Bars + " snímků", "Frame time – last " + Bars + " frames")));
            gh.Add(gt);
            _graphNote = Text("", 11, Muted);
            gh.Add(_graphNote);
            var area = Row();
            area.style.flexGrow = 1;
            area.style.alignItems = Align.FlexEnd;
            area.style.marginTop = 8;
            graph.Add(area);
            for (int i = 0; i < Bars; i++)
            {
                var b = new VisualElement { pickingMode = PickingMode.Ignore };
                b.style.flexGrow = 1;
                b.style.marginLeft = 1;
                b.style.height = 2;
                b.style.backgroundColor = Good;
                b.style.borderTopLeftRadius = b.style.borderTopRightRadius = 2;
                area.Add(b);
                _bars[i] = b;
            }

            var ph = Text("", 13, Muted, true);
            ph.style.marginBottom = 8;
            _texts.Add((ph, () => Lang.T("PŘEDVOLBA", "PRESET")));
            page.Add(ph);
            var row = Row();
            page.Add(row);
            PresetCard(row, Preset.Off, '', () => Lang.T("Hra přesně jako bez modu.", "The game exactly as without the mod."), () => "");
            PresetCard(row, Preset.Balanced, '', () => Lang.T("Jen úpravy, které nemění vzhled.", "Only changes that keep the look."), () => Lang.T("až −16 %", "up to −16%"));
            PresetCard(row, Preset.MaxPerformance, '', () => Lang.T("Navíc kratší stíny, SMAA a jiné nasvícení drobností.", "Also shorter shadows, SMAA, different prop lighting."), () => Lang.T("až −26 %", "up to −26%"));
            PresetCard(row, Preset.Custom, '', () => Lang.T("Tvoje volby z ostatních záložek.", "Your picks from the other tabs."), () => "", last: true);
            return page;
        }

        private Label StatCard(VisualElement parent, char icon, Func<string> caption, Color valueColor, bool last = false)
        {
            var card = Box(Card, 14);
            card.style.flexGrow = 1;
            card.style.flexBasis = 0;
            card.style.marginRight = last ? 0 : 10;
            card.style.paddingLeft = 14; card.style.paddingTop = 12; card.style.paddingBottom = 12;
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            parent.Add(card);
            var ib = Box(Faint, 10);
            ib.style.width = 40; ib.style.height = 40;
            ib.style.alignItems = Align.Center; ib.style.justifyContent = Justify.Center;
            ib.style.marginRight = 12;
            ib.Add(Icon(icon, 20, Muted));
            card.Add(ib);
            var col = Column();
            card.Add(col);
            var v = Text("–", 24, valueColor, true);
            col.Add(v);
            var c = Text("", 11, Muted);
            _texts.Add((c, caption));
            col.Add(c);
            return v;
        }

        private void PresetCard(VisualElement parent, Preset preset, char icon, Func<string> info, Func<string> gain, bool last = false)
        {
            var card = Box(Card, 14);
            card.style.flexGrow = 1;
            card.style.flexBasis = 0;
            card.style.height = 150;
            card.style.marginRight = last ? 0 : 10;
            card.style.paddingLeft = 14; card.style.paddingRight = 12; card.style.paddingTop = 14; card.style.paddingBottom = 12;
            Border(card, 2, Color.clear);
            parent.Add(card);
            var top = Row();
            top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;
            card.Add(top);
            var circle = Box(Faint, 20);
            circle.style.width = 40; circle.style.height = 40;
            circle.style.alignItems = Align.Center; circle.style.justifyContent = Justify.Center;
            var ic = Icon(icon, 20, Ink);
            circle.Add(ic);
            top.Add(circle);
            var g = Text("", 12, Good, true);
            _texts.Add((g, gain));
            top.Add(g);
            var n = Text("", 16, Ink, true);
            n.style.marginTop = 10;
            _texts.Add((n, () => Profile.Name(preset)));
            card.Add(n);
            var d = Text("", 11, Muted);
            d.style.marginTop = 4;
            d.style.whiteSpace = WhiteSpace.Normal;
            _texts.Add((d, info));
            card.Add(d);
            _hits.Add(new Hit
            {
                El = card,
                Tab = 0,
                IsOn = () => _plugin.CurrentPreset == preset,
                Pick = () => _plugin.SetPreset(preset),
                Paint = (on, hot) =>
                {
                    card.style.backgroundColor = on ? AccentSoft : hot ? CardHot : Card;
                    Border(card, 2, on ? Accent : Color.clear);
                    circle.style.backgroundColor = on ? Accent : Faint;
                },
            });
        }

        // ---------- option pages ----------

        private VisualElement BuildGraphics()
        {
            var page = Page();
            var c = _plugin.Custom;
            Setting(page, 1, '', () => Lang.T("Stíny drobností", "Shadows of small props"),
                () => Lang.T("Hrnky, papíry a klávesnice vrhají stín jen zblízka.", "Mugs, papers and keyboards cast shadows only up close."),
                () => "−1,4 ms", true,
                Opt(() => Lang.T("Vždy", "Always"), p => p.ShadowCullFactor <= 0, () => Set(() => c.ShadowCulling.Value = false)),
                Opt(() => Lang.T("Zblízka", "Near"), p => p.ShadowCullFactor >= 15, () => Set(() => { c.ShadowCulling.Value = true; c.ShadowCullFactor.Value = 20; c.ShadowCullMinDistance.Value = 8; })),
                Opt(() => Lang.T("Těsně", "Closest"), p => p.ShadowCullFactor > 0 && p.ShadowCullFactor < 15, () => Set(() => { c.ShadowCulling.Value = true; c.ShadowCullFactor.Value = 10; c.ShadowCullMinDistance.Value = 5; })));
            Setting(page, 1, '', () => Lang.T("Venkovní lampy", "Street lights outside"),
                () => Lang.T("Jejich stíny s dosahem 52 m překreslují celé patro 6×.", "With a 52 m range their shadows redraw the whole floor 6×."),
                () => "−1,5 ms", true,
                Opt(() => Lang.T("Stíny", "Shadows"), p => p.ExteriorShadowsOff == 0, () => Set(() => c.ExteriorShadowsOff.Value = 0)),
                Opt(() => Lang.T("Bez uvnitř", "Off inside"), p => p.ExteriorShadowsOff == 1, () => Set(() => c.ExteriorShadowsOff.Value = 1)),
                Opt(() => Lang.T("Vždy bez", "Always off"), p => p.ExteriorShadowsOff == 2, () => Set(() => c.ExteriorShadowsOff.Value = 2)));
            Setting(page, 1, '', () => Lang.T("Stínové kaskády", "Shadow cascades"),
                () => Lang.T("2 kaskády s ostřejší mapou místo 4: méně kreslení.", "2 cascades with a sharper map instead of 4: less drawing."),
                () => Lang.T("méně dávek", "fewer batches"), true,
                Opt(() => Lang.T("Hra (4)", "Game (4)"), p => p.ShadowCascades == 0, () => Set(() => { c.ShadowCascades.Value = 0; c.ShadowResolution.Value = 0; })),
                Opt(() => Lang.T("2 ostřejší", "2 sharper"), p => p.ShadowCascades == 2, () => Set(() => { c.ShadowCascades.Value = 2; c.ShadowResolution.Value = 4096; })));
            Setting(page, 1, '', () => Lang.T("Dosah stínů", "Shadow distance"),
                () => Lang.T("Kratší dosah = méně stínů v dálce.", "Shorter = fewer distant shadows."),
                () => "", false,
                Opt(() => Lang.T("Hra", "Game"), p => p.ShadowDistance <= 0, () => Set(() => c.ShadowDistance.Value = 0)),
                Opt(() => "40 m", p => Mathf.Approximately(p.ShadowDistance, 40), () => Set(() => c.ShadowDistance.Value = 40)),
                Opt(() => "25 m", p => Mathf.Approximately(p.ShadowDistance, 25), () => Set(() => c.ShadowDistance.Value = 25)));
            Setting(page, 1, '', () => Lang.T("Osvětlení drobností", "Lighting of small props"),
                () => Lang.T("Ze sond scény místo vlastního napečení (o odstín tmavší).", "From scene probes instead of their own bake (a shade darker)."),
                () => "−1 ms", false,
                Opt(() => Lang.T("Hra", "Game"), p => !p.PropsUseSceneProbes, () => Set(() => c.PropsUseSceneProbes.Value = false)),
                Opt(() => Lang.T("Ze sond", "Probes"), p => p.PropsUseSceneProbes, () => Set(() => c.PropsUseSceneProbes.Value = true)));
            Setting(page, 1, '', () => Lang.T("Vyhlazování hran", "Anti-aliasing"),
                () => Lang.T("SMAA je levnější, hrany trochu méně hladké.", "SMAA is cheaper, edges slightly less smooth."),
                () => "−0,4 ms", false,
                Opt(() => "TAA", p => !p.TaaToSmaa, () => Set(() => c.TaaToSmaa.Value = false)),
                Opt(() => "SMAA", p => p.TaaToSmaa, () => Set(() => c.TaaToSmaa.Value = true)));
            Setting(page, 1, '', () => Lang.T("Webkamera šéfa", "Boss webcam"),
                () => Lang.T("Obraz na tabuli (a jiné kamery do textury) se kreslil každý snímek.", "The board picture (and other texture cameras) redrew every frame."),
                () => Lang.T("až −2 ms", "up to −2 ms"), true,
                Opt(() => Lang.T("Plně", "Full"), p => p.TextureCameraFps <= 0, () => Set(() => { c.TextureCameraFps.Value = 0; c.TextureCameraShadows.Value = true; })),
                Opt(() => "30 FPS", p => p.TextureCameraFps == 30, () => Set(() => { c.TextureCameraFps.Value = 30; c.TextureCameraShadows.Value = true; })),
                Opt(() => "15 FPS", p => p.TextureCameraFps > 0 && p.TextureCameraFps < 30, () => Set(() => { c.TextureCameraFps.Value = 15; c.TextureCameraShadows.Value = false; })));
            return page;
        }

        private VisualElement BuildCpu()
        {
            var page = Page();
            var c = _plugin.Custom;
            Setting(page, 2, '', () => Lang.T("Konzole BepInExu", "BepInEx console"),
                () => Lang.T("Hra kvůli ní čeká při každém pohybu myši. Log jde dál do souboru.", "The game waits on it at every mouse move. The log still goes to a file."),
                () => Lang.T("plynulé otáčení", "smooth turning"), true,
                Opt(() => Lang.T("Nechat", "Keep"), p => !p.DetachConsole, () => Set(() => c.DetachConsole.Value = false)),
                Opt(() => Lang.T("Zavřít", "Close"), p => p.DetachConsole, () => Set(() => c.DetachConsole.Value = true)));
            Setting(page, 2, '', () => Lang.T("Kurzor při otáčení", "Cursor while turning"),
                () => Lang.T("Skrytý kurzor se nepřenastavuje při každém pohybu myši.", "The hidden cursor isn't re-set on every mouse move."),
                () => Lang.T("~1 ms při otáčení", "~1 ms turning"), true,
                Opt(() => Lang.T("Hra", "Game"), p => !p.SkipCursorUpdates, () => Set(() => c.SkipCursorUpdates.Value = false)),
                Opt(() => Lang.T("Přeskočit", "Skip"), p => p.SkipCursorUpdates, () => Set(() => c.SkipCursorUpdates.Value = true)));
            Setting(page, 2, '', () => Lang.T("Tiché zdroje zvuku", "Silent audio sources"),
                () => Lang.T("Přes 1400 zvuků počítačů se vypne, dokud nehrají.", "1400+ computer sounds are off until they play."),
                () => Lang.T("až −1 ms", "up to −1 ms"), true,
                Opt(() => Lang.T("Nechat", "Keep"), p => !p.ParkIdleAudio, () => Set(() => c.ParkIdleAudio.Value = false)),
                Opt(() => Lang.T("Vypínat", "Park"), p => p.ParkIdleAudio, () => Set(() => c.ParkIdleAudio.Value = true)));
            return page;
        }

        private VisualElement BuildLimits()
        {
            var page = Page();
            var c = _plugin.Custom;
            Setting(page, 3, '', () => Lang.T("V hlavním menu", "In the main menu"),
                () => Lang.T("Menu jinak běží stovkami FPS zbytečně.", "Otherwise the menu runs at hundreds of FPS for nothing."),
                () => Lang.T("méně tepla", "less heat"), true, Limit(() => c.MenuFps, p => p.MenuFps, 0, 30, 60, 120));
            Setting(page, 3, '', () => Lang.T("Hra na pozadí", "Game in the background"),
                () => Lang.T("Když hra není aktivní okno, třeba při psaní v Discordu.", "When the game isn't the active window, e.g. typing in Discord."),
                () => "", true, Limit(() => c.BackgroundFps, p => p.BackgroundFps, 0, 15, 30, 60));
            Setting(page, 3, '', () => Lang.T("Ve hře", "In game"),
                () => Lang.T("Strop FPS pro celou hru. S VSync se použije dělení obnovovací frekvence.", "FPS cap for the whole game. With VSync a divider of the refresh rate is used."),
                () => "", true, Limit(() => c.FpsLimit, p => p.FpsLimit, 0, 60, 120, 144, 240));
            return page;
        }

        private VisualElement BuildDisplay()
        {
            var page = Page();
            Setting(page, 4, '', () => Lang.T("Ukazatel FPS vlevo nahoře", "FPS readout in the top left"),
                () => Lang.T("FPS, čas snímku, nejhorší snímek, dávky a zprávy Windows. Klávesa F10.", "FPS, frame time, worst frame, batches and Windows messages. Key F10."),
                () => "", true,
                new Hit { IsOn = () => !_plugin.ShowFps.Value, Pick = () => _plugin.ShowFps.Value = false, Tab = 4, El = Caption(() => Lang.T("Skrýt", "Hide")) },
                new Hit { IsOn = () => _plugin.ShowFps.Value, Pick = () => _plugin.ShowFps.Value = true, Tab = 4, El = Caption(() => Lang.T("Ukázat", "Show")) });
            var info = Box(Card, 14);
            info.style.paddingLeft = 16; info.style.paddingRight = 16; info.style.paddingTop = 14; info.style.paddingBottom = 14;
            info.style.marginTop = 6;
            page.Add(info);
            var ir = Row();
            ir.style.alignItems = Align.FlexStart;
            info.Add(ir);
            var ic = Icon('', 20, Muted);
            ic.style.marginRight = 12;
            ir.Add(ic);
            var it = Text("", 12, Muted);
            it.style.whiteSpace = WhiteSpace.Normal;
            it.style.flexShrink = 1;
            _texts.Add((it, () => Lang.T(
                "Hru brzdí počet kreslicích příkazů a čekání na Windows, ne grafická karta. Čísla u voleb jsou naměřená na Ryzen 9 9900X3D a RTX 5070 Ti v plné kanceláři; jinde a na jiném PC se liší. Nastavení se ukládá do BepInEx/config/dex.swyf.performance.cfg.",
                "The game is held back by the number of draw calls and by waiting on Windows, not by the graphics card. The numbers next to the options were measured on a Ryzen 9 9900X3D and an RTX 5070 Ti in a full office; elsewhere and on other PCs they differ. Settings are saved in BepInEx/config/dex.swyf.performance.cfg.")));
            ir.Add(it);
            return page;
        }

        /// <summary>One option row: icon, name, description, impact badge, segmented choices.</summary>
        private void Setting(VisualElement page, int tab, char icon, Func<string> name, Func<string> info, Func<string> impact, bool sameLook, params Hit[] options)
        {
            var card = Box(Card, 14);
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.paddingLeft = 14; card.style.paddingRight = 12; card.style.paddingTop = 10; card.style.paddingBottom = 10;
            card.style.marginBottom = 8;
            page.Add(card);

            var ib = Box(AccentSoft, 12);
            ib.style.width = 44; ib.style.height = 44;
            ib.style.alignItems = Align.Center; ib.style.justifyContent = Justify.Center;
            ib.style.marginRight = 14;
            ib.style.flexShrink = 0;
            ib.Add(Icon(icon, 22, AccentHot));
            card.Add(ib);

            var col = Column();
            col.style.flexShrink = 1;
            col.style.marginRight = 12;
            card.Add(col);
            var top = Row();
            top.style.alignItems = Align.Center;
            col.Add(top);
            var n = Text("", 15, Ink, true);
            _texts.Add((n, name));
            top.Add(n);
            top.Add(Badge(impact, Good));
            top.Add(Badge(() => sameLook ? Lang.T("stejný vzhled", "same look") : Lang.T("mění vzhled", "changes the look"), sameLook ? Muted : Warn));
            var d = Text("", 12, Muted);
            d.style.marginTop = 3;
            d.style.whiteSpace = WhiteSpace.Normal;
            _texts.Add((d, info));
            col.Add(d);

            var seg = Box(Seg, 10);
            seg.style.flexDirection = FlexDirection.Row;
            seg.style.flexShrink = 0;
            seg.style.paddingLeft = 3; seg.style.paddingRight = 3; seg.style.paddingTop = 3; seg.style.paddingBottom = 3;
            card.Add(seg);
            foreach (var o in options)
            {
                o.Tab = tab;
                o.El.style.minWidth = options.Length > 3 ? 54 : 84;
                seg.Add(o.El);
                o.Off = new Color(0, 0, 0, 0); o.OffHot = SegHot;
                _hits.Add(o);
            }
        }

        private VisualElement Badge(Func<string> text, Color color)
        {
            var b = Box(new Color(color.r, color.g, color.b, 0.14f), 6);
            b.style.marginLeft = 8;
            b.style.paddingLeft = 7; b.style.paddingRight = 7; b.style.paddingTop = 2; b.style.paddingBottom = 2;
            var l = Text("", 10, color, true);
            _texts.Add((l, () => { var s = text(); b.style.display = string.IsNullOrEmpty(s) ? DisplayStyle.None : DisplayStyle.Flex; return s; }));
            b.Add(l);
            return b;
        }

        private VisualElement Caption(Func<string> text)
        {
            var b = Box(Color.clear, 8);
            b.style.height = 32;
            b.style.paddingLeft = 10; b.style.paddingRight = 10;
            b.style.alignItems = Align.Center; b.style.justifyContent = Justify.Center;
            var l = Text("", 12, Ink, true);
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            _texts.Add((l, text));
            b.Add(l);
            return b;
        }

        private Hit Opt(Func<string> caption, Func<Profile, bool> isOn, Action pick) =>
            new Hit { El = Caption(caption), IsOn = () => isOn(_plugin.ActiveProfile), Pick = pick };

        private Hit[] Limit(Func<BepInEx.Configuration.ConfigEntry<int>> entry, Func<Profile, int> current, params int[] values)
        {
            var list = new List<Hit>();
            foreach (int v in values)
            {
                int vv = v;
                list.Add(new Hit
                {
                    El = Caption(() => vv == 0 ? Lang.T("Bez", "None") : vv.ToString()),
                    IsOn = () => current(_plugin.ActiveProfile) == vv,
                    Pick = () => Set(() => entry().Value = vv),
                });
            }
            return list.ToArray();
        }

        /// <summary>Any single option: become Custom (seeded with what runs now), then change it.</summary>
        private void Set(Action change)
        {
            _plugin.BeginCustomEdit();
            change();
            _plugin.EndCustomEdit();
        }

        public void ShowTabFor(int i) { if (i >= 0 && i < _pages.Count) ShowTab(i); }

        private void ShowTab(int i)
        {
            _tab = i;
            for (int p = 0; p < _pages.Count; p++) _pages[p].style.display = p == i ? DisplayStyle.Flex : DisplayStyle.None;
            _pageTitle.text = Tabs[i].name();
            _pageSub.text = Tabs[i].sub();
        }

        private void Retranslate()
        {
            foreach (var (label, text) in _texts) label.text = text();
            if (_pageTitle != null) { _pageTitle.text = Tabs[_tab].name(); _pageSub.text = Tabs[_tab].sub(); }
            if (_presetChip != null) _presetChip.text = Profile.Name(_plugin.CurrentPreset);
        }

        // ================= open / close / input =================

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            Retranslate();
            _root.style.display = DisplayStyle.Flex;
            var mpc = MetaPlayerController.Instance;
            if (mpc != null) { mpc.blocker.InputBlockers.Add(this); _blocked = mpc; }
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _root.style.display = DisplayStyle.None;
            if (_blocked != null) _blocked.blocker.InputBlockers.Remove(this);
            _blocked = null;
            // Back to play: the game (MetaCursor) only re-applies its cursor state when that state changes.
            if (MetaPlayerController.Instance != null && !FrameLimiter.InMenu())
            {
                UnityEngine.Cursor.lockState = CursorLockMode.Locked;
                UnityEngine.Cursor.visible = false;
            }
        }

        private void Update()
        {
            if (!IsOpen) return;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            if (Time.unscaledTime > _nextStats) { _nextStats = Time.unscaledTime + 0.15f; UpdateStats(); }

            var mouse = Mouse.current;
            if (mouse == null || _root.panel == null) return;
            var sp = mouse.position.ReadValue();
            // RuntimePanelUtils is stripped; the root covers the whole screen, so scale by hand.
            float scale = _root.worldBound.width > 1 ? _root.worldBound.width / Screen.width : 1f;
            var pp = new Vector2(sp.x * scale, (Screen.height - sp.y) * scale);
            bool click = mouse.leftButton.wasPressedThisFrame;
            Hit picked = null;
            foreach (var h in _hits)
            {
                if (h.Tab >= 0 && h.Tab != _tab) continue;
                bool on, hot = h.El.worldBound.Contains(pp);
                try { on = h.IsOn(); } catch { on = false; }
                if (h.Paint != null) h.Paint(on, hot);
                else h.El.style.backgroundColor = on ? (hot ? h.OnHot : h.On) : (hot ? h.OffHot : h.Off);
                if (hot && click) picked = h;
            }
            if (picked != null)
            {
                try { picked.Pick(); } catch (Exception e) { Plugin.Log.LogError("Menu: " + e); }
                Retranslate();
            }
        }

        private void UpdateStats()
        {
            double ms = FrameStats.AverageMs(30);
            _fps.text = ms > 0 ? (1000 / ms).ToString("F0") : "–";
            _ms.text = ms.ToString("F1") + " ms";
            _batches.text = FrameStats.Batches >= 0 ? FrameStats.Batches.ToString() : "–";
            _input.text = GapTimer.LastMs.ToString("F1") + " ms";
            _input.style.color = GapTimer.LastMs > 2 ? Bad : GapTimer.LastMs > 0.8 ? Warn : Ink;
            if (_tab != 0) return;
            // Bars scaled to the slowest of the last frames (at least 20 ms), coloured by frame time.
            double worst = 20;
            for (int i = 1; i <= Bars; i++) worst = Math.Max(worst, FrameStats.RecentMs(i));
            for (int i = 0; i < Bars; i++)
            {
                double f = FrameStats.RecentMs(Bars - i);
                var b = _bars[i];
                b.style.height = new Length((float)Math.Max(2, f / worst * 100), LengthUnit.Percent);
                b.style.backgroundColor = f <= 0 ? Faint : f < 10 ? Good : f < 20 ? Warn : Bad;
            }
            _graphNote.text = Lang.T($"nejhorší {FrameStats.WorstMs(Bars):F1} ms · stupnice {worst:F0} ms", $"worst {FrameStats.WorstMs(Bars):F1} ms · scale {worst:F0} ms");
        }

        private void LateUpdate()
        {
            if (!IsOpen) return;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        private void OnDestroy()
        {
            Close();
            Lang.Unwatch();
            if (_go != null) Destroy(_go);
            if (_panel != null) Destroy(_panel);
            Icons.Unload();
        }

        // ================= helpers =================

        private static VisualElement Page()
        {
            var p = new VisualElement { pickingMode = PickingMode.Ignore };
            p.style.flexDirection = FlexDirection.Column;
            p.style.flexGrow = 1;
            return p;
        }

        private static void Fill(VisualElement e)
        {
            e.style.position = Position.Absolute;
            e.style.left = 0; e.style.right = 0; e.style.top = 0; e.style.bottom = 0;
        }

        private static VisualElement Row() => new VisualElement { pickingMode = PickingMode.Ignore, style = { flexDirection = FlexDirection.Row } };

        private static VisualElement Column()
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.style.flexDirection = FlexDirection.Column;
            e.style.flexGrow = 1;
            return e;
        }

        private static VisualElement Box(Color c, float radius)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.style.backgroundColor = c;
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius = e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = radius;
            return e;
        }

        private static void Border(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = w;
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = c;
        }

        private static VisualElement Icon(char code, float size, Color tint)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.style.width = size;
            e.style.height = size;
            e.style.flexShrink = 0;
            var tex = Icons.Get(code);
            if (tex != null) e.style.backgroundImage = new StyleBackground(tex);
            e.style.unityBackgroundImageTintColor = tint;
            return e;
        }

        private static Label Text(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.fontSize = size;
            l.style.color = color;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginLeft = 0; l.style.marginRight = 0; l.style.marginTop = 0; l.style.marginBottom = 0;
            l.style.paddingLeft = 0; l.style.paddingRight = 0; l.style.paddingTop = 0; l.style.paddingBottom = 0;
            return l;
        }
    }
}

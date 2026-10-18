using System;
using System.Collections.Generic;
using Arcade.Games;
using UnityEngine;
using UnityEngine.UIElements;

namespace Arcade
{
    /// <summary>
    /// Content of one Arkáda window: a menu of game tiles and the game screen (pixel canvas, score, overlay).
    /// The plugin ticks every live view from its Update.
    /// </summary>
    internal sealed class ArcadeView
    {
        private static readonly List<ArcadeView> Live = new List<ArcadeView>();

        /// <summary>True while a game screen is showing in the focused Arkáda window: the game's keybinds stay quiet.</summary>
        internal static bool CapturesKeys
        {
            get
            {
                foreach (var v in Live) if (v._capturing) return true;
                return false;
            }
        }

        internal static void Build(object runtime)
        {
            var content = OsBridge.WindowContent(runtime);
            if (content == null) return;
            foreach (var old in Live.ToArray())
                if (ReferenceEquals(old._runtime, runtime)) { old.Dispose(); Live.Remove(old); }
            content.Clear();
            OsBridge.DisableAutoSize(runtime);
            Live.Add(new ArcadeView(runtime, content));
        }

        internal static void StateChanged(object runtime)
        {
            foreach (var v in Live)
                if (ReferenceEquals(v._runtime, runtime) && OsBridge.State(runtime) != 1 && v._mode == Mode.Playing) v.SetMode(Mode.Paused);
        }

        internal static void TickAll(float dt)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var v = Live[i];
                if (v._dead) { v.Dispose(); Live.RemoveAt(i); continue; }
                try { v.Tick(dt); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Arkáda stopped: " + e);
                    v.Dispose();
                    Live.RemoveAt(i);
                }
            }
        }

        /// <summary>The game language changed: re-text every open Arkáda window in place (game state is kept).</summary>
        internal static void RefreshAllTexts()
        {
            foreach (var v in Live)
            {
                try { v.RefreshTexts(); }
                catch (Exception e) { Plugin.Log.LogWarning("Could not re-text an Arkáda window: " + e.Message); }
            }
        }

        internal static void DisposeAll()
        {
            foreach (var v in Live) v.Dispose();
            Live.Clear();
        }

        private enum Mode { Menu, Ready, Playing, Paused, Over }

        private static readonly Color Bg = Hex(0x0f1220), Card = Hex(0x1a1f36), CardHover = Hex(0x252b4a), Ink = Hex(0xeef0ff),
            Muted = Hex(0x9aa3c7), Accent = Hex(0xb48cff), Gold = Hex(0xffd166);

        private readonly object _runtime;
        private readonly VisualElement _root, _menu, _screen, _stage, _canvasEl, _overlay;
        private readonly Label _head, _sub, _back, _title, _score, _best, _status, _hint, _overlayTitle, _overlaySub;
        private readonly Dictionary<string, Label> _tileBest = new Dictionary<string, Label>();
        private readonly Dictionary<string, Label> _tileName = new Dictionary<string, Label>();
        private readonly Dictionary<string, Label> _tileTag = new Dictionary<string, Label>();
        private readonly List<PixelCanvas> _icons = new List<PixelCanvas>();
        private readonly ArcadeInput _input = new ArcadeInput();
        private readonly Game[] _games = { new Pacman(), new Snake(), new Tetris(), new Breakout(), new Pong() };

        private Game _game;
        private PixelCanvas _canvas;
        private Mode _mode = Mode.Menu;
        private float _acc;
        private bool _attached, _dead, _capturing, _newRecord;

        private ArcadeView(object runtime, VisualElement content)
        {
            _runtime = runtime;
            _root = new VisualElement { focusable = true };
            _root.style.flexGrow = 1;
            _root.style.minHeight = 0;
            _root.style.backgroundColor = Bg;
            Pad(_root, 14);
            _root.RegisterCallback<AttachToPanelEvent>(_ => _attached = true);
            _root.RegisterCallback<DetachFromPanelEvent>(_ => { if (_attached) _dead = true; });
            // Clicking the arcade takes keyboard focus away from text fields in other windows.
            _root.RegisterCallback<PointerDownEvent>(_ => _root.Focus(), TrickleDown.TrickleDown);
            content.Add(_root);

            // ---- Menu ----
            _menu = new VisualElement();
            _menu.style.flexGrow = 1;
            _root.Add(_menu);
            _head = Text(HeadText, 24, Ink, bold: true);
            _menu.Add(_head);
            _sub = Text(SubText, 12, Muted);
            _sub.style.marginBottom = 12;
            _menu.Add(_sub);
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.SpaceBetween;
            _menu.Add(grid);
            foreach (var g in _games) grid.Add(Tile(g));

            // ---- Game screen ----
            _screen = new VisualElement();
            _screen.style.flexGrow = 1;
            _screen.style.minHeight = 0;
            _screen.style.display = DisplayStyle.None;
            _root.Add(_screen);

            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 6;
            _screen.Add(bar);
            var back = PlainButton(BackText, () => ShowMenu(), out _back);
            bar.Add(back);
            _title = Text("", 18, Ink, bold: true);
            _title.style.flexGrow = 1;
            _title.style.marginLeft = 10;
            bar.Add(_title);
            _score = Text("", 15, Ink, bold: true);
            bar.Add(_score);
            _best = Text("", 13, Gold);
            _best.style.marginLeft = 14;
            bar.Add(_best);

            _status = Text("", 12, Muted);
            _status.style.marginBottom = 6;
            _screen.Add(_status);

            _stage = new VisualElement();
            _stage.style.flexGrow = 1;
            _stage.style.minHeight = 0;
            _stage.style.alignItems = Align.Center;
            _stage.style.justifyContent = Justify.Center;
            _stage.style.overflow = Overflow.Hidden;
            _stage.RegisterCallback<GeometryChangedEvent>(_ => FitCanvas());
            _stage.RegisterCallback<ClickEvent>(_ => OnStageClick());
            _screen.Add(_stage);

            _canvasEl = new VisualElement { pickingMode = PickingMode.Ignore };
            _stage.Add(_canvasEl);

            _overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0;
            _overlay.style.right = 0;
            _overlay.style.top = 0;
            _overlay.style.bottom = 0;
            _overlay.style.alignItems = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _stage.Add(_overlay);
            _overlayTitle = Text("", 26, Ink, bold: true);
            _overlayTitle.pickingMode = PickingMode.Ignore;
            _overlayTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _overlay.Add(_overlayTitle);
            _overlaySub = Text("", 13, Ink);
            _overlaySub.pickingMode = PickingMode.Ignore;
            _overlaySub.style.unityTextAlign = TextAnchor.MiddleCenter;
            _overlaySub.style.marginTop = 6;
            _overlay.Add(_overlaySub);

            _hint = Text("", 11, Muted);
            _hint.style.marginTop = 8;
            _hint.style.whiteSpace = WhiteSpace.Normal;
            _hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            _screen.Add(_hint);
        }

        // ---- Building blocks ----

        private VisualElement Tile(Game g)
        {
            var tile = new VisualElement();
            tile.style.width = Length.Percent(32);
            tile.style.marginBottom = 10;
            tile.style.backgroundColor = Card;
            Round(tile, 12);
            Pad(tile, 12);
            tile.style.alignItems = Align.Center;
            tile.RegisterCallback<PointerEnterEvent>(_ => tile.style.backgroundColor = CardHover);
            tile.RegisterCallback<PointerLeaveEvent>(_ => tile.style.backgroundColor = Card);
            tile.RegisterCallback<ClickEvent>(_ => Open(g));

            var icon = new PixelCanvas(32, 32);
            g.DrawIcon(icon);
            icon.Upload();
            _icons.Add(icon);
            var pic = new VisualElement { pickingMode = PickingMode.Ignore };
            pic.style.width = 72;
            pic.style.height = 72;
            pic.style.backgroundImage = new StyleBackground(icon.Texture);
            Round(pic, 10);
            pic.style.marginBottom = 8;
            tile.Add(pic);

            var name = Text(g.Name, 16, Ink, bold: true);
            name.pickingMode = PickingMode.Ignore;
            tile.Add(name);
            _tileName[g.Id] = name;
            var tag = Text(g.Tagline, 11, Muted);
            tag.pickingMode = PickingMode.Ignore;
            tag.style.whiteSpace = WhiteSpace.Normal;
            tag.style.unityTextAlign = TextAnchor.MiddleCenter;
            tile.Add(tag);
            _tileTag[g.Id] = tag;
            var best = Text(BestText(g), 11, Gold);
            best.pickingMode = PickingMode.Ignore;
            best.style.marginTop = 4;
            tile.Add(best);
            _tileBest[g.Id] = best;
            return tile;
        }

        private static string BestText(Game g)
        {
            int b = Best(g);
            return b > 0 ? BestLabel(b) : Lang.T("Zatím bez rekordu", "No best score yet");
        }

        private static string HeadText => Lang.T("Arkáda", "Arcade");
        private static string SubText => Lang.T("Vyber si hru. Hraje se šipkami nebo WASD, mezerník = start.",
            "Pick a game. Play with the arrow keys or WASD, Space = start.");
        private static string BackText => Lang.T("‹ Hry", "‹ Games");
        private static string ScoreText(int score) => Lang.T($"Skóre {score}", $"Score {score}");
        private static string BestLabel(int best) => Lang.T($"Rekord {best}", $"Best {best}");

        /// <summary>Re-reads every visible text for the current game language.</summary>
        private void RefreshTexts()
        {
            SetText(_head, HeadText);
            SetText(_sub, SubText);
            SetText(_back, BackText);
            foreach (var g in _games)
            {
                SetText(_tileName[g.Id], g.Name);
                SetText(_tileTag[g.Id], g.Tagline);
                SetText(_tileBest[g.Id], BestText(g));
            }
            if (_game != null)
            {
                SetText(_title, _game.Name);
                SetText(_hint, _game.Controls);
                SetText(_score, ScoreText(_game.Score));
                SetText(_best, BestLabel(Math.Max(Best(_game), _game.Score)));
                SetText(_status, _game.Status ?? "");
            }
            if (_mode != Mode.Menu) UpdateOverlay();
        }

        private VisualElement PlainButton(string text, Action onClick, out Label label)
        {
            var b = new VisualElement();
            b.style.backgroundColor = Card;
            Round(b, 8);
            b.style.paddingLeft = 10;
            b.style.paddingRight = 12;
            b.style.paddingTop = 4;
            b.style.paddingBottom = 4;
            var l = Text(text, 13, Ink, bold: true);
            l.pickingMode = PickingMode.Ignore;
            b.Add(l);
            label = l;
            b.RegisterCallback<PointerEnterEvent>(_ => b.style.backgroundColor = CardHover);
            b.RegisterCallback<PointerLeaveEvent>(_ => b.style.backgroundColor = Card);
            b.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); onClick(); });
            return b;
        }

        private static Label Text(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text);
            l.style.fontSize = size;
            l.style.color = color;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginLeft = 0;
            l.style.marginRight = 0;
            l.style.paddingLeft = 0;
            l.style.paddingRight = 0;
            return l;
        }

        private static void Pad(VisualElement e, float p)
        {
            e.style.paddingLeft = p;
            e.style.paddingRight = p;
            e.style.paddingTop = p;
            e.style.paddingBottom = p;
        }

        private static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static Color Hex(uint rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        // ---- Flow ----

        private void Open(Game g)
        {
            _game = g;
            _canvas?.Dispose();
            _canvas = new PixelCanvas(g.Width, g.Height);
            _canvasEl.style.backgroundImage = new StyleBackground(_canvas.Texture);
            _title.text = g.Name;
            _hint.text = g.Controls;
            _menu.style.display = DisplayStyle.None;
            _screen.style.display = DisplayStyle.Flex;
            g.Restart();
            SetMode(Mode.Ready);
            FitCanvas();
            _root.Focus();
        }

        private void ShowMenu()
        {
            _game = null;
            _screen.style.display = DisplayStyle.None;
            _menu.style.display = DisplayStyle.Flex;
            foreach (var g in _games) _tileBest[g.Id].text = BestText(g);
            SetMode(Mode.Menu);
        }

        private void OnStageClick()
        {
            if (_mode == Mode.Ready || _mode == Mode.Paused) SetMode(Mode.Playing);
            else if (_mode == Mode.Over) { _game.Restart(); SetMode(Mode.Ready); }
        }

        private void SetMode(Mode m)
        {
            _mode = m;
            _acc = 0;
            UpdateOverlay();
        }

        private void FitCanvas()
        {
            if (_game == null) return;
            var r = _stage.contentRect;
            if (r.width <= 1 || r.height <= 1) return;
            float scale = Mathf.Min(r.width / _game.Width, r.height / _game.Height);
            if (scale >= 2) scale = Mathf.Floor(scale); // whole-number scale keeps the pixels crisp
            _canvasEl.style.width = Mathf.Floor(_game.Width * scale);
            _canvasEl.style.height = Mathf.Floor(_game.Height * scale);
        }

        private bool Focused
        {
            get
            {
                if (_root.panel == null || OsBridge.State(_runtime) != 1) return false;
                if (!KolkataMonitorInputState.IsMonitorInputCaptured) return false;
                var window = OsBridge.Window(_runtime);
                return window == null || window.ClassListContains("kolkata-window--active");
            }
        }

        private void Tick(float dt)
        {
            if (!_attached) return;
            bool focused = Focused;
            _capturing = focused && _mode != Mode.Menu;
            if (_mode == Mode.Menu) return;

            _input.Read(focused);
            if (_mode == Mode.Playing && !focused) SetMode(Mode.Paused);

            switch (_mode)
            {
                case Mode.Ready:
                    if (_input.Confirm || _input.AnyDirectionPressed) { SetMode(Mode.Playing); RunSteps(dt); }
                    break;
                case Mode.Playing:
                    if (_input.Pause) SetMode(Mode.Paused);
                    else RunSteps(dt);
                    break;
                case Mode.Paused:
                    if (_input.Pause || _input.Confirm) SetMode(Mode.Playing);
                    break;
                case Mode.Over:
                    if (_input.Confirm) { _game.Restart(); SetMode(Mode.Ready); }
                    break;
            }

            if (OsBridge.State(_runtime) != 1 || _game == null) return;
            _game.Draw(_canvas);
            _canvas.Upload();
            _canvasEl.MarkDirtyRepaint();
            SetText(_score, ScoreText(_game.Score));
            SetText(_best, BestLabel(Math.Max(Best(_game), _game.Score)));
            SetText(_status, _game.Status ?? "");
            if (_mode == Mode.Playing) UpdateOverlay();
        }

        private void RunSteps(float dt)
        {
            _acc += Mathf.Min(dt, 0.1f);
            int n = 0;
            while (_acc >= Game.Dt && n++ < 6)
            {
                _game.Step(_input);
                _input.ConsumePresses();
                _acc -= Game.Dt;
                if (_game.Over) { GameOver(); return; }
            }
        }

        private void GameOver()
        {
            _newRecord = _game.Score > Best(_game);
            if (_newRecord) SaveBest(_game, _game.Score);
            SetMode(Mode.Over);
        }

        private void UpdateOverlay()
        {
            string title = null, sub = null;
            bool dim = true;
            switch (_mode)
            {
                case Mode.Ready:
                    title = _game?.Name;
                    sub = Lang.T("Mezerník nebo šipka = start", "Space or arrow = start");
                    break;
                case Mode.Paused:
                    title = Lang.T("Pauza", "Paused");
                    sub = Lang.T("P nebo mezerník = pokračovat", "P or Space = resume");
                    break;
                case Mode.Over:
                    title = _game.OverTitle;
                    sub = (_newRecord ? Lang.T($"Nový rekord: {_game.Score}!", $"New best: {_game.Score}!") : ScoreText(_game.Score))
                        + "\n" + Lang.T("Mezerník = hrát znovu", "Space = play again");
                    break;
                case Mode.Playing:
                    title = _game?.Banner;
                    dim = false;
                    break;
            }
            _overlay.style.display = title == null ? DisplayStyle.None : DisplayStyle.Flex;
            _overlay.style.backgroundColor = dim ? new Color(0.03f, 0.04f, 0.09f, 0.62f) : new Color(0, 0, 0, 0);
            SetText(_overlayTitle, title ?? "");
            SetText(_overlaySub, sub ?? "");
            _overlaySub.style.display = string.IsNullOrEmpty(sub) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static void SetText(Label l, string text)
        {
            if (l.text != text) l.text = text;
        }

        private static int Best(Game g) => PlayerPrefs.GetInt("swyf.arcade.best." + g.Id, 0);

        private static void SaveBest(Game g, int score)
        {
            PlayerPrefs.SetInt("swyf.arcade.best." + g.Id, score);
            PlayerPrefs.Save();
        }

        private void Dispose()
        {
            _capturing = false;
            _canvas?.Dispose();
            _canvas = null;
            foreach (var i in _icons) i.Dispose();
            _icons.Clear();
            _root.RemoveFromHierarchy();
        }
    }
}

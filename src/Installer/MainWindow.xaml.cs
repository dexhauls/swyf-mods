using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SwyfInstaller
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(params string[] names)
        {
            foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    public enum ModStatus { Off, On, Running, Error }

    public sealed class ModCard : Observable
    {
        private bool _enabled;
        private ModStatus _status;
        private string _statusText;

        public InstalledMod Mod { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Subtitle { get; set; }
        public string Description { get; set; }
        public string IconGlyph { get; set; }
        public List<string> Tags { get; set; }
        public bool Locked { get; set; }
        public List<SettingRow> Settings { get; set; } = new List<SettingRow>();

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; Raise(nameof(Enabled)); }
        }

        public ModStatus Status
        {
            get => _status;
            set { _status = value; Raise(nameof(Status)); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; Raise(nameof(StatusText)); }
        }
    }

    public sealed class AddCard
    {
    }

    /// <summary>One "settings" entry of a mod's mod.json, shown on its card as a row of options.</summary>
    public sealed class SettingRow
    {
        public ModCard Card { get; set; }
        public ModSetting Setting { get; set; }
        public string Label { get; set; }
        public List<SettingChoice> Choices { get; set; }
    }

    public sealed class SettingChoice : Observable
    {
        private bool _selected;

        public SettingRow Row { get; set; }
        public ModSettingOption Option { get; set; }
        public string Name { get; set; }
        public string Hint { get; set; }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; Raise(nameof(Selected)); }
        }
    }

    public enum TaskState { Pending, Running, Done }

    public sealed class TaskRow : Observable
    {
        private TaskState _state;
        private string _title;
        public string Key { get; set; }

        public string Title
        {
            get => _title;
            set { _title = value; Raise(nameof(Title)); }
        }

        public TaskState State
        {
            get => _state;
            set { _state = value; Raise(nameof(State)); }
        }
    }

    public sealed class DownloadChoice : Observable
    {
        private bool _selected;
        public ModDownload Download { get; set; }
        public string Name => Download.LocalName;
        public string Description => Download.LocalDescription;
        public string SizeText { get; set; }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; Raise(nameof(Selected)); }
        }
    }

    public partial class MainWindow : Window
    {
        private enum Page { Find, Base, Library }

        private enum FindState { Searching, NotFound, Found, WrongFolder }

        private static string[] StepNames => new[] { Lang.T("Hra", "Game"), Lang.T("Základ", "Base"), Lang.T("Knihovna modů", "Mod library") };

        private static string T(string cs, string en) => Lang.T(cs, en);

        private readonly Hub _hub = new Hub();
        private readonly ObservableCollection<object> _cards = new ObservableCollection<object>();
        private readonly ObservableCollection<TaskRow> _baseTasks = new ObservableCollection<TaskRow>();
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly List<(Border Circle, TextBlock Number, TextBlock Label, Rectangle Line)> _steps = new List<(Border, TextBlock, TextBlock, Rectangle)>();
        private string _gameDir;
        private Page _page = Page.Find;
        private Action _dialogOk;
        private string _pendingPackage;
        private string _packagePath;
        private ModManifest _packageManifest;
        private bool _busy;
        private FindState _findState;
        private bool _findNeedsBase;
        private bool _baseLegacy, _baseUpdate;
        private List<InstalledMod> _baseMods = new List<InstalledMod>();
        private InstalledMod _packageExisting;

        public MainWindow()
        {
            InitializeComponent();
            // Not in --game test mode: testing shouldn't touch the installed hub.
            if (!Environment.GetCommandLineArgs().Contains("--game"))
                try { ShellIntegration.UpdateInstalledCopy(); } catch (Exception ex) { Debug.WriteLine(ex); }
            ContentRoot.SizeChanged += (s, e) => ContentRoot.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);
            LoadArtwork();
            ModList.ItemsSource = _cards;
            BaseTasks.ItemsSource = _baseTasks;
            BuildStepper();
            ApplyStaticTexts();
            _statusTimer.Tick += (s, e) => RefreshStatuses();
            // The game's language may have changed while the hub was in the background.
            Activated += (s, e) =>
            {
                if (Lang.Refresh(_gameDir)) OnLanguageChanged();
            };

            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            _pendingPackage = args.FirstOrDefault(a => (a.EndsWith(".swyfmod", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                                        a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) && File.Exists(a));
            Loaded += async (s, e) => await FindGameAsync(args);
        }

        // ================= Language =================

        /// <summary>Texts that don't depend on state: XAML strings (DynamicResource S.*), stepper, version line.</summary>
        private void ApplyStaticTexts()
        {
            var texts = new Dictionary<string, string>
            {
                ["S.Required"] = T("Povinný", "Required"),
                ["S.RemoveMod"] = T("Odebrat mod", "Remove mod"),
                ["S.AddMod"] = T("Přidat mod", "Add mod"),
                ["S.AddModHint"] = T("Přetáhni sem .swyfmod nebo .zip, nebo klikni", "Drop a .swyfmod or .zip here, or click"),
                ["S.Minimize"] = T("Minimalizovat", "Minimize"),
                ["S.Close"] = T("Zavřít", "Close"),
                ["S.PickFolder"] = T("Vybrat složku hry", "Choose game folder"),
                ["S.Bullet1"] = T("Mody zapínáš a vypínáš za běhu, bez restartu hry", "Turn mods on and off while you play, no game restart"),
                ["S.Bullet2"] = T("Nový mod = dvojklik na soubor .swyfmod", "New mod = double-click a .swyfmod file"),
                ["S.Bullet3"] = T("Hra se nemění, všechno jde kdykoli odinstalovat", "The game itself isn't changed, everything can be uninstalled anytime"),
                ["S.SkipForNow"] = T("Zatím přeskočit", "Skip for now"),
                ["S.Library"] = T("Knihovna modů", "Mod library"),
                ["S.Update"] = T("Aktualizovat", "Update"),
                ["S.Change"] = T("Změnit", "Change"),
                ["S.UninstallAll"] = T("Odinstalovat vše", "Uninstall all"),
                ["S.Launch"] = T("Spustit hru", "Launch game"),
                ["S.DropHere"] = T("Pusť mod sem", "Drop the mod here"),
                ["S.DropHint"] = T("soubor .swyfmod, .zip nebo .dll", ".swyfmod, .zip or .dll file"),
                ["S.DownloadsHeading"] = T("K MODU SE STÁHNE", "ALSO DOWNLOADS"),
                ["S.Cancel"] = T("Zrušit", "Cancel"),
            };
            foreach (var kv in texts) Resources[kv.Key] = kv.Value;

            VersionText.Text = T($"verze {Hub.Version}  ·  Scam With Your Friends", $"version {Hub.Version}  ·  Scam With Your Friends");
            var names = StepNames;
            for (var i = 0; i < _steps.Count && i < names.Length; i++) _steps[i].Label.Text = names[i];
        }

        /// <summary>The game's language changed: redo every text currently on screen.</summary>
        private void OnLanguageChanged()
        {
            ApplyStaticTexts();
            switch (_page)
            {
                case Page.Find:
                    RenderFindTexts();
                    break;
                case Page.Base:
                    RenderBaseTexts();
                    break;
                case Page.Library:
                    if (!_busy) RebuildCards();
                    else RefreshStatuses();
                    break;
            }
            if (InstallLayer.Visibility == Visibility.Visible && PkgChoice.Visibility == Visibility.Visible) RenderPackageTexts();
        }

        // ================= 1) Find the game =================

        private async Task FindGameAsync(List<string> args)
        {
            SetFind(FindState.Searching);
            var started = DateTime.UtcNow;
            // "--game <folder>" overrides detection (testing, or a friend with an odd setup).
            var i = args.IndexOf("--game");
            var forced = i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
            var found = forced != null && SteamLocator.IsGameFolder(forced) ? forced : await Task.Run(() => SteamLocator.FindGame());
            var wait = TimeSpan.FromMilliseconds(_pendingPackage != null ? 500 : 1100) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            if (found != null) await GameFoundAsync(found);
            else SetFind(FindState.NotFound);
        }

        private async Task GameFoundAsync(string dir)
        {
            _gameDir = dir;
            // Now the game's own PlayerPrefs key can be found via app.info.
            if (Lang.Refresh(dir)) ApplyStaticTexts();
            var needsBase = Hub.BaseNeedsInstall(dir);
            _findNeedsBase = needsBase;
            SetFind(FindState.Found);
            FindPath.Text = dir;
            FindPathCard.Visibility = Visibility.Visible;
            await Task.Delay(_pendingPackage != null ? 700 : 1200);
            if (needsBase) OpenBase();
            else OpenLibrary();
        }

        private void SetFind(FindState state)
        {
            _findState = state;
            var searching = state == FindState.Searching;
            var found = state == FindState.Found;
            var pick = state == FindState.NotFound || state == FindState.WrongFolder;
            FindSpinner.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            FindIcon.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            FindIcon.Text = found ? "" : "";
            FindIcon.Foreground = (Brush)FindResource(found ? "Success" : "Accent");
            FindRing.Fill = new SolidColorBrush(found ? Color.FromArgb(0x22, 0x6E, 0xE7, 0xA0) : Color.FromArgb(0x14, 0xFF, 0xC2, 0x3D));
            RenderFindTexts();
            FindPathCard.Visibility = Visibility.Collapsed;
            FindPickButton.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
            if (found) Pop(FindIcon);
        }

        private void RenderFindTexts()
        {
            switch (_findState)
            {
                case FindState.Searching:
                    FindTitle.Text = T("Hledám hru…", "Looking for the game…");
                    FindSubtitle.Text = T("Kouknu do tvé Steam knihovny, kde máš Scam With Your Friends.",
                        "Checking your Steam library for Scam With Your Friends.");
                    break;
                case FindState.NotFound:
                    FindTitle.Text = T("Steam hru nenašel", "Steam didn't find the game");
                    FindSubtitle.Text = T("Nevadí, ukaž mi složku, kde máš Scam With Your Friends (tam, kde je Scam With Your Friends.exe).",
                        "No problem, show me the folder where Scam With Your Friends is installed (the one with Scam With Your Friends.exe).");
                    break;
                case FindState.Found:
                    FindTitle.Text = T("Hra nalezena!", "Game found!");
                    FindSubtitle.Text = _findNeedsBase ? T("Jdeme připravit základ…", "Let's set up the base…") : T("Otevírám knihovnu modů…", "Opening the mod library…");
                    break;
                case FindState.WrongFolder:
                    FindTitle.Text = T("Tady hra není", "The game isn't here");
                    FindSubtitle.Text = T("V téhle složce chybí Scam With Your Friends.exe. Zkus vybrat jinou.",
                        "There's no Scam With Your Friends.exe in this folder. Try another one.");
                    break;
            }
        }

        private async void OnPickFolder(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var picked = FolderPicker.Pick(new WindowInteropHelper(this).Handle,
                T("Vyber složku se hrou Scam With Your Friends", "Choose the Scam With Your Friends game folder"),
                _gameDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            if (picked == null) return;
            var dir = new[]
            {
                picked,
                System.IO.Path.Combine(picked, "Scam With Your Friends"),
                System.IO.Path.Combine(picked, "common", "Scam With Your Friends"),
                System.IO.Path.Combine(picked, "steamapps", "common", "Scam With Your Friends"),
            }.FirstOrDefault(SteamLocator.IsGameFolder);

            if (_page != Page.Find) GoTo(Page.Find);
            if (dir == null)
            {
                SetFind(FindState.WrongFolder);
                return;
            }
            await GameFoundAsync(dir);
        }

        // ================= 2) Base =================

        private void OnSkipBase(object sender, RoutedEventArgs e) => OpenLibrary();

        private void OnOpenBase(object sender, RoutedEventArgs e) => OpenBase();

        private void OpenBase()
        {
            _statusTimer.Stop();
            var hasLoader = Hub.HasAnyLoader(_gameDir);
            _baseLegacy = hasLoader && !Hub.IsManagedByHub(_gameDir);
            _baseUpdate = !_baseLegacy && Hub.IsBaseInstalled(_gameDir);
            BaseSkipButton.Visibility = hasLoader ? Visibility.Visible : Visibility.Collapsed;

            _baseMods = Hub.ListMods(_gameDir);
            BaseModsPanel.Visibility = _baseMods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BaseButton.IsEnabled = true;
            BaseProgress.Visibility = Visibility.Hidden;
            _baseTasks.Clear();
            _baseTasks.Add(new TaskRow { Key = Hub.StageLoader });
            _baseTasks.Add(new TaskRow { Key = Hub.StageFix });
            _baseTasks.Add(new TaskRow { Key = Hub.StageManager });
            RenderBaseTexts();
            GoTo(Page.Base);
        }

        private void RenderBaseTexts()
        {
            BaseTitle.Text = _baseLegacy ? T("Převod na Mod Hub", "Switch to Mod Hub")
                : _baseUpdate ? T("Aktualizace základu", "Base update")
                : T("Nejdřív základ", "First, the base");
            BaseSubtitle.Text = _baseLegacy
                ? T("Našel jsem ručně nainstalovaný mod loader. Nahradím ho základem s Mod Managerem, aby šly mody zapínat a vypínat za běhu. Mody i jejich nastavení ti zůstanou. Hra u toho musí být chvilku zavřená.",
                    "I found a manually installed mod loader. I'll replace it with the base and the Mod Manager so mods can be turned on and off while the game runs. Your mods and their settings stay. The game needs to be closed for a moment.")
                : _baseUpdate
                    ? T("Tenhle hub má novější Mod Manager. Aktualizuju ho, tvoje mody i nastavení zůstanou. Hra u toho musí být chvilku zavřená.",
                        "This hub has a newer Mod Manager. I'll update it; your mods and settings stay. The game needs to be closed for a moment.")
                    : T("Jednou do hry nainstaluju mod loader a Mod Manager. Díky němu se pak mody zapínají a vypínají za běhu hry, bez restartu. Na tenhle jediný krok musí být hra zavřená.",
                        "I'll install a mod loader and the Mod Manager into the game once. After that, mods turn on and off while the game is running, no restart needed. The game must be closed for this one step.");
            BaseButtonText.Text = _baseLegacy ? T("Převést", "Switch over") : _baseUpdate ? T("Aktualizovat", "Update") : T("Nainstalovat základ", "Install base");

            BaseModsList.ItemsSource = _baseMods.Select(m => m.Manifest.LocalName ?? m.Id).ToList();
            BaseModsTitle.Text = T($"NAŠEL JSEM TVOJE MODY ({_baseMods.Count}) · ZŮSTANOU TI", $"FOUND YOUR MODS ({_baseMods.Count}) · THEY'LL STAY");
            if (!_busy) BasePercent.Text = T("Co se nainstaluje", "What gets installed");
            foreach (var task in _baseTasks)
            {
                task.Title = task.Key == Hub.StageLoader ? "Mod loader (BepInEx)"
                    : task.Key == Hub.StageFix ? T("Úprava hry pro mody", "Game fix for mods")
                    : $"SWYF Mod Manager {Hub.Version}";
            }
        }

        private async void OnInstallBase(object sender, RoutedEventArgs e)
        {
            if (Hub.IsGameRunning(_gameDir))
            {
                ShowDialog(T("Zavři hru", "Close the game"),
                    T("Základ se instaluje jen jednou a hra u toho nesmí běžet. Pak už mody půjdou zapínat a vypínat za běhu.",
                        "The base is installed only once and the game can't be running while it happens. After that, mods can be turned on and off while it runs."),
                    T("Rozumím", "Got it"), Tone.Warning);
                return;
            }
            _busy = true;
            BaseButton.IsEnabled = false;
            BaseProgress.Value = 0;
            BaseProgress.Visibility = Visibility.Visible;
            var progress = new Progress<double>(v =>
            {
                Animate(BaseProgress, RangeBase.ValueProperty, v, 180);
                BasePercent.Text = T($"Instaluju… {Math.Round(v * 100)} %", $"Installing… {Math.Round(v * 100)}%");
            });
            var stage = new Progress<string>(key =>
            {
                foreach (var t in _baseTasks)
                {
                    if (t.Key == key) { t.State = TaskState.Running; break; }
                    t.State = TaskState.Done;
                }
            });
            try
            {
                var dir = _gameDir;
                await Task.Run(() => _hub.InstallBase(dir, progress, stage));
                foreach (var t in _baseTasks)
                {
                    t.State = TaskState.Done;
                    await Task.Delay(160);
                }
                try
                {
                    // Not in --game test mode: testing shouldn't register file types on the machine.
                    if (!Environment.GetCommandLineArgs().Contains("--game")) ShellIntegration.Register();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                BasePercent.Text = T("Hotovo", "Done");
                await Task.Delay(500);
                _busy = false;
                OpenLibrary();
                ShowToast(T("Základ je nainstalovaný. Teď přidej mody.", "The base is installed. Now add some mods."), Tone.Ok);
            }
            catch (Exception ex)
            {
                _busy = false;
                BaseButton.IsEnabled = true;
                ShowError(ex);
            }
        }

        // ================= 3) Library =================

        private void OpenLibrary()
        {
            LibraryPath.Text = _gameDir;
            RebuildCards();
            GoTo(Page.Library);
            _statusTimer.Start();
            if (_pendingPackage != null)
            {
                var p = _pendingPackage;
                _pendingPackage = null;
                Dispatcher.BeginInvoke(new Action(() => OpenPackage(p)), DispatcherPriority.Background);
            }
        }

        private void RebuildCards()
        {
            _cards.Clear();
            _cards.Add(new ModCard
            {
                Id = "#manager",
                Name = "SWYF Mod Manager",
                Subtitle = T($"verze {Hub.Version}  ·  základ", $"version {Hub.Version}  ·  base"),
                Description = T("Hlídá složku Mods a načítá mody za běhu hry. Bez něj ostatní mody nefungují.",
                    "Watches the Mods folder and loads mods while the game runs. Other mods don't work without it."),
                IconGlyph = "",
                Tags = new List<string> { T("Za běhu", "Live"), T("Bez restartu", "No restart") },
                Locked = true,
                Enabled = true
            });
            foreach (var mod in Hub.ListMods(_gameDir))
            {
                var m = mod.Manifest;
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(m.Version)) meta.Add(T($"verze {m.Version}", $"version {m.Version}"));
                if (!string.IsNullOrEmpty(m.Author)) meta.Add(m.Author);
                if (mod.SizeOnDisk > 50L * 1024 * 1024) meta.Add(FormatSize(mod.SizeOnDisk));
                var card = new ModCard
                {
                    Mod = mod,
                    Id = mod.Id,
                    Name = m.LocalName ?? mod.Id,
                    Subtitle = meta.Count > 0 ? string.Join("  ·  ", meta) : "mod",
                    Description = m.LocalDescription ?? "",
                    IconGlyph = Glyph(m.Icon, ""),
                    Tags = m.LocalTags ?? new List<string>(),
                    Locked = false,
                    Enabled = mod.Enabled
                };
                foreach (var s in (m.Settings ?? new List<ModSetting>()).Where(s => s?.File != null && s.Key != null && s.Options?.Count > 0))
                {
                    var current = Hub.ReadSetting(_gameDir, s);
                    var row = new SettingRow { Card = card, Setting = s, Label = s.LocalName ?? s.Key };
                    row.Choices = s.Options.Select(o => new SettingChoice
                    {
                        Row = row,
                        Option = o,
                        Name = o.LocalName ?? o.Value,
                        Hint = o.LocalHint,
                        Selected = string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)
                    }).ToList();
                    card.Settings.Add(row);
                }
                _cards.Add(card);
            }
            _cards.Add(new AddCard());
            RefreshStatuses();
        }

        private void RefreshStatuses()
        {
            if (_gameDir == null) return;
            var running = Hub.IsGameRunning(_gameDir);
            var outdated = Hub.BaseNeedsInstall(_gameDir);
            // Before the base is converted, the old loader loads mods itself and there is no manager status.
            var status = running && !outdated ? Hub.ReadManagerStatus(_gameDir) : null;
            BaseOutdatedBar.Visibility = outdated ? Visibility.Visible : Visibility.Collapsed;
            BaseOutdatedText.Text = running
                ? T("Mody se zatím načítají postaru (bez změn za běhu). Pro Mod Manager na chvilku zavři hru a aktualizuj základ.",
                    "Mods still load the old way (no live changes). To get the Mod Manager, close the game for a moment and update the base.")
                : T("Základ je potřeba aktualizovat, aby šly mody měnit za běhu.", "The base needs an update so mods can be changed while the game runs.");
            GameDot.Fill = (Brush)FindResource(running ? "Success" : "TrackOff");
            GameStatusText.Text = !running
                ? T("Hra neběží · zapnuté mody se načtou při spuštění", "Game not running · enabled mods load when it starts")
                : outdated
                    ? T("Hra běží · změny modů se projeví po restartu hry", "Game running · mod changes apply after a game restart")
                    : T("Hra běží · změny se projeví hned, bez restartu", "Game running · changes apply instantly, no restart");
            LaunchButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

            foreach (var card in _cards.OfType<ModCard>())
            {
                if (card.Locked)
                {
                    card.Status = outdated ? ModStatus.Off : running && status != null ? ModStatus.Running : ModStatus.On;
                    card.StatusText = outdated ? T("Čeká na aktualizaci základu", "Waiting for a base update")
                        : running && status != null ? T("Běží ve hře", "Running in game")
                        : T("Nainstalováno", "Installed");
                    continue;
                }
                if (!card.Enabled)
                {
                    card.Status = ModStatus.Off;
                    card.StatusText = T("Vypnuto", "Off");
                }
                else if (running && status?.Loaded?.Any(l => l.Id.Equals(card.Id, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    card.Status = ModStatus.Running;
                    card.StatusText = T("Běží ve hře", "Running in game");
                }
                else if (running && status?.Errors?.Any(x => x.Equals(card.Id, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    card.Status = ModStatus.Error;
                    card.StatusText = T("Chyba při načtení", "Failed to load");
                }
                else
                {
                    card.Status = ModStatus.On;
                    card.StatusText = running && status != null ? T("Načítá se…", "Loading…") : T("Zapnuto", "On");
                }
            }
        }

        private void OnToggleMod(object sender, RoutedEventArgs e)
        {
            if (!((sender as CheckBox)?.Tag is ModCard card)) return;
            var enable = (sender as CheckBox).IsChecked == true;
            try
            {
                Hub.SetEnabled(card.Mod, enable);
                card.Enabled = enable;
                card.Mod.Enabled = enable;
                RefreshStatuses();
                var live = Hub.IsGameRunning(_gameDir);
                ShowToast(enable
                    ? T($"{card.Name} zapnut{(live ? ", hra ho načítá" : "")}", $"{card.Name} turned on{(live ? ", the game is loading it" : "")}")
                    : T($"{card.Name} vypnut{(live ? " i ve hře" : "")}", $"{card.Name} turned off{(live ? ", in the game too" : "")}"), enable ? Tone.Ok : Tone.Info);
            }
            catch (Exception ex)
            {
                (sender as CheckBox).IsChecked = card.Enabled;
                ShowError(ex);
            }
        }

        private void OnSettingChoice(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is SettingChoice choice) || choice.Selected) return;
            var row = choice.Row;
            try
            {
                Hub.WriteSetting(_gameDir, row.Card.Mod, row.Setting, choice.Option);
                foreach (var c in row.Choices) c.Selected = c == choice;
                var live = Hub.IsGameRunning(_gameDir);
                ShowToast(T($"{row.Label}: {choice.Name}{(live ? ", projeví se po restartu hry" : "")}",
                            $"{row.Label}: {choice.Name}{(live ? ", takes effect after restarting the game" : "")}"), Tone.Ok);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void OnRemoveMod(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is ModCard card)) return;
            var big = card.Mod.SizeOnDisk > 50L * 1024 * 1024;
            var live = Hub.IsGameRunning(_gameDir);
            ShowDialog(T($"Odebrat {card.Name}?", $"Remove {card.Name}?"),
                T($"Mod se smaže ze hry{(live ? " a hned se vypne" : "")}.{(big ? $" Uvolní se {FormatSize(card.Mod.SizeOnDisk)}." : "")} Kdykoli ho můžeš nainstalovat znovu.",
                  $"The mod will be deleted from the game{(live ? " and turned off right away" : "")}.{(big ? $" This frees {FormatSize(card.Mod.SizeOnDisk)}." : "")} You can reinstall it anytime."),
                T("Odebrat", "Remove"), Tone.Danger, () =>
                {
                    try
                    {
                        Hub.Remove(card.Mod);
                        RebuildCards();
                        ShowToast(T($"{card.Name} odebrán", $"{card.Name} removed"), Tone.Info);
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex);
                    }
                });
        }

        private void OnAddModClick(object sender, RoutedEventArgs e) => PickPackage();

        private void OnAddModClick(object sender, MouseButtonEventArgs e) => PickPackage();

        private void PickPackage()
        {
            if (_busy) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Vyber mod pro Scam With Your Friends", "Choose a mod for Scam With Your Friends"),
                Filter = T("Mody", "Mods") + " (*.swyfmod, *.zip, *.dll)|*.swyfmod;*.zip;*.dll",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true) OpenPackage(dialog.FileName);
        }

        private void OnUninstallAll(object sender, RoutedEventArgs e)
        {
            ShowDialog(T("Odinstalovat všechno?", "Uninstall everything?"),
                T("Smaže se mod loader, Mod Manager, jeho nastavení a všechny mody včetně stažených modelů. Hra pak poběží úplně bez modů.",
                    "This deletes the mod loader, the Mod Manager, its settings and all mods, including downloaded models. The game will then run with no mods at all."),
                T("Odinstalovat", "Uninstall"), Tone.Danger, () =>
                {
                    if (Hub.IsGameRunning(_gameDir))
                    {
                        ShowDialog(T("Zavři hru", "Close the game"), T("Odinstalovat se dá, jen když hra neběží.", "You can only uninstall while the game isn't running."),
                            T("Rozumím", "Got it"), Tone.Warning);
                        return;
                    }
                    try
                    {
                        Hub.UninstallAll(_gameDir);
                        _statusTimer.Stop();
                        OpenBase();
                        ShowToast(T("Všechno odinstalováno. Hra poběží bez modů.", "Everything uninstalled. The game will run without mods."), Tone.Info);
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex);
                    }
                });
        }

        private void OnLaunch(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamLocator.AppId}") { UseShellExecute = true });
            ShowToast(T("Spouštím hru přes Steam…", "Launching the game through Steam…"), Tone.Info);
        }

        // ================= Installing a .swyfmod =================

        private void OpenPackage(string path)
        {
            if (_page != Page.Library)
            {
                _pendingPackage = path; // after the base is installed
                return;
            }
            ModManifest m;
            try
            {
                path = Hub.ResolvePackagePath(path);
                m = Hub.ReadPackage(path);
            }
            catch (Exception ex)
            {
                ShowDialog(T("Tohle není mod", "This isn't a mod"), ex.Message, T("Rozumím", "Got it"), Tone.Warning);
                return;
            }

            var existing = Hub.ListMods(_gameDir).FirstOrDefault(x => x.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
            _packagePath = path;
            _packageManifest = m;
            _packageExisting = existing;
            PkgIcon.Text = Glyph(m.Icon, "");
            PkgDownloads.ItemsSource = null;
            RenderPackageTexts();
            PkgDownloadsPanel.Visibility = (m.Downloads?.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
            PkgChoice.Visibility = Visibility.Visible;
            PkgProgressPanel.Visibility = Visibility.Collapsed;
            ShowLayer(InstallLayer);
        }

        /// <summary>Texts of the install dialog; download choices keep their switches when redone.</summary>
        private void RenderPackageTexts()
        {
            var m = _packageManifest;
            var existing = _packageExisting;
            if (m == null) return;
            InstallHeading.Text = existing != null ? T("AKTUALIZACE MODU", "MOD UPDATE") : T("NOVÝ MOD", "NEW MOD");
            PkgName.Text = m.LocalName ?? m.Id;
            var version = existing?.Manifest.Version != null && m.Version != null && existing.Manifest.Version != m.Version
                ? T($"verze {existing.Manifest.Version} → {m.Version}", $"version {existing.Manifest.Version} → {m.Version}")
                : m.Version != null ? T($"verze {m.Version}", $"version {m.Version}") : null;
            PkgMeta.Text = string.Join("  ·  ", new[] { version, m.Author }.Where(s => !string.IsNullOrEmpty(s)));
            PkgDescription.Text = m.LocalDescription ?? "";
            PkgInstallText.Text = existing != null ? T("Aktualizovat", "Update") : T("Nainstalovat", "Install");

            var previous = (PkgDownloads.ItemsSource as IEnumerable<DownloadChoice>)?.ToDictionary(c => c.Download, c => c.Selected);
            PkgDownloads.ItemsSource = (m.Downloads ?? new List<ModDownload>()).Select(d =>
            {
                var have = Hub.IsDownloaded(_gameDir, m, d);
                return new DownloadChoice
                {
                    Download = d,
                    SizeText = have ? T("už staženo", "already downloaded") : FormatSize(d.Size),
                    Selected = previous != null && previous.TryGetValue(d, out var sel) ? sel : d.Default || have
                };
            }).ToList();
        }

        private void OnPackageCancel(object sender, RoutedEventArgs e) => InstallLayer.Visibility = Visibility.Collapsed;

        private async void OnPackageInstall(object sender, RoutedEventArgs e)
        {
            var m = _packageManifest;
            var path = _packagePath;
            var chosen = (PkgDownloads.ItemsSource as IEnumerable<DownloadChoice>)?.Where(c => c.Selected).Select(c => c.Download.Id).ToList() ?? new List<string>();
            var dir = _gameDir;

            _busy = true;
            PkgChoice.Visibility = Visibility.Collapsed;
            PkgProgressPanel.Visibility = Visibility.Visible;
            PkgProgress.Value = 0;
            PkgProgressLabel.Text = T("Připravuju…", "Preparing…");
            PkgProgressDetail.Text = "";
            var progress = new Progress<TransferProgress>(p =>
            {
                PkgProgressLabel.Text = p.Label;
                var v = p.Total > 0 ? p.Done / (double)p.Total : 0;
                Animate(PkgProgress, RangeBase.ValueProperty, v, 150);
                PkgProgressDetail.Text = p.BytesPerSecond > 0
                    ? T($"{FormatSize(p.Done)} z {FormatSize(p.Total)}", $"{FormatSize(p.Done)} of {FormatSize(p.Total)}") + $"  ·  {FormatSize((long)p.BytesPerSecond)}/s"
                    : T($"{Math.Round(v * 100)} %", $"{Math.Round(v * 100)}%");
            });
            try
            {
                await Task.Run(() => Hub.InstallPackageAsync(dir, path, m, chosen, progress));
                InstallLayer.Visibility = Visibility.Collapsed;
                _busy = false;
                RebuildCards();
                var name = m.LocalName ?? m.Id;
                ShowToast(Hub.IsGameRunning(dir)
                    ? T($"{name} nainstalován, hra ho právě načítá", $"{name} installed, the game is loading it now")
                    : T($"{name} nainstalován", $"{name} installed"), Tone.Ok);
            }
            catch (Exception ex)
            {
                InstallLayer.Visibility = Visibility.Collapsed;
                _busy = false;
                RebuildCards();
                ShowError(ex);
            }
        }

        // ================= Drag & drop =================

        private static string DroppedPackage(DragEventArgs e) =>
            (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f =>
                f.EndsWith(".swyfmod", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            var ok = DroppedPackage(e) != null && !_busy;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DropLayer.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedPackage(e) != null && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e) => DropLayer.Visibility = Visibility.Collapsed;

        private void OnDrop(object sender, DragEventArgs e)
        {
            DropLayer.Visibility = Visibility.Collapsed;
            var file = DroppedPackage(e);
            if (file == null || _busy) return;
            if (_page == Page.Library) OpenPackage(file);
            else
            {
                _pendingPackage = file;
                ShowToast(T("Mod nainstaluju hned po základu.", "I'll install the mod right after the base."), Tone.Info);
            }
        }

        // ================= Dialog & toast =================

        private enum Tone { Ok, Info, Warning, Danger }

        private void ShowDialog(string title, string text, string okText, Tone tone, Action onOk = null)
        {
            DialogTitle.Text = title;
            DialogText.Text = text;
            DialogOk.Content = okText;
            var danger = tone == Tone.Danger;
            DialogIcon.Text = danger ? "" : "";
            DialogIcon.Foreground = (Brush)FindResource(danger ? "Danger" : "Accent");
            DialogIconTile.Background = new SolidColorBrush(danger ? Color.FromArgb(0x26, 0xFF, 0x6B, 0x6B) : Color.FromArgb(0x22, 0xFF, 0xC2, 0x3D));
            DialogCancel.Visibility = onOk != null ? Visibility.Visible : Visibility.Collapsed;
            DialogOk.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
            DialogOk.Padding = new Thickness(20, 9, 20, 9);
            _dialogOk = onOk;
            ShowLayer(DialogLayer);
        }

        private void ShowError(Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerException != null) ex = agg.InnerException;
            if (ex is UnauthorizedAccessException)
                ShowDialog(T("Nemám přístup do složky hry", "No access to the game folder"),
                    T("Zavři hub a spusť ho jako správce: pravý klik na SWYF-Hub.exe → Spustit jako správce.",
                        "Close the hub and run it as administrator: right-click SWYF-Hub.exe → Run as administrator."), T("Rozumím", "Got it"), Tone.Warning);
            else if (ex is System.Net.Http.HttpRequestException)
                ShowDialog(T("Stahování selhalo", "Download failed"),
                    T("Zkontroluj připojení k internetu a zkus to znovu.", "Check your internet connection and try again.") + "\n\n" + ex.Message, T("Rozumím", "Got it"), Tone.Warning);
            else if (ex is IOException)
                ShowDialog(T("Soubor nejde zapsat", "Can't write a file"),
                    T("Asi ho něco používá. Zkus to znovu, případně zavři hru.", "Something is probably using it. Try again, or close the game.") + "\n\n" + ex.Message,
                    T("Rozumím", "Got it"), Tone.Warning);
            else
                ShowDialog(T("Něco se nepovedlo", "Something went wrong"), ex.Message, T("Rozumím", "Got it"), Tone.Warning);
        }

        private void OnDialogOk(object sender, RoutedEventArgs e)
        {
            DialogLayer.Visibility = Visibility.Collapsed;
            var ok = _dialogOk;
            _dialogOk = null;
            ok?.Invoke();
        }

        private void OnDialogCancel(object sender, RoutedEventArgs e) => DialogLayer.Visibility = Visibility.Collapsed;

        private void ShowLayer(FrameworkElement layer)
        {
            layer.Opacity = 0;
            layer.Visibility = Visibility.Visible;
            Animate(layer, OpacityProperty, 1, 160);
        }

        private int _toastId;

        private async void ShowToast(string text, Tone tone)
        {
            var id = ++_toastId;
            ToastText.Text = text;
            ToastIcon.Text = tone == Tone.Ok ? "" : "";
            ToastIcon.Foreground = (Brush)FindResource(tone == Tone.Ok ? "Success" : "Accent");
            var shift = (TranslateTransform)Toast.RenderTransform;
            shift.Y = 12;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Animate(Toast, OpacityProperty, 1, 200);
            await Task.Delay(3200);
            if (id == _toastId) Animate(Toast, OpacityProperty, 0, 300);
        }

        // ================= Pages, stepper, artwork =================

        private void GoTo(Page page)
        {
            var pages = new Dictionary<Page, FrameworkElement> { { Page.Find, PageFind }, { Page.Base, PageBase }, { Page.Library, PageLibrary } };
            var forward = page >= _page;
            foreach (var kv in pages) kv.Value.Visibility = kv.Key == page ? Visibility.Visible : Visibility.Collapsed;
            var target = pages[page];
            target.Opacity = 0;
            var shift = (TranslateTransform)target.RenderTransform;
            shift.X = forward ? 28 : -28;
            Animate(target, OpacityProperty, 1, 260);
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _page = page;
            if (page != Page.Library) _statusTimer.Stop();
            UpdateStepper();
        }

        private void BuildStepper()
        {
            for (var i = 0; i < StepNames.Length; i++)
            {
                var number = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 12, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1.5), Child = number };
                var label = new TextBlock { Text = StepNames[i], FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var item = new StackPanel { Orientation = Orientation.Horizontal };
                item.Children.Add(circle);
                item.Children.Add(label);
                Stepper.Children.Add(item);
                Rectangle line = null;
                if (i < StepNames.Length - 1)
                {
                    line = new Rectangle { Width = 56, Height = 2, RadiusX = 1, RadiusY = 1, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                    Stepper.Children.Add(line);
                }
                _steps.Add((circle, number, label, line));
            }
        }

        private void UpdateStepper()
        {
            var current = (int)_page;
            var accent = (Brush)FindResource("Accent");
            for (var i = 0; i < _steps.Count; i++)
            {
                var (circle, number, label, line) = _steps[i];
                var done = i < current || (_page == Page.Library && i == current);
                if (done && i < current)
                {
                    circle.Background = accent;
                    circle.BorderBrush = accent;
                    number.Text = "";
                    number.FontFamily = (FontFamily)FindResource("IconFont");
                    number.FontSize = 11;
                    number.Foreground = (Brush)FindResource("AccentDark");
                    label.Foreground = (Brush)FindResource("Text");
                }
                else
                {
                    circle.Background = i == current ? new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xC2, 0x3D)) : Brushes.Transparent;
                    circle.BorderBrush = i == current ? accent : (Brush)FindResource("TrackOff");
                    number.Text = (i + 1).ToString(CultureInfo.InvariantCulture);
                    number.FontFamily = (FontFamily)FindResource("UiFont");
                    number.FontSize = 12;
                    number.Foreground = i == current ? accent : (Brush)FindResource("TextMuted");
                    label.Foreground = (Brush)FindResource(i == current ? "Text" : "TextMuted");
                }
                label.FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
                if (line != null) line.Fill = i < current ? accent : (Brush)FindResource("SurfacePressed");
            }
        }

        private void LoadArtwork()
        {
            HeroImage.Source = LoadImage(SteamLocator.FindArtwork("library_hero.jpg"));
            var logo = LoadImage(SteamLocator.FindArtwork("logo.png"));
            LogoImage.Source = logo;
            LogoImage.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
            LogoFallback.Visibility = logo != null ? Visibility.Collapsed : Visibility.Visible;
        }

        private static BitmapImage LoadImage(string path)
        {
            if (path == null) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }

        // ================= Helpers =================

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            var u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(u >= 2 ? "0.0" : "0", Lang.Culture) + " " + units[u];
        }

        private static string Glyph(string hex, string fallback) =>
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? ((char)code).ToString() : fallback;

        private static void Animate(UIElement element, DependencyProperty property, double to, int ms) =>
            element.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        private static void Pop(FrameworkElement element)
        {
            var scale = new ScaleTransform(0.6, 0.6);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            var anim = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
